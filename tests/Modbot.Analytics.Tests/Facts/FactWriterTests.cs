using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

[Collection(nameof(PostgresCollection))]
public class FactWriterTests : FactTestBase
{
    public FactWriterTests(PostgresFixture db) : base(db) { }

    [Fact]
    public async Task WriteAsync_StoresEveryColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var clock = new FakeClock(Now);
        var writer = NewWriter(context, clock);
        var subject = UniqueId("usr");

        var result = await writer.WriteAsync(
            new FactRecord
            {
                Type = FactType.InstanceJoined,
                OccurredAt = Now,
                OccurredBefore = Now.AddMinutes(5),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = subject,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = "alice",
                WorldId = "wrld_test",
                InstanceId = "42",
                Source = FactSource.SyncDiff,
                Data = new JsonObject { ["note"] = "hello" },
            },
            ct);

        Assert.False(result.WasDeduplicated);

        var stored = await context.Events.AsNoTracking()
            .SingleAsync(e => e.SubjectId == subject, ct);

        Assert.Equal(result.Id, stored.Id);
        Assert.Equal(Now, stored.OccurredAt);
        Assert.Equal(Now.AddMinutes(5), stored.OccurredBefore);
        Assert.Equal(FactType.InstanceJoined, stored.Type);
        Assert.Equal(FactPlatform.VRChat, stored.SubjectPlatform);
        Assert.Equal(FactPlatform.Modbot, stored.ActorPlatform);
        Assert.Equal("alice", stored.ActorId);
        Assert.Equal("wrld_test", stored.WorldId);
        Assert.Equal("42", stored.InstanceId);
        Assert.Equal(FactSource.SyncDiff, stored.Source);
        Assert.Contains("hello", stored.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeWithALocalOffsetIsStoredAndDeduplicatedAsTheSameInstant()
    {
        // The companion reports what VRChat's log says, and VRChat's log is in the player's
        // local time. The first live batch carried -05:00 and the dedup lookup died with
        // "Cannot write DateTimeOffset with Offset=-05:00" -- PostgreSQL's timestamptz holds an
        // instant and Npgsql insists the offset be zero. Same instant in, same instant out, and a
        // repeat is recognised as a repeat.
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));
        var subject = UniqueId("usr");
        var local = Now.ToOffset(TimeSpan.FromHours(-5));

        FactRecord Fact() => new()
        {
            Type = FactType.InstanceJoined,
            OccurredAt = local,
            OccurredBefore = local.AddMinutes(5),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = "wrld_test",
            InstanceId = "42",
            Source = FactSource.Client,
            Data = new JsonObject(),
        };

        var first = await writer.WriteAsync(Fact(), ct);
        var second = await writer.WriteAsync(Fact(), ct);

        Assert.False(first.WasDeduplicated);
        Assert.True(second.WasDeduplicated);
        Assert.Equal(first.Id, second.Id);

        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.SubjectId == subject, ct);
        Assert.Equal(Now, stored.OccurredAt);
        Assert.Equal(TimeSpan.Zero, stored.OccurredAt.Offset);
    }

    [Fact]
    public async Task ObservedAt_ComesFromTheServerClockNotTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var clock = new FakeClock(Now.AddHours(2));
        var writer = NewWriter(context, clock);
        var subject = UniqueId("usr");

        // The caller says the event happened an hour ago. Spec 4.4: observed_at is the server's
        // own record of when it learned, is authoritative for ordering, and is never taken from
        // a client -- a moderator PC with a wrong clock must not be able to reorder the log.
        await writer.WriteAsync(Presence(subject, Now.AddHours(1)), ct);

        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.SubjectId == subject, ct);

        Assert.Equal(clock.UtcNow, stored.ObservedAt);
        Assert.Equal(Now.AddHours(1), stored.OccurredAt);
    }

    [Fact]
    public async Task OccurredBefore_IsNullForAnExactEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));
        var subject = UniqueId("usr");

        await writer.WriteAsync(Presence(subject, Now), ct);

        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.SubjectId == subject, ct);

        Assert.Null(stored.OccurredBefore);
    }

    [Fact]
    public async Task Data_DefaultsToAnEmptyJsonObject()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));
        var subject = UniqueId("usr");

        await writer.WriteAsync(Presence(subject, Now), ct);

        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.SubjectId == subject, ct);

        Assert.Equal("{}", stored.Data);
    }

    [Fact]
    public async Task ASubjectIdWithNoRecognisableShape_IsStoredVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        // Spec 3.1.1: legacy VRChat ids follow no structured format. A format check would pass
        // review and then silently exclude the oldest members of a community.
        var legacy = $"Mr.Legacy Id_{Guid.NewGuid():N}";

        await writer.WriteAsync(Presence(legacy, Now), ct);

        Assert.Equal(1, await context.Events.CountAsync(e => e.SubjectId == legacy, ct));
    }

    [Fact]
    public async Task WriteManyAsync_ReturnsAResultPerFactInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));
        var a = UniqueId("usr");
        var b = UniqueId("usr");

        var results = await writer.WriteManyAsync(
            [Presence(a, Now), Presence(b, Now)], ct);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.WasDeduplicated));
        Assert.NotEqual(results[0].Id, results[1].Id);
    }

    [Fact]
    public async Task AFactOutsideEveryPartition_FailsLoudly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        // This is why the partition job is mandatory rather than an optimisation: Postgres
        // rejects a row with no matching partition, and the loss would be total and silent if
        // nobody were watching for it.
        var farFuture = Now.AddYears(20);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => writer.WriteAsync(Presence(UniqueId("usr"), farFuture), ct));

        Assert.Contains("partition", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
