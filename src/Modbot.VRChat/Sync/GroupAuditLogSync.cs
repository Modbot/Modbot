using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;
using VRChat.API.Model;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Reads the managed group's VRChat audit log and turns it into facts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the authoritative moderation source</strong> (spec 5.9). Everything else that
/// could tell Modbot someone was banned is an inference: a sync diff sees that a member is gone
/// and can never see who removed them, which makes spec 5.8's entire accountability story
/// unanswerable. The audit log states the actor, and the time, exactly -- so these facts carry no
/// <c>occurred_before</c> window and every one of them carries an <c>actor_id</c>.
/// </para>
/// <para>
/// <strong>Idempotency comes first, precision second.</strong> Each pass deliberately re-reads a
/// window behind the cursor, because VRChat's paging is over a live log and an entry can surface
/// after Modbot has already read past its timestamp. Re-reading costs a duplicate check; reading
/// exactly once costs a ban. The check is on VRChat's own entry id, which is carried into every
/// fact's payload precisely so that this pass can recognise its own earlier work.
/// </para>
/// <para>
/// <strong>Nothing here retries a 429</strong> (spec 4.3.1). A rate-limited pass stops where it
/// is, leaves the cursor alone, and reports the outcome so the cadence can go as slow as it is
/// allowed to. The next pass re-reads from the same place; the overlap is what makes stopping
/// mid-page safe.
/// </para>
/// </remarks>
public sealed class GroupAuditLogSync
{
    private readonly IVRChatGate _gate;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly SyncDiagnostics _diagnostics;
    private readonly AuditLogSyncOptions _options;
    private readonly ILogger _log;

    /// <summary>
    /// Months this run has already made room for. The maintainer is idempotent but not free --
    /// it asks the catalogue every time -- and a backfill page of sixty entries from one month
    /// would otherwise ask sixty times for the same answer.
    /// </summary>
    private readonly HashSet<string> _ensuredMonths = new(StringComparer.Ordinal);

    public GroupAuditLogSync(
        IVRChatGate gate,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ModbotContext db,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        AuditLogSyncOptions? options = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _gate = gate;
        _facts = facts;
        _partitions = partitions;
        _db = db;
        _clock = clock;
        _diagnostics = diagnostics;
        _options = (options ?? new AuditLogSyncOptions()).Clamped();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<AuditLogRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        // Never validated, only checked for presence: VRChat ids follow no structure (spec 3.1.1),
        // so "does this look like a group id" is a question with no correct implementation.
        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new AuditLogRunResult(SyncOutcome.NotConfigured, Message: "no managed group configured");

        var groupId = settings.ManagedGroupId;

        var run = await ReadAsync(settings, groupId, ct).ConfigureAwait(false);

        settings.AuditLogPolledAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// Live entries first; the walk through existing history gets whatever is left over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering matters on exactly the groups where it is hard to see. A backfill that had
    /// priority would walk backwards through months of history a page at a time, and today's bans
    /// would not be recorded until it finished -- which on a large group is hours. Nothing would
    /// be lost, because each fact still carries VRChat's own timestamp, but a freshly installed
    /// Modbot would show an empty dashboard through its first afternoon.
    /// </para>
    /// <para>
    /// The exception is the very first pass, when there is no cursor yet. There is no "live"
    /// window to read until the head of the log is known, so that pass establishes it.
    /// </para>
    /// </remarks>
    private async Task<AuditLogRunResult> ReadAsync(Settings settings, string groupId, CancellationToken ct)
    {
        var backfilling = _options.Backfill && !settings.AuditLogBackfillComplete;

        if (backfilling && settings.AuditLogSyncedThrough is null)
            return await BackfillAsync(settings, groupId, ct).ConfigureAwait(false);

        var tail = await TailAsync(settings, groupId, ct).ConfigureAwait(false);

        // Not drained means there is more waiting right now, so history waits too; a cold stop or
        // a failure means the next request would not be sent, or should not be.
        if (!backfilling || !tail.Drained)
            return tail;

        return tail.Plus(await BackfillAsync(settings, groupId, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Walks back through the audit log VRChat already holds, one page per pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only history that exists from before Modbot was installed, and spec 5.1's
    /// argument is that recorded history cannot be retrofitted -- so it is worth the requests. One
    /// page per pass rather than a loop, so a group with a long log does not spend its whole
    /// audit-log budget for an hour on history while today's bans wait behind it.
    /// </para>
    /// <para>
    /// The cursor is a plain offset, which is safe here for a reason worth stating: the audit log
    /// only grows at the head. An entry arriving mid-walk shifts the window toward entries already
    /// read, so the failure mode is a duplicate -- which is discarded -- and never a gap.
    /// </para>
    /// <para>
    /// <see cref="Settings.AuditLogSyncedThrough"/> is advanced to the newest entry seen anywhere
    /// in the walk, so that whichever order VRChat returns pages in, the tail poll that follows
    /// the backfill starts from the true head.
    /// </para>
    /// </remarks>
    private async Task<AuditLogRunResult> BackfillAsync(
        Settings settings,
        string groupId,
        CancellationToken ct)
    {
        if (settings.AuditLogBackfillOffset >= _options.MaxBackfillPages * _options.PageSize)
        {
            _log.Warning(
                "Stopping the audit-log backfill at {Pages} pages: VRChat is still reporting more history. "
                + "Recent entries are unaffected; older ones will not be recorded",
                _options.MaxBackfillPages);

            settings.AuditLogBackfillComplete = true;
            return new AuditLogRunResult(SyncOutcome.Quiet, Message: "backfill page limit reached");
        }

        var page = await FetchAsync(groupId, settings.AuditLogBackfillOffset, startDate: null, ct)
            .ConfigureAwait(false);

        if (page.Failure is { } failure)
            return failure;

        var entries = page.Entries;
        var written = await RecordAsync(entries, ct).ConfigureAwait(false);

        settings.AuditLogBackfillOffset += entries.Count;
        settings.AuditLogSyncedThrough = Newest(settings.AuditLogSyncedThrough, entries);

        // Short page or no next page: VRChat has nothing older left. An empty page counts as
        // exhausted too -- there is nothing to be gained by asking the same question again.
        if (entries.Count == 0 || entries.Count < _options.PageSize || !page.HasNext)
        {
            settings.AuditLogBackfillComplete = true;
            _log.Information(
                "Finished reading the group's existing audit log: {Entries} entries in total",
                settings.AuditLogBackfillOffset);
        }

        return new AuditLogRunResult(
            written.FactsWritten > 0 ? SyncOutcome.Produced : SyncOutcome.Quiet,
            PagesRead: 1,
            EntriesRead: entries.Count,
            FactsWritten: written.FactsWritten,
            AlreadyRecorded: written.AlreadyRecorded,
            Unmapped: written.Unmapped,
            Unusable: written.Unusable,
            Backfilling: !settings.AuditLogBackfillComplete,
            Drained: true,
            SyncedThrough: settings.AuditLogSyncedThrough);
    }

    /// <summary>
    /// Reads everything since the cursor, minus the overlap, paging until the window is drained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cursor advances <strong>only when the window is fully drained</strong>. A pass that
    /// stops early -- because it hit its page budget, because a bucket went cold, because the
    /// process is shutting down -- leaves it exactly where it was, so the next pass re-reads
    /// rather than steps over the part it never saw. Advancing on a partial read is the one
    /// mistake here that loses data silently and permanently.
    /// </para>
    /// <para>
    /// A frozen cursor alone would not be enough, though. A backlog bigger than one pass's page
    /// budget -- a day's outage on a busy group -- would have every pass re-read the same first
    /// pages and never reach what is behind them, and it would look like a producer working
    /// perfectly. So the offset within the window is remembered too, and only reset when the
    /// window is finally drained.
    /// </para>
    /// <para>
    /// The offset is <em>not</em> advanced when a page fails. Re-reading is free -- duplicates are
    /// discarded -- and a failure is exactly the moment not to assume the next page starts where
    /// this one would have ended.
    /// </para>
    /// </remarks>
    private async Task<AuditLogRunResult> TailAsync(Settings settings, string groupId, CancellationToken ct)
    {
        var from = settings.AuditLogSyncedThrough is { } cursor
            ? (cursor - _options.Overlap).UtcDateTime
            : (DateTime?)null;

        var totals = new RecordTotals();
        var pages = 0;
        var drained = false;
        DateTimeOffset? newest = settings.AuditLogSyncedThrough;
        var offset = settings.AuditLogCatchUpOffset;

        while (pages < _options.MaxPagesPerRun)
        {
            var page = await FetchAsync(groupId, offset, from, ct).ConfigureAwait(false);

            if (page.Failure is { } failure)
            {
                // Whatever was recorded before the failure stays recorded -- facts are never
                // rolled back -- but the cursor does not move, so the next pass reads it again
                // and the duplicate check absorbs the overlap.
                return failure with
                {
                    PagesRead = pages,
                    EntriesRead = totals.EntriesRead,
                    FactsWritten = totals.FactsWritten,
                    AlreadyRecorded = totals.AlreadyRecorded,
                    SyncedThrough = settings.AuditLogSyncedThrough,
                };
            }

            pages++;
            var entries = page.Entries;
            totals.Add(await RecordAsync(entries, ct).ConfigureAwait(false), entries.Count);
            newest = Newest(newest, entries);

            if (entries.Count == 0 || entries.Count < _options.PageSize || !page.HasNext)
            {
                drained = true;
                break;
            }

            offset += entries.Count;
        }

        if (drained)
        {
            settings.AuditLogSyncedThrough = newest;
            settings.AuditLogCatchUpOffset = 0;
        }
        else
        {
            settings.AuditLogCatchUpOffset = offset;
        }

        return new AuditLogRunResult(
            totals.FactsWritten > 0 ? SyncOutcome.Produced : SyncOutcome.Quiet,
            PagesRead: pages,
            EntriesRead: totals.EntriesRead,
            FactsWritten: totals.FactsWritten,
            AlreadyRecorded: totals.AlreadyRecorded,
            Unmapped: totals.Unmapped,
            Unusable: totals.Unusable,
            Drained: drained,
            SyncedThrough: settings.AuditLogSyncedThrough,
            Message: drained
                ? null
                : $"page budget reached; resuming at offset {offset} with the cursor unmoved");
    }

    private async Task<FetchedPage> FetchAsync(
        string groupId,
        int offset,
        DateTime? startDate,
        CancellationToken ct)
    {
        // The endpoint class is named explicitly, and it is resource-scoped on the group, because
        // VRChat's limits are sometimes per-resource (spec 4.3.1). `groups.auditlog` already has
        // a budget -- spec 4.2 gives it one request per 8 seconds -- so no new rate-limit question
        // is being asked here.
        var endpoint = new VRChatEndpoint(
            VRChatEndpointClass.GroupsAuditLog, groupId, "GetGroupAuditLogs");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Groups.GetGroupAuditLogsWithHttpInfoAsync(
                groupId,
                n: _options.PageSize,
                offset: offset,
                startDate: startDate,
                cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (result.Success)
            return new FetchedPage(result.Value?.Results ?? [], result.Value?.HasNext ?? false, null);

        if (result.Kind == VRChatFailureKind.RateLimited)
        {
            // Reported, not retried, and not escalated to an error: a cold stop is Modbot working
            // as designed, and logging it as a failure would train an operator to ignore the line
            // that matters (spec 4.3.1).
            _log.Information(
                "Audit-log sync is paused: {Reason}",
                result.ErrorMessage ?? "the groups.auditlog bucket is cold-stopped");

            return new FetchedPage([], false,
                new AuditLogRunResult(SyncOutcome.RateLimited, Message: result.ErrorMessage));
        }

        _log.Warning(
            "Audit-log sync could not read the log: {Status} {Reason}",
            result.StatusCode,
            result.ErrorMessage ?? "no detail");

        return new FetchedPage([], false,
            new AuditLogRunResult(SyncOutcome.Failed, Message: result.ErrorMessage));
    }

    /// <summary>
    /// Maps a page and writes whatever is new.
    /// </summary>
    /// <remarks>
    /// Entries already recorded are discarded before they reach <see cref="IFactWriter"/> rather
    /// than after: the writer's own deduplication is for client reports only (spec 5.7), and it is
    /// deliberately not applied to this source, because two audit entries a second apart are two
    /// things that happened.
    /// </remarks>
    private async Task<RecordTotals> RecordAsync(
        IReadOnlyList<GroupAuditLogEntry> entries,
        CancellationToken ct)
    {
        var totals = new RecordTotals();
        if (entries.Count == 0)
            return totals;

        var mapped = new List<AuditLogMapping>(entries.Count);

        foreach (var entry in entries)
        {
            var mapping = AuditLogEntryMapper.Map(entry);

            switch (mapping.Rejection)
            {
                case AuditLogRejection.None:
                    mapped.Add(mapping);
                    break;

                case AuditLogRejection.UnknownEventType:
                    // Reported so somebody adds a mapping -- AND recorded, under
                    // FactType.Unrecognised with VRChat's own name kept in TypeRaw. It used to be
                    // counted and dropped, and the cursor moved on; VRChat's audit log ages out,
                    // so every one of those was gone for good. Section 5.1's whole argument is
                    // that history cannot be backfilled, and this was Modbot doing the deleting.
                    totals.Unmapped++;
                    ReportUnmapped(entry, mapping.EventType);
                    if (mapping.Fact is not null)
                        mapped.Add(mapping);
                    break;

                default:
                    totals.Unusable++;
                    _log.Warning(
                        "Skipped audit entry {EntryId} of type {EventType}: {Reason}",
                        mapping.EntryId ?? "(no id)", mapping.EventType, mapping.Rejection);
                    break;
            }
        }

        if (mapped.Count == 0)
            return totals;

        var known = await AlreadyRecordedAsync(mapped, ct).ConfigureAwait(false);

        foreach (var mapping in mapped)
        {
            var fact = mapping.Fact!;

            // The entry id is the real key. The shape is the fallback for an entry that arrived
            // without one -- weaker, because it merges two role grants issued in the same
            // millisecond, but the alternative for an id-less entry is re-writing it on every
            // overlapping poll forever.
            var recorded = mapping.EntryId is { } id
                ? known.EntryIds.Contains(id)
                : known.Shapes.Contains(ShapeOf(fact));

            if (recorded)
            {
                totals.AlreadyRecorded++;
                continue;
            }

            // A backfilled entry can predate every partition the maintainer's rolling window
            // covers, and an insert with nowhere to land fails the whole page -- including the
            // entries after it, which have done nothing wrong.
            await EnsureRoomForAsync(fact.OccurredAt, ct).ConfigureAwait(false);

            await _facts.WriteAsync(fact, ct).ConfigureAwait(false);
            totals.FactsWritten++;
        }

        return totals;
    }

    private async Task EnsureRoomForAsync(DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (!_ensuredMonths.Add(EventPartitionMaintainer.PartitionName(occurredAt)))
            return;

        await _partitions.EnsureForAsync(occurredAt, ct).ConfigureAwait(false);
    }

    private void ReportUnmapped(GroupAuditLogEntry entry, string eventType)
    {
        if (!_diagnostics.RecordUnmappedEvent(eventType, entry.Id, entry.Description))
            return;

        // Once per distinct type per process. Loud, because the alternative failure mode is a
        // fact log that looks complete and is not: VRChat types eventType as a free-form string,
        // so a type Modbot has never seen is indistinguishable from one whose name is wrong here.
        _log.Warning(
            "VRChat audit event {EventType} has no mapping yet. It is recorded as modbot.unrecognised with the original name kept, and needs one adding. "
            + "Sample entry {EntryId}: {Description}",
            eventType,
            entry.Id ?? "(no id)",
            entry.Description ?? "(no description)");
    }

    /// <summary>
    /// Which of these entries the fact log already holds, by VRChat's own entry id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrowed by subject and by time so that the index spec 5.3 already declares --
    /// <c>(subject_platform, subject_id, occurred_at)</c> -- answers it. The entry id itself lives
    /// inside <c>data</c>, and filtering on a <c>jsonb</c> expression instead would be a sequential
    /// scan of the current partition on every poll, which for a busy group is minutes of history
    /// re-read every few seconds.
    /// </para>
    /// <para>
    /// The id, not the shape of the fact, is the key. Two role grants to the same person in the
    /// same millisecond -- which is what assigning several roles at once looks like -- are two
    /// separate entries, and a key made of type, subject and timestamp would merge them.
    /// </para>
    /// </remarks>
    private async Task<RecordedFacts> AlreadyRecordedAsync(
        IReadOnlyList<AuditLogMapping> mapped,
        CancellationToken ct)
    {
        var subjects = mapped.Select(m => m.Fact!.SubjectId).Distinct(StringComparer.Ordinal).ToArray();
        var from = mapped.Min(m => m.Fact!.OccurredAt);
        var to = mapped.Max(m => m.Fact!.OccurredAt);

        var rows = await _db.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                     && e.Source == FactSource.AuditLog
                     && subjects.Contains(e.SubjectId)
                     && e.OccurredAt >= from
                     && e.OccurredAt <= to)
            .Select(e => new { e.Type, e.SubjectId, e.OccurredAt, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            shapes.Add(Shape(row.Type, row.SubjectId, row.OccurredAt));

            if (ReadEntryId(row.Data) is { } id)
                ids.Add(id);
        }

        return new RecordedFacts(ids, shapes);
    }

    private static string ShapeOf(FactRecord fact) =>
        Shape(fact.Type, fact.SubjectId, fact.OccurredAt);

    /// <summary>
    /// A separator that cannot occur inside a VRChat id, for the reason <c>FactWriter</c> uses
    /// one: ids are arbitrary user-chosen text (spec 3.1.1), so a printable separator could
    /// appear inside one and let two different events collide on a single key.
    /// </summary>
    private const char KeySeparator = '';

    private static string Shape(string type, string subjectId, DateTimeOffset occurredAt) =>
        string.Join(KeySeparator, type, subjectId, occurredAt.UtcTicks);

    private readonly record struct RecordedFacts(HashSet<string> EntryIds, HashSet<string> Shapes);

    private static string? ReadEntryId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonNode.Parse(payload) is JsonObject o && o.TryGetPropertyValue("auditEntryId", out var id)
                ? id?.GetValue<string>()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // The property exists but is not a string. Not this producer's row, then.
            return null;
        }
    }

    private static DateTimeOffset? Newest(
        DateTimeOffset? current,
        IReadOnlyList<GroupAuditLogEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.CreatedAt == default)
                continue;

            var at = AuditLogEntryMapper.ReadTimestamp(entry.CreatedAt);
            if (current is null || at > current)
                current = at;
        }

        return current;
    }

    private readonly record struct FetchedPage(
        IReadOnlyList<GroupAuditLogEntry> Entries,
        bool HasNext,
        AuditLogRunResult? Failure);

    private struct RecordTotals
    {
        public int EntriesRead;
        public int FactsWritten;
        public int AlreadyRecorded;
        public int Unmapped;
        public int Unusable;

        public void Add(RecordTotals other, int entriesRead)
        {
            EntriesRead += entriesRead;
            FactsWritten += other.FactsWritten;
            AlreadyRecorded += other.AlreadyRecorded;
            Unmapped += other.Unmapped;
            Unusable += other.Unusable;
        }
    }
}
