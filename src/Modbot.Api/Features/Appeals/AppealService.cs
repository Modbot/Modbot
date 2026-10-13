using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Cases;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Appeals;

/// <summary>Why an appeal could not be logged or decided, with the status to answer with.</summary>
public sealed class AppealRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Appeals against bans: log one, answer it, and read them back (batch 3, step G).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reading is the profile permission, writing is the unban permission.</strong> Case files
/// are read under <see cref="ModbotPermissions.ViewProfile"/>, and an appeal hangs off a person and
/// usually off a case file. Logging and answering one is the work of the people who can lift a ban,
/// <see cref="ModbotPermissions.Unban"/>; no permission of its own was needed.
/// </para>
/// <para>
/// <strong>An appeal never acts.</strong> Upholding one writes down that it was upheld. The ban is
/// lifted, if it is, by an unban, the way it is today. Nothing here reaches VRChat or Discord.
/// </para>
/// <para>
/// The row and the fact that describes the write commit together, as a watch's do.
/// </para>
/// </remarks>
public sealed class AppealService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>The earliest year the list and the report take. Modbot did not exist before it.</summary>
    public const int FirstYear = 2000;

    public const string Pending = "pending";
    public const string Upheld = "upheld";
    public const string TurnedDown = "turned-down";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter? _facts;
    private readonly EventPartitionMaintainer? _partitions;

    public AppealService(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter? facts = null,
        EventPartitionMaintainer? partitions = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
    }

    // ── Logging ─────────────────────────────────────────────────────────────────────────────

    public async Task<AppealView> LogAsync(LogAppealRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.Has(ModbotPermissions.Unban))
            throw new AppealRefused(403, "You do not have permission to log appeals.");

        // Taken as sent: a legacy VRChat id follows no structure (foundation §3.1.1).
        var userId = (request.UserId ?? string.Empty).Trim();

        if (userId.Length == 0)
            throw new AppealRefused(400, "Pick a person first: Modbot was given no id.");

        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var receivedOn = request.ReceivedOn ?? today;

        if (receivedOn > today)
            throw new AppealRefused(400, "The day it was received cannot be a day to come.");

        if (receivedOn.Year < FirstYear)
            throw new AppealRefused(400, "That day is too far back.");

        var note = (request.Note ?? string.Empty).Trim();

        if (note.Length > Appeal.MaxNoteLength)
            throw new AppealRefused(400, $"That note is too long (at most {Appeal.MaxNoteLength} characters).");

        DateTimeOffset? caseFileDate = null;

        if (request.CaseFileId is { } caseFileId)
        {
            var file = await _db.CaseFiles.AsNoTracking()
                .Where(c => c.Id == caseFileId)
                .Select(c => new { c.UserId, c.BannedAt, c.CreatedAt })
                .FirstOrDefaultAsync(ct);

            if (file is null)
                throw new AppealRefused(404, "That case file does not exist.");

            if (file.UserId != userId)
                throw new AppealRefused(400, "That case file is about somebody else.");

            caseFileDate = file.BannedAt ?? file.CreatedAt;
        }

        var (facts, partitions) = Writer();

        await partitions.EnsureForAsync(now, ct);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var appeal = new Appeal
        {
            UserId = userId,
            ReceivedOn = receivedOn,
            CaseFileId = request.CaseFileId,
            Note = note,
            LoggedAt = now,
            LoggedByUserId = caller.UserId,
            LoggedByUsername = caller.Username,
        };

        _db.Appeals.Add(appeal);
        await _db.SaveChangesAsync(ct);

        var data = new JsonObject
        {
            ["appealId"] = appeal.Id.ToString(),
            ["receivedOn"] = receivedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["actorDisplayName"] = caller.Username,

            // Under the key every reader of a timeline already looks in, as a note's text is.
            ["description"] = note.Length > 0 ? note : "Appeal received",
        };

        if (request.CaseFileId is { } linked)
            data["caseFileId"] = linked.ToString();

        await facts.WriteAsync(FactFor(FactType.AppealLogged, userId, caller, now, data), ct);

        await transaction.CommitAsync(ct);

        return Shape(appeal, await NameAsync(userId, ct), caseFileDate, caller);
    }

    // ── Deciding ────────────────────────────────────────────────────────────────────────────

    public async Task<AppealView> DecideAsync(Guid id, DecideAppealRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.Has(ModbotPermissions.Unban))
            throw new AppealRefused(403, "You do not have permission to decide appeals.");

        var outcome = request.Outcome switch
        {
            Upheld => AppealOutcome.Upheld,
            TurnedDown => AppealOutcome.TurnedDown,
            _ => throw new AppealRefused(400, "Say whether it was upheld or turned down."),
        };

        var exists = await _db.Appeals.AsNoTracking().AnyAsync(a => a.Id == id, ct);

        if (!exists)
            throw new AppealRefused(404, "That appeal does not exist.");

        var (facts, partitions) = Writer();
        var now = _clock.UtcNow;

        await partitions.EnsureForAsync(now, ct);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // One statement that only matches a waiting appeal: two people deciding at the same time
        // cannot both win, because the second waits for the first to finish and then finds
        // nothing left to change. No decision fact is written for the one that loses.
        var changed = await _db.Appeals
            .Where(a => a.Id == id && a.Outcome == AppealOutcome.Pending)
            .ExecuteUpdateAsync(
                u => u
                    .SetProperty(a => a.Outcome, outcome)
                    .SetProperty(a => a.DecidedAt, now)
                    .SetProperty(a => a.DecidedByUserId, caller.UserId)
                    .SetProperty(a => a.DecidedByUsername, caller.Username),
                ct);

        if (changed == 0)
            throw new AppealRefused(409, "That appeal was already decided.");

        var appeal = await _db.Appeals.AsNoTracking().FirstAsync(a => a.Id == id, ct);

        var word = outcome == AppealOutcome.Upheld ? Upheld : TurnedDown;

        var data = new JsonObject
        {
            ["appealId"] = appeal.Id.ToString(),
            ["outcome"] = word,
            ["actorDisplayName"] = caller.Username,
            ["description"] = outcome == AppealOutcome.Upheld ? "Appeal upheld" : "Appeal turned down",
        };

        await facts.WriteAsync(FactFor(FactType.AppealDecided, appeal.UserId, caller, now, data), ct);

        await transaction.CommitAsync(ct);

        var dates = await CaseFileDatesAsync([appeal], ct);

        return Shape(appeal, await NameAsync(appeal.UserId, ct), appeal.CaseFileId is { } c ? dates.GetValueOrDefault(c) : null, caller);
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appeals, newest received first, narrowed to one person, a year (by the day received), or
    /// the ones that wait or ended a given way.
    /// </summary>
    public async Task<AppealList> ListAsync(
        string? userId,
        int? year,
        string? outcome,
        int limit,
        Caller caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var query = _db.Appeals.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var id = userId.Trim();
            query = query.Where(a => a.UserId == id);
        }

        if (year is { } y)
        {
            CheckYear(y, _clock.UtcNow);
            var from = new DateOnly(y, 1, 1);
            var to = new DateOnly(y, 12, 31);
            query = query.Where(a => a.ReceivedOn >= from && a.ReceivedOn <= to);
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var wanted = outcome switch
            {
                Pending => AppealOutcome.Pending,
                Upheld => AppealOutcome.Upheld,
                TurnedDown => AppealOutcome.TurnedDown,
                _ => throw new AppealRefused(400, "`outcome` must be pending, upheld or turned-down."),
            };

            query = query.Where(a => a.Outcome == wanted);
        }

        var total = await query.CountAsync(ct);
        var waiting = await query.CountAsync(a => a.Outcome == AppealOutcome.Pending, ct);

        var rows = await query
            .OrderByDescending(a => a.ReceivedOn)
            .ThenByDescending(a => a.LoggedAt)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(ct);

        var names = await NamesAsync(rows.Select(a => a.UserId).Distinct().ToList(), ct);
        var dates = await CaseFileDatesAsync(rows, ct);

        return new AppealList(
            [.. rows.Select(a => Shape(
                a,
                names.GetValueOrDefault(a.UserId),
                a.CaseFileId is { } c ? dates.GetValueOrDefault(c) : null,
                caller))],
            total,
            waiting,
            caller.Has(ModbotPermissions.Unban),
            _clock.UtcNow);
    }

    /// <summary>A year the list and the report take: from <see cref="FirstYear"/> to this one.</summary>
    public static void CheckYear(int year, DateTimeOffset now)
    {
        if (year < FirstYear || year > now.UtcDateTime.Year)
            throw new AppealRefused(400, "Pick a year from the list.");
    }

    // ── Pieces ──────────────────────────────────────────────────────────────────────────────

    private static FactRecord FactFor(string type, string userId, Caller caller, DateTimeOffset now, JsonObject data) => new()
    {
        Type = type,
        OccurredAt = now,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = userId,
        ActorPlatform = FactPlatform.Modbot,
        ActorId = caller.UserId.ToString(),
        Source = FactSource.Manual,
        Data = data,
    };

    private static AppealView Shape(Appeal a, string? name, DateTimeOffset? caseFileDate, Caller caller) => new(
        a.Id,
        a.UserId,
        name,
        a.ReceivedOn,
        OutcomeWord(a.Outcome),
        a.CaseFileId,
        caseFileDate,
        a.Note,
        a.LoggedAt,
        a.LoggedByUsername,
        a.DecidedAt,
        a.DecidedByUsername,
        a.Outcome == AppealOutcome.Pending && caller.Has(ModbotPermissions.Unban));

    public static string OutcomeWord(AppealOutcome outcome) => outcome switch
    {
        AppealOutcome.Upheld => Upheld,
        AppealOutcome.TurnedDown => TurnedDown,
        _ => Pending,
    };

    private async Task<string?> NameAsync(string userId, CancellationToken ct)
        => (await NamesAsync([userId], ct)).GetValueOrDefault(userId);

    private async Task<Dictionary<string, string>> NamesAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var found = await _db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null)
            .Select(u => new { u.UserId, u.DisplayName })
            .ToListAsync(ct);

        return found.ToDictionary(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal);
    }

    /// <summary>When each linked ban happened, else when its case file was written.</summary>
    private async Task<Dictionary<Guid, DateTimeOffset>> CaseFileDatesAsync(IEnumerable<Appeal> appeals, CancellationToken ct)
    {
        var ids = appeals.Where(a => a.CaseFileId is not null).Select(a => a.CaseFileId!.Value).Distinct().ToList();

        if (ids.Count == 0)
            return [];

        var files = await _db.CaseFiles.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.BannedAt, c.CreatedAt })
            .ToListAsync(ct);

        return files.ToDictionary(c => c.Id, c => c.BannedAt ?? c.CreatedAt);
    }

    private (IFactWriter Facts, EventPartitionMaintainer Partitions) Writer()
        => _facts is null || _partitions is null
            ? throw new AppealRefused(503, "This deployment is not set up to log appeals.")
            : (_facts, _partitions);
}
