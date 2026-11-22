using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Repairs;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Repairs;

/// <summary>
/// The one-off repair of the false "left the group" facts the member sync wrote on 2026-11-21.
/// Which facts it picks is the whole risk: a real leave removed is history lost.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class FalseLeaveRepairTests : AnalyticsTestBase
{
    private const string Group = "grp_repair";

    public FalseLeaveRepairTests(PostgresFixture fixture) : base(fixture) { }

    private FalseLeaveRepair NewRepair(ModbotContext context)
        => new(context, NewJob(context), Microsoft.Extensions.Logging.Abstractions.NullLogger<FalseLeaveRepair>.Instance);

    private static FactRecord Leave(string subject, DateTimeOffset at, FactSource source = FactSource.SyncDiff)
        => Fact(FactType.MemberLeft, at, subjectId: subject, source: source)
            with { Data = new JsonObject { ["groupId"] = Group } };

    private async Task AddMemberAsync(string userId, DateTimeOffset? joinedAt, DateTimeOffset? leftAt = null)
    {
        await using var context = Database.NewContext();

        context.GroupMembers.Add(new GroupMember
        {
            GroupId = Group,
            UserId = userId,
            JoinedAt = joinedAt,
            LeftAt = leftAt,
            FirstSeenAt = Start.AddDays(-60),
            LastSeenAt = Start,
        });

        await context.SaveChangesAsync(Ct);
    }

    private async Task<IReadOnlyList<string>> LeaveSubjectsAsync()
    {
        await using var context = Database.NewContext();

        return await context.Events.AsNoTracking()
            .Where(e => e.Type == FactType.MemberLeft)
            .Select(e => e.SubjectId)
            .OrderBy(s => s)
            .ToListAsync(Ct);
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var context = Database.NewContext();

        return (await context.Database
            .SqlQuery<bool>($"SELECT to_regclass({table}) IS NOT NULL AS \"Value\"")
            .ToListAsync(Ct))[0];
    }

    [Fact]
    public async Task CurrentMemberWithOlderJoinDate_HasTheFalseLeaveRemoved()
    {
        await WriteAsync(Leave("usr_false", Start));
        await AddMemberAsync("usr_false", Start.AddDays(-30));

        await using var context = Database.NewContext();
        var result = await NewRepair(context).RunAsync(Ct);

        Assert.True(result.Ran);
        Assert.Equal(1, result.FactsDeleted);
        Assert.Empty(await LeaveSubjectsAsync());
    }

    [Fact]
    public async Task RejoinedMemberWithNewerJoinDate_KeepsTheirLeave()
    {
        await WriteAsync(Leave("usr_rejoined", Start));
        await AddMemberAsync("usr_rejoined", Start.AddDays(2));

        await using var context = Database.NewContext();
        var result = await NewRepair(context).RunAsync(Ct);

        Assert.True(result.Ran);
        Assert.Equal(0, result.FactsDeleted);
        Assert.Equal(["usr_rejoined"], await LeaveSubjectsAsync());
    }

    [Fact]
    public async Task MemberWhoIsGone_KeepsTheirLeave()
    {
        await WriteAsync(Leave("usr_gone", Start));
        await AddMemberAsync("usr_gone", Start.AddDays(-30), leftAt: Start);

        await using var context = Database.NewContext();
        var result = await NewRepair(context).RunAsync(Ct);

        Assert.Equal(0, result.FactsDeleted);
        Assert.Equal(["usr_gone"], await LeaveSubjectsAsync());
    }

    [Fact]
    public async Task LeaveFromTheAuditLog_IsKept()
    {
        await WriteAsync(Leave("usr_audit", Start, FactSource.AuditLog));
        await AddMemberAsync("usr_audit", Start.AddDays(-30));

        await using var context = Database.NewContext();
        var result = await NewRepair(context).RunAsync(Ct);

        Assert.Equal(0, result.FactsDeleted);
        Assert.Equal(["usr_audit"], await LeaveSubjectsAsync());
    }

    [Fact]
    public async Task LeaveNoticedBeforeTheBugsDay_IsKept()
    {
        // observed_at is stamped from the clock when the fact is written.
        Clock.UtcNow = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        await WriteAsync(Leave("usr_early", Clock.UtcNow.AddHours(-1)));
        Clock.UtcNow = Start;

        await AddMemberAsync("usr_early", Start.AddYears(-5));

        await using var context = Database.NewContext();
        var result = await NewRepair(context).RunAsync(Ct);

        Assert.Equal(0, result.FactsDeleted);
        Assert.Equal(["usr_early"], await LeaveSubjectsAsync());
    }

    [Fact]
    public async Task RunTwice_SecondRunRefusesAndChangesNothing()
    {
        await WriteAsync(Leave("usr_false", Start), Leave("usr_false_two", Start));
        await AddMemberAsync("usr_false", Start.AddDays(-30));
        await AddMemberAsync("usr_false_two", Start.AddDays(-30));

        await using (var first = Database.NewContext())
        {
            var result = await NewRepair(first).RunAsync(Ct);
            Assert.True(result.Ran);
            Assert.Equal(2, result.FactsDeleted);
        }

        // A new false leave appears after the first run: the second run still must not touch it.
        await WriteAsync(Leave("usr_false_three", Start));
        await AddMemberAsync("usr_false_three", Start.AddDays(-30));

        await using var second = Database.NewContext();
        var again = await NewRepair(second).RunAsync(Ct);

        Assert.False(again.Ran);
        Assert.Equal(0, again.FactsDeleted);
        Assert.Equal(["usr_false_three"], await LeaveSubjectsAsync());
        Assert.True((await NewRepair(second).PreviewAsync(Ct)).AlreadyRan);
    }

    [Fact]
    public async Task Preview_CountsAndChangesNothing()
    {
        await WriteAsync(
            Leave("usr_false", Start),
            Leave("usr_rejoined", Start),
            Leave("usr_audit", Start, FactSource.AuditLog));
        await AddMemberAsync("usr_false", Start.AddDays(-30));
        await AddMemberAsync("usr_rejoined", Start.AddDays(2));
        await AddMemberAsync("usr_audit", Start.AddDays(-30));

        await using (var job = Database.NewContext())
            await NewJob(job).RunIncrementalAsync(Ct);

        var totalsBefore = await SnapshotAsync();

        await using var context = Database.NewContext();
        var preview = await NewRepair(context).PreviewAsync(Ct);

        Assert.False(preview.AlreadyRan);
        Assert.Equal(2, preview.LeaveFactsInWindow);
        Assert.Equal(1, preview.Qualifying);
        Assert.Equal(1, preview.Days);
        Assert.Equal(["usr_false"], preview.SampleSubjects);

        Assert.Equal(["usr_audit", "usr_false", "usr_rejoined"], await LeaveSubjectsAsync());
        Assert.Equal(totalsBefore, await SnapshotAsync());
        Assert.False(await TableExistsAsync(FalseLeaveRepair.FactsBackupTable));
        Assert.False(await TableExistsAsync(FalseLeaveRepair.LinkedFactsBackupTable));
        Assert.False(await TableExistsAsync(FalseLeaveRepair.EventReportsBackupTable));
    }

    [Fact]
    public async Task LinkedFactsAndEventReports_GoWithTheFact_AndAreCopiedFirst()
    {
        await WriteAsync(Leave("usr_false", Start), Leave("usr_kept", Start));
        await AddMemberAsync("usr_false", Start.AddDays(-30));
        await AddMemberAsync("usr_kept", Start.AddDays(2));

        await using (var setup = Database.NewContext())
        {
            var facts = await setup.Events.AsNoTracking().ToListAsync(Ct);
            var gone = facts.Single(f => f.SubjectId == "usr_false");
            var kept = facts.Single(f => f.SubjectId == "usr_kept");

            setup.LinkedFacts.AddRange(
                new LinkedFact { FactId = gone.Id, OccurredAt = gone.OccurredAt, MainFactId = kept.Id, MainOccurredAt = kept.OccurredAt, LinkedAt = Start },
                new LinkedFact { FactId = kept.Id, OccurredAt = kept.OccurredAt, MainFactId = gone.Id, MainOccurredAt = gone.OccurredAt, LinkedAt = Start });

            setup.EventReports.AddRange(
                new EventReport { FactId = gone.Id, OccurredAt = gone.OccurredAt, DeviceId = Guid.NewGuid(), ReportedAt = Start },
                new EventReport { FactId = kept.Id, OccurredAt = kept.OccurredAt, DeviceId = Guid.NewGuid(), ReportedAt = Start });

            await setup.SaveChangesAsync(Ct);
        }

        await using var context = Database.NewContext();
        var preview = await NewRepair(context).PreviewAsync(Ct);

        Assert.Equal(1, preview.LinkedFacts);
        Assert.Equal(1, preview.EventReports);

        var result = await NewRepair(context).RunAsync(Ct);

        Assert.Equal(1, result.FactsDeleted);
        Assert.Equal(1, result.LinkedFactsDeleted);
        Assert.Equal(1, result.EventReportsDeleted);

        await using var read = Database.NewContext();
        Assert.Equal(1, await read.LinkedFacts.AsNoTracking().CountAsync(Ct));
        Assert.Equal(1, await read.EventReports.AsNoTracking().CountAsync(Ct));

        var backedUp = await read.Database
            .SqlQueryRaw<int>($"SELECT (SELECT COUNT(*) FROM {FalseLeaveRepair.FactsBackupTable})::int + (SELECT COUNT(*) FROM {FalseLeaveRepair.LinkedFactsBackupTable})::int + (SELECT COUNT(*) FROM {FalseLeaveRepair.EventReportsBackupTable})::int AS \"Value\"")
            .ToListAsync(Ct);

        Assert.Equal(3, backedUp[0]);
    }

    [Fact]
    public async Task DailyTotalsForTheDay_AreWorkedOutAgain()
    {
        await WriteAsync(Leave("usr_false", Start), Leave("usr_real", Start));
        await AddMemberAsync("usr_false", Start.AddDays(-30));
        await AddMemberAsync("usr_real", Start.AddDays(2));

        await using (var job = Database.NewContext())
            await NewJob(job).RunIncrementalAsync(Ct);

        Assert.Equal(2m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersLeft));

        await using var context = Database.NewContext();
        var result = await NewRepair(context).RunAsync(Ct);

        Assert.Equal(1, result.DaysRecomputed);
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.MembersLeft));
    }
}
