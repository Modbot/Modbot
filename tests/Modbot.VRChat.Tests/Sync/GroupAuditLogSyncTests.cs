using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The producer that makes Modbot a moderation tool: VRChat's audit log, recorded as facts.
/// </summary>
/// <remarks>
/// These are the failures that actually happen in production -- a process restarted mid-sync, an
/// entry that surfaced late, a window re-read on purpose, a bucket gone cold -- rather than the
/// happy path, which is one page and a loop.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class GroupAuditLogSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    [Fact]
    public async Task AnAuditEntryBecomesAFactWithBothTheSubjectAndTheActor()
    {
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-5), GroupAuditLogEvents.UserBan));

        var run = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.Equal(1, run.FactsWritten);

        var fact = Assert.Single(await FactsAsync());

        Assert.Equal(FactType.MemberBanned, fact.Type);
        Assert.Equal("usr_target", fact.SubjectId);

        // Spec 5.8: without this the audit log is no better than a sync diff, and every
        // accountability question in the design is unanswerable.
        Assert.Equal("usr_moderator", fact.ActorId);
        Assert.Equal(FactPlatform.VRChat, fact.ActorPlatform);

        Assert.Equal(FactSource.AuditLog, fact.Source);
        Assert.Null(fact.OccurredBefore);
        Assert.Equal(Now.AddMinutes(-5), fact.OccurredAt);
    }

    /// <summary>
    /// The overlap is deliberate (see <see cref="AuditLogSyncOptions.Overlap"/>), so the same
    /// entries are read again on every poll. If that produced duplicates, every ban in the log
    /// would be counted once per poll for fifteen minutes.
    /// </summary>
    [Fact]
    public async Task ReReadingTheOverlapWindowWritesNothingTwice()
    {
        VRChat.Groups.Add(
            Entry("gaud_1", Now.AddMinutes(-5)),
            Entry("gaud_2", Now.AddMinutes(-4), GroupAuditLogEvents.UserUnban));

        await RunAuditLogAsync();
        var second = await RunAuditLogAsync();
        var third = await RunAuditLogAsync();

        Assert.Equal(0, second.FactsWritten);
        Assert.Equal(2, second.AlreadyRecorded);
        Assert.Equal(SyncOutcome.Quiet, third.Outcome);

        Assert.Equal(2, (await FactsAsync()).Count);
    }

    /// <summary>
    /// Several roles assigned at once produce several entries sharing a target and a timestamp. A
    /// duplicate check keyed on the shape of the fact rather than on VRChat's entry id would merge
    /// them into one, and the group would be missing role grants it can see in VRChat's own UI.
    /// </summary>
    [Fact]
    public async Task EntriesSharingASubjectAndAnInstantAreNotMerged()
    {
        var at = Now.AddMinutes(-2);

        VRChat.Groups.Add(
            Entry("gaud_1", at, GroupAuditLogEvents.RoleAssign),
            Entry("gaud_2", at, GroupAuditLogEvents.RoleAssign),
            Entry("gaud_3", at, GroupAuditLogEvents.RoleAssign));

        await RunAuditLogAsync();
        var again = await RunAuditLogAsync();

        Assert.Equal(3, (await FactsAsync()).Count);
        Assert.Equal(0, again.FactsWritten);
    }

    /// <summary>
    /// The cursor advances only when the window was drained. A pass that stops at its page budget
    /// and advances anyway steps silently over the entries it never read -- and the audit log is
    /// the one source where a silent gap is a lost ban rather than a stale number.
    /// </summary>
    [Fact]
    public async Task APassThatStopsAtItsPageBudgetDoesNotAdvanceTheCursor()
    {
        for (var i = 0; i < 10; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddMinutes(-i - 1), target: $"usr_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = false, PageSize = 2, MaxPagesPerRun = 2 };
        var run = await RunAuditLogAsync(options);

        Assert.Equal(2, run.PagesRead);
        Assert.Equal(4, run.FactsWritten);
        Assert.Null((await SettingsAsync()).AuditLogSyncedThrough);
        Assert.NotNull(run.Message);
    }

    /// <summary>
    /// The restart case. Each pass gets a fresh context and a fresh producer, which is what a
    /// redeploy looks like from the database's point of view: whatever was in memory is gone and
    /// only the cursor and the facts remain.
    /// </summary>
    [Fact]
    public async Task ARestartMidSyncResumesWithoutDuplicatingOrSkipping()
    {
        for (var i = 0; i < 10; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddMinutes(-i - 1), target: $"usr_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = false, PageSize = 2, MaxPagesPerRun = 2 };

        // Four passes, each "restarting" the producer, until the whole log is drained.
        for (var pass = 0; pass < 4; pass++)
            await RunAuditLogAsync(options);

        var facts = await FactsAsync();

        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.SubjectId).Distinct().Count());
    }

    /// <summary>
    /// The reason the cursor is a watermark-minus-an-overlap rather than a high-water mark.
    /// VRChat's paging is over a live log, and an entry can surface with a timestamp Modbot has
    /// already read past.
    /// </summary>
    [Fact]
    public async Task AnEntryThatArrivesLateIsStillRecorded()
    {
        VRChat.Groups.Add(Entry("gaud_late", Now.AddMinutes(-10)));
        await RunAuditLogAsync();

        var cursor = (await SettingsAsync()).AuditLogSyncedThrough;
        Assert.Equal(Now.AddMinutes(-10), cursor);

        // Older than the cursor, and it only shows up now.
        VRChat.Groups.Add(Entry("gaud_older", Now.AddMinutes(-12), target: "usr_missed"));

        var run = await RunAuditLogAsync();

        Assert.Equal(1, run.FactsWritten);
        Assert.Contains(await FactsAsync(), f => f.SubjectId == "usr_missed");
    }

    /// <summary>
    /// And the limit of that: an entry older than the overlap is outside the window the producer
    /// re-reads, so it is not found. Asserted rather than left implicit, because it is the cost
    /// of the design and the number that has to move if it ever bites.
    /// </summary>
    [Fact]
    public async Task AnEntryOlderThanTheOverlapIsOutsideTheWindowThatIsReRead()
    {
        var options = new AuditLogSyncOptions { CatchUp = false, Overlap = TimeSpan.FromMinutes(5) };

        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1)));
        await RunAuditLogAsync(options);

        VRChat.Groups.Add(Entry("gaud_ancient", Now.AddHours(-3), target: "usr_ancient"));
        var run = await RunAuditLogAsync(options);

        Assert.Equal(0, run.FactsWritten);
        Assert.DoesNotContain(await FactsAsync(), f => f.SubjectId == "usr_ancient");
    }

    /// <summary>
    /// VRChat types <c>eventType</c> as a free-form string, so Modbot's table of them is an
    /// observation and not a contract. An unrecognised type is reported loudly, because the
    /// alternative -- dropping it -- leaves a fact log that looks complete and is not.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedEventTypeIsReportedRatherThanDropped()
    {
        VRChat.Groups.Add(
            Entry("gaud_1", Now.AddMinutes(-3), "group.something.new"),
            Entry("gaud_2", Now.AddMinutes(-2), "group.something.new"),
            Entry("gaud_3", Now.AddMinutes(-1), GroupAuditLogEvents.UserBan));

        var run = await RunAuditLogAsync();

        // Still counted as unmapped, so the diagnostics point somebody at the gap -- and now
        // written too. Three facts, not one: the two unrecognised entries are recorded under
        // FactType.Unrecognised with VRChat's own wording in TypeRaw.
        Assert.Equal(2, run.Unmapped);
        Assert.Equal(3, run.FactsWritten);

        var facts = await FactsAsync();
        Assert.Equal(2, facts.Count(f => f.Type == FactType.Unrecognised && f.TypeRaw == "group.something.new"));

        var unmapped = Assert.Single(Diagnostics.UnmappedAuditEvents);

        Assert.Equal("group.something.new", unmapped.EventType);
        Assert.Equal(2, unmapped.Count);

        // One of them, whichever the page happened to present first. The sample exists so an
        // operator can look the real entry up in VRChat, not so a test can pin a page order.
        Assert.Contains(unmapped.SampleEntryId, new[] { "gaud_1", "gaud_2" });
    }

    /// <summary>
    /// An unmapped type still counts as read, so the poll rate does not treat a group that only
    /// does things Modbot has no name for yet as busy -- and the pass still succeeds rather than
    /// failing over something VRChat is entitled to send.
    /// </summary>
    [Fact]
    public async Task APageOfOnlyUnrecognisedEntriesIsRecordedRatherThanDropped()
    {
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1), "group.something.new"));

        var run = await RunAuditLogAsync();

        // Something was written, so this is a productive pass, not a quiet one -- and not a
        // failed one either. An unknown type is a gap in Modbot's vocabulary, not an error.
        Assert.Equal(SyncOutcome.Produced, run.Outcome);

        var fact = Assert.Single(await FactsAsync());
        Assert.Equal(FactType.Unrecognised, fact.Type);
        Assert.Equal("group.something.new", fact.TypeRaw);
    }

    [Fact]
    public async Task AnEmptyAuditLogWritesNothingAndSaysNothingHappened()
    {
        var run = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.Equal(0, run.EntriesRead);
        Assert.Empty(await FactsAsync());
    }

    /// <summary>
    /// An account without audit access gets a 403 on every poll. It must not look like a quiet
    /// group, and it must not advance the cursor past history it never saw.
    /// </summary>
    [Fact]
    public async Task NoAuditAccessIsAFailureAndLeavesTheCursorAlone()
    {
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1)));
        VRChat.Groups.AuditLogStatus = System.Net.HttpStatusCode.Forbidden;

        var run = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.Failed, run.Outcome);
        Assert.Empty(await FactsAsync());
        Assert.Null((await SettingsAsync()).AuditLogSyncedThrough);
    }

    [Fact]
    public async Task AnUnconfiguredDeploymentIssuesNothingAtAll()
    {
        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = null;
            await context.SaveChangesAsync(Ct);
        }

        var run = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.NotConfigured, run.Outcome);
        Assert.Equal(0, VRChat.Groups.AuditLogRequests);
    }

    /// <summary>
    /// The one-off walk through the history VRChat already holds. It is the only history that
    /// exists from before Modbot was installed, and spec 5.1's argument is that it cannot be
    /// retrofitted later.
    /// </summary>
    [Fact]
    public async Task TheCatchUpWalksTheExistingLogAPageAtATimeAndThenStops()
    {
        for (var i = 0; i < 5; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddDays(-i - 1), target: $"usr_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = true, PageSize = 2 };

        // One page per pass, deliberately: a group with a long log must not spend its whole
        // audit-log budget on history while today's bans wait behind it.
        var first = await RunAuditLogAsync(options);
        Assert.Equal(1, first.PagesRead);
        Assert.True(first.CatchingUp);

        await RunAuditLogAsync(options);
        var third = await RunAuditLogAsync(options);

        Assert.False(third.CatchingUp);
        Assert.Equal(5, (await FactsAsync()).Count);
        Assert.True((await SettingsAsync()).AuditLogCatchUpComplete);
    }

    /// <summary>
    /// History never delays today. A catch-up that had priority would walk months of log a page at
    /// a time while this afternoon's bans went unrecorded -- nothing lost, since each fact carries
    /// VRChat's own timestamp, but a freshly installed Modbot showing an empty dashboard for hours.
    /// </summary>
    [Fact]
    public async Task LiveEntriesAreRecordedWhileTheCatchUpIsStillWalkingHistory()
    {
        for (var i = 0; i < 6; i++)
            VRChat.Groups.Add(Entry($"gaud_old_{i}", Now.AddDays(-i - 1), target: $"usr_old_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = true, PageSize = 2 };

        // One pass to find the head of the log; the catch-up still has pages to go after it.
        var first = await RunAuditLogAsync(options);
        Assert.True(first.CatchingUp);

        // Something happens now, with history still only a third read.
        VRChat.Groups.Add(Entry("gaud_live", Now.AddMinutes(-1), target: "usr_live"));

        var next = await RunAuditLogAsync(options);

        Assert.True(next.CatchingUp);
        Assert.Contains(await FactsAsync(), f => f.SubjectId == "usr_live");
    }

    /// <summary>
    /// The cursor the catch-up leaves behind has to be the newest entry in the whole log, not the
    /// newest in the last page it read -- otherwise the first tail poll after it starts hours or
    /// days in the past and re-reads everything.
    /// </summary>
    [Fact]
    public async Task TheCatchUpLeavesTheCursorAtTheHeadOfTheLog()
    {
        for (var i = 0; i < 4; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddDays(-i - 1), target: $"usr_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = true, PageSize = 2 };

        while (!(await SettingsAsync()).AuditLogCatchUpComplete)
            await RunAuditLogAsync(options);

        Assert.Equal(Now.AddDays(-1), (await SettingsAsync()).AuditLogSyncedThrough);
    }

    /// <summary>
    /// Entries from before the partition maintainer's rolling window have nowhere to land unless
    /// the producer makes room. Without this the whole catch-up fails on its first old page, and
    /// the failure looks like a database error rather than a missing partition.
    /// </summary>
    [Fact]
    public async Task CatchUpEntriesFromAnUncoveredMonthAreStillRecorded()
    {
        VRChat.Groups.Add(Entry("gaud_ancient", Now.AddMonths(-8), target: "usr_ancient"));

        await RunAuditLogAsync(new AuditLogSyncOptions { CatchUp = true, PageSize = 10 });

        Assert.Contains(await FactsAsync(), f => f.SubjectId == "usr_ancient");
    }

    // ── group.update from two producers ────────────────────────────────────────────────────

    /// <summary>
    /// <c>vrchat.group.update</c> has two writers: the group-info producer, from its own polling,
    /// and this one, from the audit log. Both facts are real and both are kept. The duplicate
    /// check keys on VRChat's entry id, which only the audit entry carries, so neither the
    /// producer's fact nor a re-read of the entry can be mistaken for the other.
    /// </summary>
    [Fact]
    public async Task AGroupUpdateFromTheAuditLogIsKeptBesideTheGroupInfoProducersAndNeitherIsDoubleCounted()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1), GroupAuditLogEvents.GroupUpdate, target: GroupId));

        var first = await RunAuditLogAsync();
        var second = await RunAuditLogAsync();

        Assert.Equal(1, first.FactsWritten);
        Assert.Equal(0, second.FactsWritten);
        Assert.Equal(1, second.AlreadyRecorded);

        var updates = (await FactsAsync()).Where(f => f.Type == FactType.GroupInfoChanged).ToList();

        Assert.Equal(2, updates.Count);
        Assert.Contains(updates, f => f.Source == FactSource.SyncDiff);
        Assert.Contains(updates, f => f.Source == FactSource.AuditLog);
    }

    // ── The offset cap ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// VRChat refuses audit-log offsets above 7,500 with a 400. The walk must never provoke it:
    /// the last request it issues is at exactly the cap, and reaching it is recorded as the
    /// history horizon rather than as anything having gone wrong.
    /// </summary>
    [Fact]
    public async Task TheCatchUpStopsAtTheOffsetCapWithoutAskingPastIt()
    {
        // Enough that an uncapped walk would ask for offset 7,600.
        const int total = GroupAuditLogSync.AuditLogOffsetCap + 150;
        for (var i = 0; i < total; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddMinutes(-i - 1), target: $"usr_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = true, PageSize = 100 };
        var outcomes = new List<SyncOutcome>();

        while (!(await SettingsAsync()).AuditLogCatchUpComplete)
            outcomes.Add((await RunAuditLogAsync(options)).Outcome);

        var historyOffsets = VRChat.Groups.AuditLogQueries
            .Where(q => q.StartDate is null)
            .Select(q => q.Offset)
            .ToList();

        Assert.Equal(GroupAuditLogSync.AuditLogOffsetCap, historyOffsets.Max());
        Assert.DoesNotContain(historyOffsets, o => o > GroupAuditLogSync.AuditLogOffsetCap);
        Assert.DoesNotContain(SyncOutcome.Failed, outcomes);
        Assert.DoesNotContain(SyncOutcome.RateLimited, outcomes);

        // The page at the cap is read in full, so the horizon is one page past it.
        const int readable = GroupAuditLogSync.AuditLogOffsetCap + 100;
        Assert.NotNull(Diagnostics.HistoryHorizonReached);
        Assert.Equal(readable, Diagnostics.HistoryHorizonReached!.EntriesRead);
        Assert.Equal(readable, (await FactsAsync()).Count);
    }

    /// <summary>
    /// With a page size that does not divide the cap, the last page before it starts at the cap
    /// exactly, so the entries just behind it are still read. The page after that is not asked for.
    /// </summary>
    [Theory]
    [InlineData(7_400, 100, 7_500)]
    [InlineData(7_440, 60, 7_500)]
    [InlineData(7_500, 60, 7_560)]
    [InlineData(0, 60, 60)]
    public void TheNextHistoryPageNeverStartsPastTheCap(int offset, int pageLength, int expected)
        => Assert.Equal(expected, GroupAuditLogSync.NextCatchUpOffset(offset, pageLength));

    /// <summary>
    /// A 400 on a history page is terminal: the walk is marked complete there, the refusal is
    /// recorded as the horizon, and the same offset is never asked for again. It is not a rate
    /// limit -- the next pass still polls the live window. A naive "failed, retry next tick"
    /// would sit on the refused offset forever.
    /// </summary>
    [Fact]
    public async Task A400OnAHistoryPageEndsTheWalkThereWithoutAColdStopOrARetry()
    {
        for (var i = 0; i < 10; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddDays(-i - 1), target: $"usr_{i}"));

        // Below Modbot's own cap, so the arithmetic guard lets offset 4 through and VRChat refuses it.
        VRChat.Groups.OffsetCap = 3;

        var options = new AuditLogSyncOptions { CatchUp = true, PageSize = 2 };
        var outcomes = new List<SyncOutcome>();

        while (!(await SettingsAsync()).AuditLogCatchUpComplete)
            outcomes.Add((await RunAuditLogAsync(options)).Outcome);

        var historyOffsets = VRChat.Groups.AuditLogQueries
            .Where(q => q.StartDate is null)
            .Select(q => q.Offset)
            .ToList();

        Assert.Equal([0, 2, 4], historyOffsets);
        Assert.DoesNotContain(SyncOutcome.RateLimited, outcomes);
        Assert.Equal(4, Diagnostics.HistoryHorizonReached!.EntriesRead);
        Assert.Equal(4, (await FactsAsync()).Count);

        var before = VRChat.Groups.AuditLogQueries.Count;
        await RunAuditLogAsync(options);
        var next = VRChat.Groups.AuditLogQueries.Skip(before).ToList();

        // The live window is still polled -- no cold stop -- and history is not asked for again.
        Assert.Contains(next, q => q.StartDate is not null);
        Assert.DoesNotContain(next, q => q.StartDate is null);
    }

    /// <summary>
    /// The same refusal while reading the live window ends the pass and sends the next one back
    /// to the front of the window rather than to the refused offset. The cursor does not move,
    /// so nothing is skipped; what was recorded before the refusal stays recorded.
    /// </summary>
    [Fact]
    public async Task A400OnTheLiveWindowEndsThePassAndRestartsTheWindowFromTheFront()
    {
        for (var i = 0; i < 10; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddMinutes(-i - 1), target: $"usr_{i}"));

        VRChat.Groups.OffsetCap = 3;

        var options = new AuditLogSyncOptions { CatchUp = false, PageSize = 2, MaxPagesPerRun = 10 };

        var first = await RunAuditLogAsync(options);

        Assert.Equal(SyncOutcome.Failed, first.Outcome);
        Assert.Equal([0, 2, 4], VRChat.Groups.AuditLogQueries.Select(q => q.Offset).ToList());
        Assert.Equal(4, (await FactsAsync()).Count);

        var settings = await SettingsAsync();
        Assert.Equal(0, settings.AuditLogBacklogOffset);
        Assert.Null(settings.AuditLogSyncedThrough);

        var before = VRChat.Groups.AuditLogQueries.Count;
        await RunAuditLogAsync(options);

        Assert.Equal(0, VRChat.Groups.AuditLogQueries[before].Offset);
        Assert.Equal(4, (await FactsAsync()).Count);
    }

    // ── Re-running the walk ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stored walk from an older <see cref="GroupAuditLogSync.CatchUpVersion"/> is walked
    /// again from offset 0. Entries already recorded are recognised by id, so the re-walk writes
    /// nothing twice and costs only the requests.
    /// </summary>
    [Fact]
    public async Task AWalkFromAnOlderVersionIsRunAgainFromTheStartAndWritesNothingTwice()
    {
        for (var i = 0; i < 5; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddDays(-i - 1), target: $"usr_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = true, PageSize = 2 };

        while (!(await SettingsAsync()).AuditLogCatchUpComplete)
            await RunAuditLogAsync(options);

        Assert.Equal(5, (await FactsAsync()).Count);
        Assert.Equal(GroupAuditLogSync.CatchUpVersion, (await SettingsAsync()).AuditLogCatchUpVersion);

        // What a deployment from before this version looks like: finished, and stamped older.
        await using (var context = Database.NewContext())
        {
            var stored = await context.GetSettingsAsync(Ct);
            stored.AuditLogCatchUpVersion = GroupAuditLogSync.CatchUpVersion - 1;
            await context.SaveChangesAsync(Ct);
        }

        var before = VRChat.Groups.AuditLogQueries.Count;
        var written = 0;
        var alreadyRecorded = 0;

        do
        {
            var run = await RunAuditLogAsync(options);
            written += run.FactsWritten;
            alreadyRecorded += run.AlreadyRecorded;
        }
        while (!(await SettingsAsync()).AuditLogCatchUpComplete);

        var historyOffsets = VRChat.Groups.AuditLogQueries
            .Skip(before)
            .Where(q => q.StartDate is null)
            .Select(q => q.Offset)
            .ToList();

        Assert.Equal([0, 2, 4], historyOffsets);
        Assert.Equal(0, written);
        Assert.True(alreadyRecorded >= 5);
        Assert.Equal(5, (await FactsAsync()).Count);
        Assert.Equal(GroupAuditLogSync.CatchUpVersion, (await SettingsAsync()).AuditLogCatchUpVersion);
    }

    [Fact]
    public async Task AWalkAtTheCurrentVersionIsNotRunAgain()
    {
        for (var i = 0; i < 3; i++)
            VRChat.Groups.Add(Entry($"gaud_{i}", Now.AddDays(-i - 1), target: $"usr_{i}"));

        var options = new AuditLogSyncOptions { CatchUp = true, PageSize = 2 };

        while (!(await SettingsAsync()).AuditLogCatchUpComplete)
            await RunAuditLogAsync(options);

        var before = VRChat.Groups.AuditLogQueries.Count;
        await RunAuditLogAsync(options);
        await RunAuditLogAsync(options);

        Assert.DoesNotContain(VRChat.Groups.AuditLogQueries.Skip(before), q => q.StartDate is null);
        Assert.True((await SettingsAsync()).AuditLogCatchUpComplete);
    }

    /// <summary>
    /// A host with the catch-up switched off keeps its stored version, so the walk still happens
    /// the day the catch-up is switched on rather than being stamped done without having run.
    /// </summary>
    [Fact]
    public async Task WithTheCatchUpOffAnOlderVersionIsLeftForLater()
    {
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1)));

        await RunAuditLogAsync(NoCatchUp());

        Assert.Equal(0, (await SettingsAsync()).AuditLogCatchUpVersion);
        Assert.False((await SettingsAsync()).AuditLogCatchUpComplete);
    }
}
