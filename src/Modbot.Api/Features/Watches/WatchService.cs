using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Cases;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Watches;

/// <summary>Why a watch could not be started, stopped or followed up, with the status to answer with.</summary>
public sealed class WatchRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Watching a person: start one, stop one, follow up on one, and read them back (watching a person
/// design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reading is the audit log's permission, writing is the notes permission.</strong> A watch
/// is a moderator's deliberate word about somebody, the same kind of thing a note is, and its
/// history is facts in the moderation log: <c>ViewAuditLog</c> already decides who may read those.
/// Starting one is <see cref="ModbotPermissions.WriteNotes"/>, and stopping or following up is open
/// to whoever started it as well, the way taking back a note is.
/// </para>
/// <para>
/// <strong>A watch never acts.</strong> Nothing here calls VRChat or Discord.
/// </para>
/// </remarks>
public sealed class WatchService
{
    /// <summary>How far ahead an end day or a follow-up day may be set.</summary>
    public static readonly TimeSpan FurthestAhead = TimeSpan.FromDays(3650);

    /// <summary>
    /// How far back a follow-up day may be. A day, so "check back today" still works for somebody
    /// whose browser starts today at midnight.
    /// </summary>
    public static readonly TimeSpan FollowUpBackAtMost = TimeSpan.FromDays(1);

    /// <summary>How many of a person's watches their popup reads.</summary>
    public const int PersonLimit = 20;

    /// <summary>How many standing watches one list read returns.</summary>
    public const int ListLimit = 500;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter? _facts;
    private readonly EventPartitionMaintainer? _partitions;

    public WatchService(
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

    // ── Starting ────────────────────────────────────────────────────────────────────────────

    public async Task<WatchView> StartAsync(StartWatchRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.Has(ModbotPermissions.WriteNotes))
            throw new WatchRefused(403, "You do not have permission to watch people.");

        // Taken as sent: a legacy VRChat id follows no structure (foundation §3.1.1).
        var userId = (request.UserId ?? string.Empty).Trim();

        if (userId.Length == 0)
            throw new WatchRefused(400, "Pick a person first: Modbot was given no id.");

        var platform = PlatformOf(request.Platform);
        var reason = (request.Reason ?? string.Empty).Trim();

        if (reason.Length == 0)
            throw new WatchRefused(400, "A watch needs a reason.");

        if (reason.Length > PersonWatch.MaxReasonLength)
            throw new WatchRefused(400, $"That reason is too long (at most {PersonWatch.MaxReasonLength} characters).");

        var now = _clock.UtcNow;

        if (request.EndsAt is { } endsAt && (endsAt <= now || endsAt > now + FurthestAhead))
            throw new WatchRefused(400, "The end day must be in the future.");

        if (request.FollowUpAt is { } followUpAt)
        {
            if (followUpAt < now - FollowUpBackAtMost || followUpAt > now + FurthestAhead)
                throw new WatchRefused(400, "The follow-up day must be today or later.");

            if (request.EndsAt is { } ends && followUpAt > ends)
                throw new WatchRefused(400, "The follow-up day is after the watch ends.");
        }

        var (facts, partitions) = Writer();

        var standing = await _db.PersonWatches
            .FirstOrDefaultAsync(w => w.SubjectPlatform == platform && w.SubjectId == userId && w.EndedAt == null, ct);

        if (standing is not null && standing.StandsAt(now))
            throw new WatchRefused(409, "Somebody is already watching this person.");

        await partitions.EnsureForAsync(now, ct);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // One that ran out and has not been closed yet is closed here, so the new one can stand.
        if (standing is not null)
            await EndAsync(standing, standing.EndsAt ?? now, by: null, facts, ct);

        var watch = new PersonWatch
        {
            SubjectPlatform = platform,
            SubjectId = userId,
            Reason = reason,
            SetByUserId = caller.UserId,
            SetByUsername = caller.Username,
            SetAt = now,
            EndsAt = request.EndsAt?.ToUniversalTime(),
            FollowUpAt = request.FollowUpAt?.ToUniversalTime(),
        };

        _db.PersonWatches.Add(watch);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two moderators pressed Watch on the same person at once; the index let one through.
            throw new WatchRefused(409, "Somebody is already watching this person.");
        }

        var data = new JsonObject
        {
            ["watchId"] = watch.Id.ToString(),
            ["reason"] = reason,
            ["actorDisplayName"] = caller.Username,

            // Under the key every reader of a timeline already looks in, as a note's text is.
            ["description"] = reason,
        };

        if (watch.EndsAt is { } until)
            data["endsAt"] = until.ToString("O");

        if (watch.FollowUpAt is { } followUp)
            data["followUpAt"] = followUp.ToString("O");

        await facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.WatchStarted,
                OccurredAt = now,
                SubjectPlatform = platform,
                SubjectId = userId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = caller.UserId.ToString(),
                Source = FactSource.Manual,
                Data = data,
            },
            ct);

        await transaction.CommitAsync(ct);

        return Shape(watch, await NameAsync(platform, userId, ct), caller, now);
    }

    // ── Stopping ────────────────────────────────────────────────────────────────────────────

    public async Task<WatchView> StopAsync(Guid id, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var watch = await FindChangeableAsync(id, caller, ct);
        var now = _clock.UtcNow;
        var (facts, partitions) = Writer();

        await partitions.EnsureForAsync(now, ct);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        await EndAsync(watch, now, caller, facts, ct);

        await transaction.CommitAsync(ct);

        return Shape(watch, await NameAsync(watch.SubjectPlatform, watch.SubjectId, ct), caller, now);
    }

    // ── Following up ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Says somebody has checked on the person, which clears the follow-up day. The watch itself
    /// carries on.
    /// </summary>
    public async Task<WatchView> FollowedUpAsync(Guid id, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var watch = await FindChangeableAsync(id, caller, ct);

        if (watch.FollowUpAt is not { } due)
            throw new WatchRefused(409, "This watch has no follow-up day.");

        var now = _clock.UtcNow;
        var (facts, partitions) = Writer();

        await partitions.EnsureForAsync(now, ct);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        watch.FollowUpAt = null;
        watch.FollowUpRemindedAt = null;
        await _db.SaveChangesAsync(ct);

        await facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.WatchFollowedUp,
                OccurredAt = now,
                SubjectPlatform = watch.SubjectPlatform,
                SubjectId = watch.SubjectId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = caller.UserId.ToString(),
                Source = FactSource.Manual,
                Data = new JsonObject
                {
                    ["watchId"] = watch.Id.ToString(),
                    ["reason"] = watch.Reason,
                    ["followUpAt"] = due.ToString("O"),
                    ["actorDisplayName"] = caller.Username,
                    ["description"] = watch.Reason,
                },
            },
            ct);

        await transaction.CommitAsync(ct);

        return Shape(watch, await NameAsync(watch.SubjectPlatform, watch.SubjectId, ct), caller, now);
    }

    // ── Running out ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Closes every watch whose end day has passed, writing the end into the log. Every reader
    /// already treats them as over; this only makes the record say so.
    /// </summary>
    /// <returns>How many it closed.</returns>
    public async Task<int> EndExpiredAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var expired = await _db.PersonWatches
            .Where(w => w.EndedAt == null && w.EndsAt != null && w.EndsAt <= now)
            .OrderBy(w => w.EndsAt)
            .Take(ListLimit)
            .ToListAsync(ct);

        if (expired.Count == 0)
            return 0;

        var (facts, partitions) = Writer();
        await partitions.EnsureForAsync(now, ct);

        foreach (var watch in expired)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await EndAsync(watch, watch.EndsAt!.Value, by: null, facts, ct);
            await transaction.CommitAsync(ct);
        }

        return expired.Count;
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The watches on one person's accounts: the one that stands first, then the rest, newest
    /// first. Either id may be missing, not both.
    /// </summary>
    public async Task<PersonWatchList> ForPersonAsync(string? vrchatId, string? discordId, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var vrchat = string.IsNullOrWhiteSpace(vrchatId) ? null : vrchatId.Trim();
        var discord = string.IsNullOrWhiteSpace(discordId) ? null : discordId.Trim();

        if (vrchat is null && discord is null)
            throw new WatchRefused(400, "Say whose watches to read.");

        var now = _clock.UtcNow;

        var rows = await _db.PersonWatches.AsNoTracking()
            .Where(w => (vrchat != null && w.SubjectPlatform == FactPlatform.VRChat && w.SubjectId == vrchat)
                     || (discord != null && w.SubjectPlatform == FactPlatform.Discord && w.SubjectId == discord))
            .OrderByDescending(w => w.SetAt)
            .Take(PersonLimit)
            .ToListAsync(ct);

        var names = await NamesAsync(rows, ct);

        var views = rows
            .Select(w => Shape(w, names.GetValueOrDefault((w.SubjectPlatform, w.SubjectId)), caller, now))
            .OrderByDescending(v => v.Standing)
            .ThenByDescending(v => v.SetAt)
            .ToList();

        return new PersonWatchList(views, caller.Has(ModbotPermissions.WriteNotes), now);
    }

    /// <summary>
    /// Every watch that stands, or only those with a follow-up due, the longest overdue first.
    /// </summary>
    public async Task<WatchList> ListAsync(bool dueOnly, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var now = _clock.UtcNow;

        var query = _db.PersonWatches.AsNoTracking()
            .Where(w => w.EndedAt == null && (w.EndsAt == null || w.EndsAt > now));

        if (dueOnly)
            query = query.Where(w => w.FollowUpAt != null && w.FollowUpAt <= now);

        var rows = await query
            .OrderBy(w => w.FollowUpAt == null)
            .ThenBy(w => w.FollowUpAt)
            .ThenByDescending(w => w.SetAt)
            .Take(ListLimit)
            .ToListAsync(ct);

        var names = await NamesAsync(rows, ct);

        return new WatchList(
            rows.Select(w => Shape(w, names.GetValueOrDefault((w.SubjectPlatform, w.SubjectId)), caller, now)).ToList(),
            now);
    }

    // ── Shared ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Ends one watch and writes it into the log. The caller holds the transaction.</summary>
    /// <param name="by">Who stopped it. Null when it ran out on its own.</param>
    private async Task EndAsync(PersonWatch watch, DateTimeOffset at, Caller? by, IFactWriter facts, CancellationToken ct)
    {
        watch.EndedAt = at;
        watch.EndedByUserId = by?.UserId;
        watch.EndedByUsername = by?.Username;
        await _db.SaveChangesAsync(ct);

        var data = new JsonObject
        {
            ["watchId"] = watch.Id.ToString(),

            // The reason again, so one entry still answers what the watch was for.
            ["reason"] = watch.Reason,
            ["description"] = watch.Reason,
        };

        if (by is not null)
            data["actorDisplayName"] = by.Username;
        else
            data["ended"] = "expired";

        await facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.WatchEnded,
                OccurredAt = at,
                SubjectPlatform = watch.SubjectPlatform,
                SubjectId = watch.SubjectId,
                ActorPlatform = by is null ? null : FactPlatform.Modbot,
                ActorId = by?.UserId.ToString(),
                Source = by is null ? FactSource.Modbot : FactSource.Manual,
                Data = data,
            },
            ct);
    }

    /// <summary>A standing watch the caller may change, or a refusal.</summary>
    private async Task<PersonWatch> FindChangeableAsync(Guid id, Caller caller, CancellationToken ct)
    {
        var watch = await _db.PersonWatches.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new WatchRefused(404, "No such watch.");

        if (!caller.Has(ModbotPermissions.WriteNotes) && watch.SetByUserId != caller.UserId)
            throw new WatchRefused(403, "You do not have permission to change this watch.");

        if (!watch.StandsAt(_clock.UtcNow))
            throw new WatchRefused(409, "This watch has already ended.");

        return watch;
    }

    private static WatchView Shape(PersonWatch watch, string? name, Caller caller, DateTimeOffset now)
    {
        var standing = watch.StandsAt(now);

        // One that ran out but has not been closed yet ended on its end day.
        var endedAt = watch.EndedAt ?? (standing ? null : watch.EndsAt);

        return new WatchView(
            watch.Id,
            watch.SubjectPlatform.ToString(),
            watch.SubjectId,
            name,
            watch.Reason,
            watch.SetByUsername,
            watch.SetAt,
            watch.EndsAt,
            watch.FollowUpAt,
            FollowUpDue: standing && watch.FollowUpAt is { } due && due <= now,
            standing,
            endedAt,
            watch.EndedByUsername,
            CanChange: standing && (caller.Has(ModbotPermissions.WriteNotes) || watch.SetByUserId == caller.UserId));
    }

    private async Task<string?> NameAsync(FactPlatform platform, string id, CancellationToken ct)
        => (await NamesAsync([new PersonWatch { SubjectPlatform = platform, SubjectId = id }], ct))
            .GetValueOrDefault((platform, id));

    /// <summary>The names Modbot has stored for these accounts, in one query per platform.</summary>
    internal async Task<Dictionary<(FactPlatform, string), string>> NamesAsync(
        IReadOnlyCollection<PersonWatch> watches, CancellationToken ct)
    {
        var names = new Dictionary<(FactPlatform, string), string>();

        var vrchat = watches.Where(w => w.SubjectPlatform == FactPlatform.VRChat).Select(w => w.SubjectId).Distinct().ToList();
        var discord = watches.Where(w => w.SubjectPlatform == FactPlatform.Discord).Select(w => w.SubjectId).Distinct().ToList();

        if (vrchat.Count > 0)
        {
            var found = await _db.VRChatUsers.AsNoTracking()
                .Where(u => vrchat.Contains(u.UserId) && u.DisplayName != null)
                .Select(u => new { u.UserId, u.DisplayName })
                .ToListAsync(ct);

            foreach (var u in found)
                names[(FactPlatform.VRChat, u.UserId)] = u.DisplayName!;
        }

        if (discord.Count > 0)
        {
            var found = await _db.DiscordMembers.AsNoTracking()
                .Where(m => discord.Contains(m.UserId))
                .Select(m => new { m.UserId, m.DisplayName })
                .ToListAsync(ct);

            foreach (var m in found)
                names.TryAdd((FactPlatform.Discord, m.UserId), m.DisplayName);
        }

        return names;
    }

    /// <summary>
    /// The platform an id belongs to. Anything else is refused rather than guessed at: a watch
    /// filed under the wrong platform is a watch on somebody else.
    /// </summary>
    private static FactPlatform PlatformOf(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return FactPlatform.VRChat;

        if (Enum.TryParse<FactPlatform>(platform, ignoreCase: true, out var parsed)
            && parsed is FactPlatform.VRChat or FactPlatform.Discord)
        {
            return parsed;
        }

        throw new WatchRefused(400, "A watch is on a person on VRChat or on Discord.");
    }

    /// <summary>The fact log, or a refusal: a host mapped without it can read watches, not change them.</summary>
    private (IFactWriter Facts, EventPartitionMaintainer Partitions) Writer()
        => _facts is null || _partitions is null
            ? throw new WatchRefused(503, "This deployment is not set up to keep watches.")
            : (_facts, _partitions);
}
