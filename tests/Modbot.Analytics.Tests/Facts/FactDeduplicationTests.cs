using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

/// <summary>
/// Spec 5.7. Six moderator clients in one instance all see the same person join and all report
/// it. Without deduplication the log inflates sixfold and time-spent totals sextuple-count --
/// and nothing errors, so the failure is invisible until somebody notices a giveaway draw is
/// weighted wrong.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class FactDeduplicationTests : FactTestBase
{
    public FactDeduplicationTests(PostgresFixture db) : base(db) { }

    private async Task<int> CountAsync(string subjectId, CancellationToken ct)
    {
        await using var context = Db.NewContext();
        return await context.Events.CountAsync(e => e.SubjectId == subjectId, ct);
    }

    [Fact]
    public async Task SixClientsReportingOneJoin_ProduceOneFact()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        // Jittered the way real clients are after clock synchronisation: close, never identical.
        double[] jitterSeconds = [0, 0.4, -1.2, 2.9, -2.1, 1.7];

        foreach (var jitter in jitterSeconds)
        {
            await using var context = Db.NewContext();
            var writer = NewWriter(context, new FakeClock(Now));
            await writer.WriteAsync(Presence(subject, Now.AddSeconds(jitter)), ct);
        }

        Assert.Equal(1, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task SixClientsReportingOneJoin_AllReceiveTheSameFactId()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");
        var results = new List<FactWriteResult>();

        foreach (var jitter in new[] { 0, 1, -1, 2, -2, 3 })
        {
            await using var context = Db.NewContext();
            var writer = NewWriter(context, new FakeClock(Now));
            results.Add(await writer.WriteAsync(Presence(subject, Now.AddSeconds(jitter)), ct));
        }

        // Callers get the id of the fact that won, so a client can still correlate its report.
        Assert.All(results, r => Assert.Equal(results[0].Id, r.Id));
        Assert.False(results[0].WasDeduplicated);
        Assert.All(results.Skip(1), r => Assert.True(r.WasDeduplicated));
    }

    [Fact]
    public async Task ReportsStraddlingABucketBoundary_StillDeduplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        // The case that killed the floor(timestamp / bucket) design: 14:00:04.9 and 14:00:05.1
        // fall in different five-second buckets and would both have been written. They are 0.2 s
        // apart. A windowed check does not care where the boundaries are.
        await WriteAsync(subject, Now.AddSeconds(4.9), ct);
        await WriteAsync(subject, Now.AddSeconds(5.1), ct);

        Assert.Equal(1, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task AGenuineRejoinFifteenSecondsLater_ProducesTwoFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        // The floor: a leave-and-rejoin can complete in 15 s on a fast cached world. The window
        // has to stay well inside that or real rejoins vanish from the log.
        await WriteAsync(subject, Now, ct);
        await WriteAsync(subject, Now.AddSeconds(15), ct);

        Assert.Equal(2, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task ReportsExactlyTheWindowApart_Deduplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        await WriteAsync(subject, Now, ct);
        await WriteAsync(subject, Now.AddSeconds(5), ct);

        Assert.Equal(1, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task ReportsJustOutsideTheWindow_DoNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        await WriteAsync(subject, Now, ct);
        await WriteAsync(subject, Now.AddSeconds(5.001), ct);

        Assert.Equal(2, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task AJoinAndALeaveAtTheSameInstant_AreDistinctFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        await WriteAsync(subject, Now, ct, type: FactType.InstanceJoined);
        await WriteAsync(subject, Now, ct, type: FactType.InstanceLeft);

        Assert.Equal(2, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task TheSameUserInTwoInstances_IsNotCollapsed()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        await WriteAsync(subject, Now, ct, instanceId: "instance-1");
        await WriteAsync(subject, Now, ct, instanceId: "instance-2");

        Assert.Equal(2, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task TwoUsersJoiningTogether_AreNotCollapsed()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = UniqueId("usr");
        var b = UniqueId("usr");

        await WriteAsync(a, Now, ct);
        await WriteAsync(b, Now, ct);

        Assert.Equal(1, await CountAsync(a, ct));
        Assert.Equal(1, await CountAsync(b, ct));
    }

    [Fact]
    public async Task ConcurrentReportsOfOneJoin_StillProduceOneFact()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        // The reason dedup is not a naive check-then-insert: six clients reporting at once would
        // all read "no existing fact" before any of them wrote. Serialisation comes from a
        // transaction-scoped advisory lock keyed on the logical event, so only reports of the
        // *same* event queue behind each other.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var context = Db.NewContext();
            var writer = NewWriter(context, new FakeClock(Now));
            return await writer.WriteAsync(Presence(subject, Now.AddSeconds(i * 0.3)), ct);
        }));

        Assert.Equal(1, await CountAsync(subject, ct));
        Assert.Single(results, r => !r.WasDeduplicated);
        Assert.All(results, r => Assert.Equal(results[0].Id, r.Id));
    }

    [Fact]
    public async Task ConcurrentReportsOfDifferentEvents_DoNotBlockEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var subjects = Enumerable.Range(0, 8).Select(_ => UniqueId("usr")).ToArray();

        await Task.WhenAll(subjects.Select(async subject =>
        {
            await using var context = Db.NewContext();
            var writer = NewWriter(context, new FakeClock(Now));
            await writer.WriteAsync(Presence(subject, Now), ct);
        }));

        foreach (var subject in subjects)
            Assert.Equal(1, await CountAsync(subject, ct));
    }

    [Fact]
    public async Task NonClientFacts_AreNeverDeduplicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        // Modbot's own audit entries share the fact table (spec 5.9). Two settings changes a
        // second apart are two things that happened, and collapsing them would quietly delete
        // audit history -- which is the opposite of what an audit log is for.
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        await writer.WriteAsync(Audit(subject, Now), ct);
        await writer.WriteAsync(Audit(subject, Now.AddSeconds(1)), ct);

        Assert.Equal(2, await CountAsync(subject, ct));
    }

    private async Task WriteAsync(
        string subjectId,
        DateTimeOffset at,
        CancellationToken ct,
        string instanceId = "instance-1",
        string type = FactType.InstanceJoined)
    {
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));
        await writer.WriteAsync(Presence(subjectId, at, instanceId, type), ct);
    }

    private static FactRecord Audit(string subjectId, DateTimeOffset at) => new()
    {
        Type = FactType.SettingsChanged,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.Modbot,
        SubjectId = subjectId,
        ActorPlatform = FactPlatform.Modbot,
        ActorId = "alice",
        Source = FactSource.Modbot,
    };
}
