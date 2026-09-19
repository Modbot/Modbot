using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Imports;

/// <summary>
/// Runs one import: reads the upload, maps each record, skips what is already in, writes the
/// rest (import design §8).
/// </summary>
/// <remarks>
/// <para>
/// Records go in batches of <see cref="BatchSize"/>: one query for the keys already present, one
/// transaction for the facts and the dedupe rows, then the row's counts. A batch that commits
/// stays committed if a later one fails, and the dedupe rows are what make the re-upload after
/// a failure import only what was left.
/// </para>
/// <para>
/// The row's counts and status are written with <c>ExecuteUpdate</c> rather than through the
/// change tracker, which is cleared after every batch: a hundred thousand tracked rows are a
/// hundred thousand rows the next <c>SaveChanges</c> has to walk.
/// </para>
/// </remarks>
public sealed class ImportRunner
{
    public const int BatchSize = 500;

    /// <summary>
    /// How far either side of a record's time a fact counts as the same event (import design
    /// §6.1): two seconds.
    /// </summary>
    /// <remarks>
    /// Two systems writing down one action almost never agree to the tick. An export usually
    /// carries whole seconds while Modbot's own record carries fractions, and two systems each
    /// rounding or truncating on their own can land just under a second apart in either
    /// direction; two seconds covers that with a second to spare for a recorder that stamps the
    /// row rather than the action. It stays far below the fifteen seconds it takes somebody to
    /// leave and come back, so two things that really did happen to one person are never merged,
    /// and records inside one file are told apart by their key (§6), never by their time --
    /// three warnings a spreadsheet dates only to the day are still three warnings.
    /// </remarks>
    public static readonly TimeSpan SameMoment = TimeSpan.FromSeconds(2);

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly AccountFacts _accountFacts;
    private readonly IModbotClock _clock;
    private readonly ILogger<ImportRunner> _log;

    public ImportRunner(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        AccountFacts accountFacts,
        IModbotClock clock,
        ILogger<ImportRunner> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(accountFacts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _accountFacts = accountFacts;
        _clock = clock;
        _log = log;
    }

    /// <summary>Runs the oldest queued import, if there is one.</summary>
    /// <returns>Whether there was one.</returns>
    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var next = await _db.Imports.AsNoTracking()
            .Where(i => i.Status == ImportStatus.Queued)
            .OrderBy(i => i.CreatedAt)
            .Select(i => i.Id)
            .FirstOrDefaultAsync(ct);

        if (next == Guid.Empty)
            return false;

        await RunAsync(next, ct);
        return true;
    }

    /// <summary>Runs one queued import to its end. Does nothing for an import in any other state.</summary>
    public async Task RunAsync(Guid id, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        // Claiming it is the one update that must not race another runner: only a Queued row
        // becomes Running, and whoever moved it is the one that runs it.
        var claimed = await _db.Imports
            .Where(i => i.Id == id && i.Status == ImportStatus.Queued)
            .ExecuteUpdateAsync(
                s => s.SetProperty(i => i.Status, ImportStatus.Running).SetProperty(i => i.StartedAt, now),
                ct);

        if (claimed == 0)
            return;

        var import = await _db.Imports.AsNoTracking().SingleAsync(i => i.Id == id, ct);
        var progress = new Progress();

        try
        {
            await ProcessAsync(import, progress, ct);
            await FinishAsync(import.Id, ImportStatus.Done, null, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Import {ImportId} from {Source} failed", import.Id, import.Source);

            _db.ChangeTracker.Clear();
            await FinishAsync(import.Id, ImportStatus.Failed, Reason(e), progress, ct);
        }

        if (!import.DryRun)
            await RecordAsync(import, progress, ct);
    }

    private async Task ProcessAsync(Import import, Progress progress, CancellationToken ct)
    {
        var body = import.Body;
        if (body is null || body.Length == 0)
            throw new InvalidOperationException("The upload was empty.");

        var months = new HashSet<DateTimeOffset>();
        var written = new WrittenEvents();
        var batch = new List<ImportItem>(BatchSize);

        foreach (var item in ImportFile.Read(body))
        {
            batch.Add(item);
            if (batch.Count < BatchSize)
                continue;

            await ProcessBatchAsync(import, batch, months, written, progress, ct);
            batch.Clear();
        }

        if (batch.Count > 0)
            await ProcessBatchAsync(import, batch, months, written, progress, ct);
    }

    /// <param name="written">
    /// The events this run has already put in, so a second record describing one of them is not
    /// mistaken for something Modbot knew beforehand (import design §6.1).
    /// </param>
    private async Task ProcessBatchAsync(
        Import import,
        List<ImportItem> items,
        HashSet<DateTimeOffset> months,
        WrittenEvents written,
        Progress progress,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var records = new List<ParsedRecord>(items.Count);

        foreach (var item in items)
        {
            progress.Received++;

            if (ImportFile.TryParse(item, now, import.SeenBy, out var record, out var reason))
                records.Add(record!);
            else
                progress.Reject(item.Line, reason ?? "Not a record.");
        }

        var keys = records.Select(r => r.Key).Distinct().ToList();
        var present = keys.Count == 0
            ? []
            : (await _db.ImportRecords.AsNoTracking()
                .Where(r => r.Source == import.Source && keys.Contains(r.Key))
                .Select(r => r.Key)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var toWrite = new List<ParsedRecord>();
        foreach (var record in records)
        {
            // The same record twice in one file is a duplicate too.
            if (present.Add(record.Key))
                toWrite.Add(record);
            else
                progress.Skipped++;
        }

        // What Modbot already has from somewhere else (import design §6.1). Asked of the fact
        // writer, which is where the question is answered for every other producer, and asked
        // before anything is written so a dry run counts the same skips a real run would make.
        // Turned off by the upload, it is not asked at all -- which leaves the record key above
        // doing its own job untouched (§6).
        var planned = new List<(ParsedRecord Record, FactRecord Fact, long? Existing)>(toWrite.Count);
        foreach (var record in toWrite)
        {
            var fact = ToFact(import, record);

            var existing = !import.Dedup || written.Has(record)
                ? null
                : await _facts.AlreadyRecordedAsync(fact, SameMoment, ct);

            if (existing is null)
                written.Add(record);
            else
                progress.AlreadyKnown++;

            planned.Add((record, fact, existing));
        }

        if (!import.DryRun && planned.Count > 0)
        {
            // Partitions before the transaction: creating one takes its own advisory lock, and
            // an import reaching back years creates several.
            foreach (var (record, _, existing) in planned)
            {
                if (existing is not null)
                    continue;

                var month = new DateTimeOffset(record.At.Year, record.At.Month, 1, 0, 0, 0, TimeSpan.Zero);
                if (months.Add(month))
                    await _partitions.EnsureForAsync(record.At, ct);
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            foreach (var (record, fact, existing) in planned)
            {
                // A record Modbot already knew still gets its row here, pointing at the fact that
                // already says it: a re-upload then skips it outright, and the record is still
                // traceable to the import that met it.
                var factId = existing ?? (await _facts.WriteAsync(fact, ct)).Id;

                _db.ImportRecords.Add(new ImportRecord
                {
                    Source = import.Source,
                    Key = record.Key,
                    FactId = factId,
                    ImportId = import.Id,
                    SubjectPlatform = record.SubjectPlatform,
                    SubjectId = record.SubjectId,
                    ImportedAt = now,
                });
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _db.ChangeTracker.Clear();
        }

        progress.Imported += planned.Count(p => p.Existing is null);

        await _db.Imports
            .Where(i => i.Id == import.Id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(i => i.Received, progress.Received)
                    .SetProperty(i => i.Imported, progress.Imported)
                    .SetProperty(i => i.Skipped, progress.Skipped)
                    .SetProperty(i => i.AlreadyKnown, progress.AlreadyKnown)
                    .SetProperty(i => i.Rejected, progress.Rejected)
                    .SetProperty(i => i.Rejections, progress.RejectionsJson()),
                ct);
    }

    private static FactRecord ToFact(Import import, ParsedRecord record)
    {
        // How the fact got here, which its source no longer says (import design §5.1). Written
        // whatever source the record carries, so "where did this claim come from" stays
        // answerable for a fact filed under VRChat's audit log.
        var data = record.Data;
        data[ImportedFact.ImportIdKey] = import.Id.ToString();
        data[ImportedFact.SourceKey] = import.Source;

        if (record.ExternalId is not null)
            data[ImportedFact.ExternalIdKey] = record.ExternalId;

        if (record.ActorName is not null && !data.ContainsKey("actorDisplayName"))
            data["actorDisplayName"] = record.ActorName;

        return new FactRecord
        {
            Type = record.Type,
            TypeRaw = record.TypeRaw,
            OccurredAt = record.At,
            SubjectPlatform = record.SubjectPlatform,
            SubjectId = record.SubjectId,
            ActorPlatform = record.ActorPlatform,
            ActorId = record.ActorId,
            Source = record.Source,
            Data = data,
        };
    }

    private async Task FinishAsync(Guid id, ImportStatus status, string? error, Progress progress, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        await _db.Imports
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(i => i.Status, status)
                    .SetProperty(i => i.Error, error)
                    .SetProperty(i => i.FinishedAt, now)
                    .SetProperty(i => i.Body, (byte[]?)null)
                    .SetProperty(i => i.Received, progress.Received)
                    .SetProperty(i => i.Imported, progress.Imported)
                    .SetProperty(i => i.Skipped, progress.Skipped)
                    .SetProperty(i => i.AlreadyKnown, progress.AlreadyKnown)
                    .SetProperty(i => i.Rejected, progress.Rejected)
                    .SetProperty(i => i.Rejections, progress.RejectionsJson()),
                ct);
    }

    /// <summary>The one audit entry per import (import design §4.4).</summary>
    private async Task RecordAsync(Import import, Progress progress, CancellationToken ct)
    {
        var status = await _db.Imports.AsNoTracking()
            .Where(i => i.Id == import.Id)
            .Select(i => i.Status)
            .SingleAsync(ct);

        await _accountFacts.RecordAsync(
            FactType.ImportDone,
            import.Id.ToString(),
            new Actor(import.StartedByUserId, import.StartedByName),
            new JsonObject
            {
                ["source"] = import.Source,
                ["fileName"] = import.FileName,
                ["status"] = status.ToString(),
                ["received"] = progress.Received,
                ["imported"] = progress.Imported,
                ["skipped"] = progress.Skipped,
                ["alreadyKnown"] = progress.AlreadyKnown,
                ["rejected"] = progress.Rejected,
            },
            ct);
    }

    private static string Reason(Exception e) => e switch
    {
        System.Text.Json.JsonException json => $"The file is not JSON: {json.Message}",
        InvalidOperationException invalid => invalid.Message,
        _ => "The import stopped on an error. Modbot's log has the details.",
    };

    private sealed class Progress
    {
        private readonly List<ImportRejection> _rejections = [];

        public int Received { get; set; }
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public int AlreadyKnown { get; set; }
        public int Rejected { get; private set; }

        public void Reject(int line, string reason)
        {
            Rejected++;
            if (_rejections.Count < Import.MaxRejectionsKept)
                _rejections.Add(new ImportRejection(line, reason));
        }

        public string RejectionsJson() => ImportView.RejectionsJson(_rejections);
    }

    /// <summary>
    /// The events this run has put in, so a second record describing one of them is not mistaken
    /// for something Modbot knew beforehand (import design §6.1).
    /// </summary>
    /// <remarks>
    /// It answers the same question <see cref="IFactWriter.AlreadyRecordedAsync"/> answers, over
    /// the same window, so this set and that query never disagree. Were it to compare instants
    /// exactly while the query allows <see cref="SameMoment"/> either side, the second of two
    /// records a fraction apart in one file would be reported as something Modbot already had --
    /// when what it had was the fact this very import wrote a moment earlier.
    /// </remarks>
    private sealed class WrittenEvents
    {
        private readonly Dictionary<SameEvent, SortedSet<DateTime>> _at = [];

        public bool Has(ParsedRecord record)
        {
            if (!_at.TryGetValue(KeyOf(record), out var times))
                return false;

            // Clamped, for the same reason the writer's own window is: a record dated to the
            // year 1 is junk, and a window that ran off the calendar would throw.
            var at = record.At.UtcDateTime;
            var from = SameMoment < at - DateTime.MinValue ? at - SameMoment : DateTime.MinValue;
            var to = SameMoment < DateTime.MaxValue - at ? at + SameMoment : DateTime.MaxValue;

            return times.GetViewBetween(from, to).Count > 0;
        }

        public void Add(ParsedRecord record)
        {
            if (!_at.TryGetValue(KeyOf(record), out var times))
                _at[KeyOf(record)] = times = [];

            times.Add(record.At.UtcDateTime);
        }

        private static SameEvent KeyOf(ParsedRecord record)
            => new(record.SubjectPlatform, record.SubjectId, record.Type);
    }
}

/// <summary>
/// What two imported records have to share before their times are even worth comparing, when the
/// question is whether Modbot already has the event (import design §6.1).
/// </summary>
public readonly record struct SameEvent(
    FactPlatform SubjectPlatform,
    string SubjectId,
    string Type);
