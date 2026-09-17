using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Retention;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Retention;

/// <summary>
/// Spec 5.5: retention tiered by fact class, enforced by destroying partitions rather than by
/// deleting rows.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RetentionPrunerTests : AnalyticsTestBase
{
    public RetentionPrunerTests(PostgresFixture fixture) : base(fixture) { }

    /// <summary>Comfortably past any window these tests configure, and in an earlier month.</summary>
    private static DateTimeOffset Old => Start.AddDays(-200);

    private RetentionPruner NewPruner(Core.Data.ModbotContext context) => new(
        context,
        Clock,
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock));

    private static string OldPartition => EventPartitionMaintainer.PartitionName(Old);

    [Fact]
    public async Task PresenceFactsAgeOutWhileModerationFactsAreKept()
    {
        await SetRetentionAsync(moderationDays: 0, presenceDays: 90);
        await WriteAsync(
            Fact(FactType.MemberBanned, Old, actorId: "alice"),
            Fact(FactType.InstanceJoined, Old.AddHours(1)),
            Fact(FactType.InstanceLeft, Old.AddHours(2)));

        var before = await PartitionIdAsync(OldPartition);

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Contains(OldPartition, result.MovedOut);
        Assert.Empty(result.Dropped);

        Assert.Equal(1, await CountAsync(FactType.MemberBanned));
        Assert.Equal(0, await CountAsync(FactType.InstanceJoined));
        Assert.Equal(0, await CountAsync(FactType.InstanceLeft));

        // The row count is not the interesting part: the table the expired rows lived in is gone,
        // replaced by a new one. Had this been a mass DELETE the identity would be unchanged --
        // and so would the dead tuples and the vacuum bill the partitioning exists to avoid.
        var after = await PartitionIdAsync(OldPartition);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task TheReplacementPartitionStillAcceptsFactsForItsMonth()
    {
        await SetRetentionAsync(moderationDays: 0, presenceDays: 90);
        await WriteAsync(
            Fact(FactType.MemberBanned, Old),
            Fact(FactType.InstanceJoined, Old));

        await using (var context = Database.NewContext())
            await NewPruner(context).PruneAsync(Ct);

        // An insert with no matching partition fails outright, so this is the check that the
        // replacement went back with the right bounds and is actually attached.
        await WriteAsync(Fact(FactType.MemberKicked, Old.AddDays(1)));

        Assert.Equal(1, await CountAsync(FactType.MemberKicked));
    }

    [Fact]
    public async Task NothingInsideTheRetentionWindowIsTouched()
    {
        await SetRetentionAsync(moderationDays: 90, presenceDays: 90);
        var recent = Start.AddDays(-30);

        await WriteAsync(
            Fact(FactType.MemberBanned, recent),
            Fact(FactType.InstanceJoined, recent));

        var partition = EventPartitionMaintainer.PartitionName(recent);
        var before = await PartitionIdAsync(partition);

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Empty(result.Dropped);
        Assert.Empty(result.MovedOut);
        Assert.Equal(1, await CountAsync(FactType.InstanceJoined));
        Assert.Equal(before, await PartitionIdAsync(partition));
    }

    /// <summary>
    /// A partition is only droppable when <em>every</em> fact in it is past retention -- which,
    /// when both classes are configured and the month is older than both, it is.
    /// </summary>
    [Fact]
    public async Task APartitionGoesEntirelyWhenEveryClassIsPastRetention()
    {
        await SetRetentionAsync(moderationDays: 120, presenceDays: 90);

        await WriteAsync(
            Fact(FactType.MemberBanned, Old),
            Fact(FactType.InstanceJoined, Old));

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Contains(OldPartition, result.Dropped);
        Assert.Empty(result.MovedOut);
        Assert.Null(await PartitionIdAsync(OldPartition));
        Assert.Equal(0, await CountAsync(FactType.MemberBanned));
    }

    /// <summary>
    /// The window is only half expired here -- moderation at 300 days still covers this month --
    /// so the partition survives with its moderation facts and loses its presence ones.
    /// </summary>
    [Fact]
    public async Task AClassStillInsideItsWindowKeepsThePartitionAlive()
    {
        await SetRetentionAsync(moderationDays: 300, presenceDays: 90);

        await WriteAsync(
            Fact(FactType.MemberBanned, Old),
            Fact(FactType.InstanceJoined, Old));

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Contains(OldPartition, result.MovedOut);
        Assert.Equal(1, await CountAsync(FactType.MemberBanned));
        Assert.Equal(0, await CountAsync(FactType.InstanceJoined));
    }

    /// <summary>
    /// Spec 5.5: <b>Modbot has no default retention window.</b> A deployment nobody has configured
    /// keeps every fact forever.
    /// </summary>
    /// <remarks>
    /// This is the regression guard for the setting itself rather than for the pruner. An earlier
    /// draft defaulted presence facts to 90 days, which quietly destroyed the history that
    /// section 5.1 says cannot be filled in later -- and every other test in this file would have gone
    /// on passing, because they configured their own window anyway. Nothing here is seeded into
    /// <c>Settings</c> on purpose: this is what a fresh install does.
    /// </remarks>
    [Fact]
    public async Task AnUnconfiguredDeploymentPrunesNothing()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Old),
            Fact(FactType.InstanceJoined, Old),
            Fact(FactType.InstanceLeft, Old.AddDays(-400)));

        var before = await PartitionIdAsync(OldPartition);

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Empty(result.Dropped);
        Assert.Empty(result.MovedOut);
        Assert.Equal(1, await CountAsync(FactType.InstanceJoined));
        Assert.Equal(1, await CountAsync(FactType.InstanceLeft));
        Assert.Equal(before, await PartitionIdAsync(OldPartition));
    }

    [Fact]
    public async Task ZeroDaysMeansForever()
    {
        await SetRetentionAsync(moderationDays: 0, presenceDays: 0);

        await WriteAsync(
            Fact(FactType.MemberBanned, Old),
            Fact(FactType.InstanceJoined, Old));

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Empty(result.Dropped);
        Assert.Empty(result.MovedOut);
        Assert.Equal(1, await CountAsync(FactType.InstanceJoined));
    }

    /// <summary>
    /// Once the expired rows are gone the partition holds nothing expired, so a second run must
    /// find nothing to do. A pruner that rewrote every old partition on every pass would copy the
    /// whole moderation history daily for no reason.
    /// </summary>
    [Fact]
    public async Task RunningAgainRewritesNothing()
    {
        await SetRetentionAsync(moderationDays: 0, presenceDays: 90);
        await WriteAsync(
            Fact(FactType.MemberBanned, Old),
            Fact(FactType.InstanceJoined, Old));

        await using var context = Database.NewContext();
        await NewPruner(context).PruneAsync(Ct);

        var identity = await PartitionIdAsync(OldPartition);
        var second = await NewPruner(context).PruneAsync(Ct);

        Assert.Empty(second.Dropped);
        Assert.Empty(second.MovedOut);
        Assert.Equal(identity, await PartitionIdAsync(OldPartition));
    }

    /// <summary>
    /// Spec 5.5: "charts therefore keep their full history even after the underlying events age
    /// out". The daily totals are the only copy of that history once the facts are gone.
    /// </summary>
    [Fact]
    public async Task DailyTotalsOutliveTheFactsTheyWereComputedFrom()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Old),
            Fact(FactType.InstanceJoined, Old));

        await using var context = Database.NewContext();
        await NewJob(context).RebuildAsync(Ct);

        var before = await SnapshotAsync();
        Assert.NotEmpty(before);

        await SetRetentionAsync(moderationDays: 120, presenceDays: 90);
        await NewPruner(context).PruneAsync(Ct);

        Assert.Null(await PartitionIdAsync(OldPartition));
        Assert.Equal(before, await SnapshotAsync());
    }

    /// <summary>
    /// Spec 5.9.2 lists retention pruning as something Modbot records about itself, and there is
    /// no second audit system to record it in.
    /// </summary>
    [Fact]
    public async Task WhatWasDestroyedIsRecordedInTheFactLog()
    {
        await SetRetentionAsync(moderationDays: 0, presenceDays: 90);
        await WriteAsync(
            Fact(FactType.MemberBanned, Old),
            Fact(FactType.InstanceJoined, Old));

        await using var context = Database.NewContext();
        await NewPruner(context).PruneAsync(Ct);

        var record = await context.Events
            .AsNoTracking()
            .SingleAsync(e => e.Type == FactType.RetentionPruned, Ct);

        Assert.Equal(FactSource.Modbot, record.Source);
        Assert.Contains(OldPartition, record.Data, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whoever attached a partition this job cannot name knows something it does not, and
    /// <c>DROP TABLE</c> on a guess is not recoverable.
    /// </summary>
    [Fact]
    public async Task APartitionNotNamedByTheMaintainerIsLeftAlone()
    {
        await SetRetentionAsync(moderationDays: 90, presenceDays: 90);
        var month = new DateTimeOffset(2019, 5, 1, 0, 0, 0, TimeSpan.Zero);

        await using var context = Database.NewContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE modbot_event_archive_2019_05
                PARTITION OF modbot_event
                FOR VALUES FROM ('2019-05-01 00:00:00+00') TO ('2019-06-01 00:00:00+00')
            """,
            Ct);

        // Written straight into the table: the partition maintainer would refuse to create its
        // own May 2019 partition on top of this one, which is the situation being set up.
        context.Events.Add(new ModbotEvent
        {
            OccurredAt = month.AddDays(3),
            ObservedAt = Clock.UtcNow,
            Type = FactType.InstanceJoined,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_archived",
            Source = FactSource.Client,
        });

        await context.SaveChangesAsync(Ct);

        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Empty(result.Dropped);
        Assert.Empty(result.MovedOut);
        Assert.Equal(1, await CountAsync(FactType.InstanceJoined));
    }

    /// <summary>
    /// The group member count readings follow the presence window: the same kind of thing --
    /// high-rate operational readings -- and not the moderation record.
    /// </summary>
    [Fact]
    public async Task MemberCountReadingsOlderThanThePresenceWindowAreDeleted()
    {
        await SetRetentionAsync(moderationDays: 0, presenceDays: 90);
        await AddReadingsAsync(Start.AddDays(-100), Start.AddDays(-91), Start.AddDays(-89), Start.AddDays(-1));

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Equal(2, result.MemberCountsDeleted);
        Assert.Equal([Start.AddDays(-89), Start.AddDays(-1)], await ReadingTimesAsync());
    }

    [Fact]
    public async Task MemberCountReadingsAreKeptForeverWithoutAPresenceWindow()
    {
        await SetRetentionAsync(moderationDays: 90, presenceDays: 0);
        await AddReadingsAsync(Start.AddDays(-400), Start.AddDays(-1));

        await using var context = Database.NewContext();
        var result = await NewPruner(context).PruneAsync(Ct);

        Assert.Equal(0, result.MemberCountsDeleted);
        Assert.Equal(2, (await ReadingTimesAsync()).Count);
    }

    private async Task AddReadingsAsync(params DateTimeOffset[] times)
    {
        await using var context = Database.NewContext();

        context.GroupMemberCounts.AddRange(times.Select(at => new GroupMemberCount
        {
            GroupId = "grp_test",
            CountedAt = at,
            MemberCount = 100,
            OnlineMemberCount = 5,
        }));

        await context.SaveChangesAsync(Ct);
    }

    private async Task<IReadOnlyList<DateTimeOffset>> ReadingTimesAsync()
    {
        await using var context = Database.NewContext();

        return await context.GroupMemberCounts.AsNoTracking()
            .OrderBy(r => r.CountedAt)
            .Select(r => r.CountedAt)
            .ToListAsync(Ct);
    }

    private async Task SetRetentionAsync(int moderationDays, int presenceDays)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        settings.ModerationFactRetentionDays = moderationDays;
        settings.PresenceFactRetentionDays = presenceDays;

        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> CountAsync(string type)
    {
        await using var context = Database.NewContext();
        return await context.Events.AsNoTracking().CountAsync(e => e.Type == type, Ct);
    }

    /// <summary>
    /// The table's own identity. A new number means a new table -- the distinction between
    /// dropping rows' home and deleting the rows.
    /// </summary>
    private async Task<long?> PartitionIdAsync(string name)
    {
        await using var context = Database.NewContext();

        var found = await context.Database
            .SqlQuery<long?>($"SELECT to_regclass({name})::oid::bigint AS \"Value\"")
            .ToListAsync(Ct);

        return found.Count > 0 ? found[0] : null;
    }
}
