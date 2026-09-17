using Modbot.AI.Insights;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Insights;

/// <summary>The figures an insight is written from, gathered from seeded tables.</summary>
[Collection(nameof(PostgresCollection))]
public class InsightFigureReaderTests : InsightTestBase
{
    public InsightFigureReaderTests(PostgresFixture fixture) : base(fixture) { }

    private static readonly InsightPeriod Week = InsightPeriod.Ending(new DateOnly(2029, 3, 15), InsightKinds.EveryWeek);

    private static InsightFigure Figure(InsightFigures figures, string name) => Assert.Single(figures.Figures, f => f.Name == name);

    [Fact]
    public async Task GroupFiguresSumTheWeekAndTheWeekBefore_AndTakeTheLastHeadcountOfEach()
    {
        await AddTotalAsync(Day(3, 8), DailyTotalMetrics.MembersJoined, 4);
        await AddTotalAsync(Day(3, 14), DailyTotalMetrics.MembersJoined, 6);
        await AddTotalAsync(Day(3, 15), DailyTotalMetrics.MembersJoined, 100); // today: not over, not counted
        await AddTotalAsync(Day(3, 2), DailyTotalMetrics.MembersJoined, 3);
        await AddTotalAsync(Day(3, 9), DailyTotalMetrics.ModeratorApprovals, 2, "vrchat:usr_a");
        await AddTotalAsync(Day(3, 9), DailyTotalMetrics.ModeratorApprovals, 1, "vrchat:usr_b");

        await AddMemberCountAsync(new DateTimeOffset(2029, 3, 6, 10, 0, 0, TimeSpan.Zero), 90);
        await AddMemberCountAsync(new DateTimeOffset(2029, 3, 13, 10, 0, 0, TimeSpan.Zero), 101);
        await AddMemberCountAsync(new DateTimeOffset(2029, 3, 15, 1, 0, 0, TimeSpan.Zero), 500);

        await AddTotalAsync(Day(3, 10), DailyTotalMetrics.WorldVisitors, 7, "wrld_one");
        await AddTotalAsync(Day(3, 11), DailyTotalMetrics.WorldVisitors, 5, "wrld_two");
        await AddTotalAsync(Day(3, 12), DailyTotalMetrics.WorldVisitors, 1, "wrld_one");
        await using (var seed = NewContext())
        {
            seed.VRChatWorlds.Add(new VRChatWorld { WorldId = "wrld_one", Name = "The Great Pug", FirstSeenAt = Start, LastSeenAt = Start });
            await seed.SaveChangesAsync(Ct);
        }

        await using var context = NewContext();
        var figures = await new InsightFigureReader(context).ReadAsync(InsightKinds.Group, Week, Ct);

        Assert.Equal(new InsightFigure("Joined", 10, 3), Figure(figures, "Joined"));
        Assert.Equal(new InsightFigure("Join requests approved", 3, 0), Figure(figures, "Join requests approved"));
        Assert.Equal(new InsightFigure("Members at the end, as VRChat reported", 101, 90), Figure(figures, "Members at the end, as VRChat reported"));

        // Nobody here has ever counted a Discord message, so the figure is left out rather than zero.
        Assert.DoesNotContain(figures.Figures, f => f.Name == "Discord messages");

        var worlds = Assert.Single(figures.Lists);
        Assert.Equal([new InsightListItem("The Great Pug", 8), new InsightListItem("wrld_two", 5)], worlds.Items);
    }

    [Fact]
    public async Task AHeadcountNeverReportedIsNull_NotZero()
    {
        await using var context = NewContext();
        var figures = await new InsightFigureReader(context).ReadAsync(InsightKinds.Group, Week, Ct);

        Assert.Equal(new InsightFigure("Members at the end, as VRChat reported", null, null), Figure(figures, "Members at the end, as VRChat reported"));
    }

    /// <summary>M8 §6: the team kind counts moderators, and never says who.</summary>
    [Fact]
    public async Task TeamFiguresCountModeratorsWithoutNamingThem()
    {
        await AddTotalAsync(Day(3, 9), DailyTotalMetrics.ModeratorBans, 2, "vrchat:usr_secret_one");
        await AddTotalAsync(Day(3, 10), DailyTotalMetrics.ModeratorWarns, 5, "vrchat:usr_secret_one");
        await AddTotalAsync(Day(3, 10), DailyTotalMetrics.ModeratorWarns, 1, "discord:1234567");
        await AddTotalAsync(Day(3, 3), DailyTotalMetrics.ModeratorBans, 1, "vrchat:usr_secret_two");

        await using (var seed = NewContext())
        {
            seed.Reviews.Add(new Review
            {
                ModeratorPlatform = FactPlatform.VRChat,
                ModeratorId = "usr_secret_one",
                Signal = "volume",
                About = "",
                Summary = "",
                WindowStart = Start,
                WindowEnd = Start,
                OpenedAt = new DateTimeOffset(2029, 3, 12, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = Start,
            });
            await seed.SaveChangesAsync(Ct);
        }

        await using var context = NewContext();
        var figures = await new InsightFigureReader(context).ReadAsync(InsightKinds.Team, Week, Ct);

        Assert.Equal(new InsightFigure("All moderator actions", 8, 1), Figure(figures, "All moderator actions"));
        Assert.Equal(new InsightFigure("Moderators who took any action", 2, 1), Figure(figures, "Moderators who took any action"));
        Assert.Equal(new InsightFigure("Warnings", 6, 0), Figure(figures, "Warnings"));
        Assert.Equal(new InsightFigure("Reviews opened", 1, 0), Figure(figures, "Reviews opened"));
        Assert.Empty(figures.Lists);

        var json = figures.ToJson();
        Assert.DoesNotContain("usr_secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstanceFiguresUseTheInstancesOpenedInTheWeek()
    {
        await AddTotalAsync(Day(3, 9), DailyTotalMetrics.InstancesOpened, 3);

        DateTimeOffset At(int day, int hour) => new(2029, 3, day, hour, 0, 0, TimeSpan.Zero);

        await using (var seed = NewContext())
        {
            seed.VRChatWorlds.Add(new VRChatWorld { WorldId = "wrld_club", Name = "Club", FirstSeenAt = Start, LastSeenAt = Start });
            seed.VRChatInstances.AddRange(
                new VRChatInstance { Id = Guid.NewGuid(), Location = "a", WorldId = "wrld_club", OpenedAt = At(9, 20), ClosedAt = At(9, 22), LastSeenAt = At(9, 22), PeakUserCount = 30 },
                new VRChatInstance { Id = Guid.NewGuid(), Location = "b", WorldId = "wrld_club", OpenedAt = At(10, 20), ClosedAt = At(10, 21), LastSeenAt = At(10, 21), PeakUserCount = 12 },
                new VRChatInstance { Id = Guid.NewGuid(), Location = "c", WorldId = "wrld_other", OpenedAt = At(11, 20), ClosedAt = At(11, 23), LastSeenAt = At(11, 23), PeakUserCount = 8 },
                new VRChatInstance { Id = Guid.NewGuid(), Location = "d", WorldId = "wrld_club", OpenedAt = At(2, 20), ClosedAt = At(2, 21), LastSeenAt = At(2, 21), PeakUserCount = 50 });
            await seed.SaveChangesAsync(Ct);
        }

        await using var context = NewContext();
        var figures = await new InsightFigureReader(context).ReadAsync(InsightKinds.Instances, Week, Ct);

        Assert.Equal(new InsightFigure("Instances opened", 3, 0), Figure(figures, "Instances opened"));
        Assert.Equal(new InsightFigure("Typical minutes an instance stayed open", 120, 60), Figure(figures, "Typical minutes an instance stayed open"));
        Assert.Equal(new InsightFigure("Most people in one instance", 30, 50), Figure(figures, "Most people in one instance"));

        var busiest = Assert.Single(figures.Lists, l => l.Name == "Busiest instances, by most people at once");
        Assert.Equal(
            ["Club, opened Friday 9 March 20:00 UTC", "Club, opened Saturday 10 March 20:00 UTC", "wrld_other, opened Sunday 11 March 20:00 UTC"],
            busiest.Items.Select(i => i.Name));
    }
}
