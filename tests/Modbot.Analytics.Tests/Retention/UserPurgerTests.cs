using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Retention;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Retention;

/// <summary>
/// Spec 5.5: "a purge-user action erases every fact for one user on request". The claim that
/// deletion works is the one that has to survive contact with a real request.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UserPurgerTests : AnalyticsTestBase
{
    /// <summary>A metric with no fact behind it, counted directly.</summary>
    private const string CountedMetric = "test.counted";

    public UserPurgerTests(PostgresFixture fixture) : base(fixture) { }

    private const string Subject = "usr_purge_me";
    private const string Bystander = "usr_someone_else";

    private UserPurger NewPurger(ModbotContext context) => new(
        context,
        Clock,
        NewJob(context),
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock));

    [Fact]
    public async Task EveryFactAboutTheSubjectGoes_AcrossPartitions()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start.AddDays(-90), subjectId: Subject),
            Fact(FactType.InstanceJoined, Start.AddDays(-40), subjectId: Subject),
            Fact(FactType.InstanceLeft, Start, subjectId: Subject),
            Fact(FactType.MemberJoined, Start, subjectId: Bystander));

        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(3, result.FactsDeleted);
        Assert.Equal(0, await CountAsync(Subject));
        Assert.Equal(1, await CountAsync(Bystander));
    }

    /// <summary>
    /// Imported facts are ordinary facts about the person and go with the rest, and so do the
    /// rows that say "this record was imported" (import design §7) -- otherwise a later upload of
    /// the same file would silently skip exactly the records that were erased.
    /// </summary>
    [Fact]
    public async Task ImportedFacts_AndTheirDedupeRows_GoToo()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start.AddYears(-2), subjectId: Subject, source: FactSource.Import),
            Fact(FactType.Unrecognised, Start.AddYears(-1), subjectId: Subject, source: FactSource.Import),
            Fact(FactType.MemberBanned, Start.AddYears(-2), subjectId: Bystander, source: FactSource.Import));

        await using (var setup = Database.NewContext())
        {
            setup.ImportRecords.AddRange(
                new ImportRecord { Source = "old-bot", Key = "id:ban-1", SubjectPlatform = FactPlatform.VRChat, SubjectId = Subject, ImportedAt = Start },
                new ImportRecord { Source = "old-bot", Key = "hash:abc", SubjectPlatform = FactPlatform.VRChat, SubjectId = Subject, ImportedAt = Start },
                new ImportRecord { Source = "old-bot", Key = "id:ban-2", SubjectPlatform = FactPlatform.VRChat, SubjectId = Bystander, ImportedAt = Start });
            await setup.SaveChangesAsync(Ct);
        }

        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(2, result.FactsDeleted);
        Assert.Equal(0, await CountAsync(Subject));
        Assert.Equal(1, await CountAsync(Bystander));

        await using var read = Database.NewContext();
        var left = await read.ImportRecords.AsNoTracking().OrderBy(r => r.Key).ToListAsync(Ct);
        var only = Assert.Single(left);
        Assert.Equal(Bystander, only.SubjectId);
    }

    /// <summary>
    /// The purge cuts across partitions, which is exactly why it is the one operation that gets to
    /// be a DELETE rather than a partition drop.
    /// </summary>
    [Fact]
    public async Task FactsFromDifferentMonthsAreAllRemoved()
    {
        var months = new[] { Start.AddMonths(-4), Start.AddMonths(-2), Start };

        foreach (var month in months)
            await WriteAsync(Fact(FactType.InstanceJoined, month, subjectId: Subject));

        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(months.Length, result.FactsDeleted);
        Assert.Equal(0, await CountAsync(Subject));
    }

    /// <summary>
    /// A moderator's ban is a record about the person banned. Erasing it because the moderator
    /// asked would delete someone else's moderation history, and the accountability in spec 5.8 is
    /// built entirely on those rows.
    /// </summary>
    [Fact]
    public async Task FactsWhereTheyWereTheActorAreKept()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start, subjectId: Bystander, actorId: Subject),
            Fact(FactType.MemberJoined, Start, subjectId: Subject));

        await using var context = Database.NewContext();
        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(0, await CountAsync(Subject));

        var survivor = await context.Events
            .AsNoTracking()
            .SingleAsync(e => e.Type == FactType.MemberBanned, Ct);

        Assert.Equal(Subject, survivor.ActorId);
        Assert.Equal(Bystander, survivor.SubjectId);
    }

    /// <summary>
    /// Aggregates that still counted the purged facts would make the deletion cosmetic.
    /// </summary>
    [Fact]
    public async Task DailyTotalsNoLongerCountThePurgedFacts()
    {
        await WriteAsync(
            Fact(FactType.MemberJoined, Start, subjectId: Subject),
            Fact(FactType.MemberJoined, Start, subjectId: Bystander),
            Fact(FactType.MemberJoined, Start.AddDays(1), subjectId: Bystander));

        await using var context = Database.NewContext();
        await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));

        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersJoined));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersNet));

        // And the running total after the purged day moves with it.
        Assert.Equal(2m, await ValueAsync(DayOf(Start.AddDays(1)), DailyTotalMetrics.MembersNet));
    }

    /// <summary>
    /// The counted-only rows are the ones with no fact behind them (spec 5.2.1) -- a per-user
    /// message count is precisely the personal data a purge is about, and nothing else would
    /// remove it.
    /// </summary>
    [Fact]
    public async Task PerUserCountsWithNoFactBehindThemAreErased()
    {
        await using var context = Database.NewContext();
        var counter = new DailyTotalCounter(context, Clock);

        await counter.IncrementAsync(
            CountedMetric,
            DailyTotalDimensions.ForUser(FactPlatform.VRChat, Subject),
            120m,
            ct: Ct);

        await counter.IncrementAsync(
            CountedMetric,
            DailyTotalDimensions.ForUser(FactPlatform.VRChat, Bystander),
            7m,
            ct: Ct);

        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        Assert.Equal(1, result.CountedDailyTotalsDeleted);

        var today = DayOf(Clock.UtcNow);
        Assert.Null(await ValueAsync(
            today, CountedMetric, DailyTotalDimensions.ForUser(FactPlatform.VRChat, Subject)));
        Assert.Equal(7m, await ValueAsync(
            today, CountedMetric, DailyTotalDimensions.ForUser(FactPlatform.VRChat, Bystander)));
    }

    /// <summary>
    /// Computed rows that follow from facts the purge did not touch stay, because a rebuild would
    /// only put them back. Deleting them would be theatre and would break the invariant.
    /// </summary>
    [Fact]
    public async Task ModeratorTotalsForSurvivingFactsAreLeftIntact()
    {
        await WriteAsync(Fact(FactType.MemberBanned, Start, subjectId: Bystander, actorId: Subject));

        await using var context = Database.NewContext();
        await NewJob(context).RebuildAsync(Ct);
        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        var dimension = DailyTotalDimensions.ForUser(FactPlatform.VRChat, Subject);
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.ModeratorBans, dimension));
    }

    /// <summary>
    /// An erasure log that names the erased person is not an erasure.
    /// </summary>
    [Fact]
    public async Task ThePurgeIsRecordedWithoutNamingWhoWasPurged()
    {
        await WriteAsync(Fact(FactType.InstanceJoined, Start, subjectId: Subject));

        await using var context = Database.NewContext();
        await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Subject, ct: Ct);

        var record = await context.Events
            .AsNoTracking()
            .SingleAsync(e => e.Type == FactType.UserPurged, Ct);

        Assert.Equal(FactSource.Modbot, record.Source);
        Assert.DoesNotContain(Subject, record.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(Subject, record.SubjectId, StringComparison.Ordinal);
        Assert.Contains("\"facts\": 1", record.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgingSomeoneWithNoFactsIsHarmless()
    {
        await using var context = Database.NewContext();
        var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, "usr_nobody", ct: Ct);

        Assert.Equal(0, result.FactsDeleted);
        Assert.Equal(0, result.DaysRecomputed);
    }

    private async Task<int> CountAsync(string subjectId)
    {
        await using var context = Database.NewContext();
        return await context.Events.AsNoTracking().CountAsync(e => e.SubjectId == subjectId, Ct);
    }
}
