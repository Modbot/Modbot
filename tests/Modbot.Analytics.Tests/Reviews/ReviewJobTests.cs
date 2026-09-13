using System.Text.Json;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Reviews;

/// <summary>
/// Spec 5.8.5: each check fires on its hand-built scenario, stays quiet on the ordinary one, does
/// not multiply on a re-run, and asks again only on new evidence after a review is closed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ReviewJobTests : ReviewTestBase
{
    private const string World = "wrld_a";

    public ReviewJobTests(PostgresFixture fixture) : base(fixture) { }

    // ── Same person ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SamePerson_Opens_WhenOneModeratorKeepsActingOnOnePersonAcrossPlaces_AndNobodyElseHas()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-5), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "2"),
            Fact(FactType.GroupInstanceWarn, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "3"));

        var result = await RunAsync();

        Assert.Equal(1, result.ReviewsOpened);

        var review = Assert.Single(await ReviewsAsync("alice"));
        Assert.Equal(ReviewSignal.SamePerson, review.Signal);
        Assert.Equal("usr_p", review.About);
        Assert.Equal(ReviewState.Open, review.State);
        Assert.Equal(Start.AddDays(-5), review.WindowStart);
        Assert.Equal(Start.AddDays(-1), review.WindowEnd);
        Assert.Contains("3 times", review.Summary);
        Assert.Contains("No other moderator", review.Summary);

        var evidence = Evidence(review);
        Assert.Equal(3, evidence.GetProperty("actions").GetInt32());
        Assert.Equal(3, evidence.GetProperty("places").GetInt32());
        Assert.Equal(0, evidence.GetProperty("otherModerators").GetInt32());
        Assert.Equal(3, evidence.GetProperty("factIds").GetArrayLength());
        Assert.Equal(2, evidence.GetProperty("byKind").GetProperty("instanceKicks").GetInt32());

        // Opening is a fact about the moderator (spec 5.8.5: resolutions are history, and so are openings).
        var fact = Assert.Single(await FactsAsync(FactType.ReviewOpened));
        Assert.Equal("alice", fact.SubjectId);
        Assert.Equal(FactSource.Modbot, fact.Source);
        Assert.Contains(review.Id.ToString(), fact.Data);
    }

    [Fact]
    public async Task SamePerson_DoesNotOpen_WhenEverythingHappenedInOneInstance()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddHours(-5), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddHours(-4), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddHours(-3), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceWarn, Start.AddHours(-2), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"));

        var result = await RunAsync();

        Assert.Equal(0, result.ReviewsOpened);
        Assert.Empty(await ReviewsAsync());
    }

    /// <summary>Kicks with no instance recorded fall back to the day, so two days still count as two places.</summary>
    [Fact]
    public async Task SamePerson_UsesTheDay_WhenNoInstanceIsRecorded()
    {
        await WriteAsync(
            Fact(FactType.MemberKicked, Start.AddDays(-6), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.MemberKicked, Start.AddDays(-4), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.MemberKicked, Start.AddDays(-2), subjectId: "usr_p", actorId: "alice"));

        var result = await RunAsync();

        Assert.Equal(1, result.ReviewsOpened);
    }

    [Fact]
    public async Task SamePerson_NeedsAMuchHigherBar_WhenOtherModeratorsHaveActedToo()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-5), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "2"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "3"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-4), subjectId: "usr_troll", actorId: "bob", worldId: World, instanceId: "4"));

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);

        // Eight of hers, one of bob's: past the higher bar, and the review says he acted too.
        Clock.Advance(TimeSpan.FromMinutes(1));
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddHours(-20), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "5"),
            Fact(FactType.GroupInstanceKick, Start.AddHours(-16), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "6"),
            Fact(FactType.GroupInstanceKick, Start.AddHours(-12), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "7"),
            Fact(FactType.GroupInstanceKick, Start.AddHours(-8), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "8"),
            Fact(FactType.GroupInstanceKick, Start.AddHours(-4), subjectId: "usr_troll", actorId: "alice", worldId: World, instanceId: "9"));

        Assert.Equal(1, (await RunAsync()).ReviewsOpened);

        var review = Assert.Single(await ReviewsAsync("alice"));
        Assert.Contains("One other moderator has acted", review.Summary);
        Assert.Equal(1, Evidence(review).GetProperty("otherModerators").GetInt32());
        Assert.Equal(8, Evidence(review).GetProperty("threshold").GetProperty("actions").GetInt32());

        // Bob's single kick is not a pattern.
        Assert.Empty(await ReviewsAsync("bob"));
    }

    [Fact]
    public async Task SamePerson_IgnoresActionsOlderThanTheWindow()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-40), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-35), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "2"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "3"));

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
    }

    [Fact]
    public async Task SamePerson_ARerunRefreshesTheOpenReview_AndDoesNotOpenASecond()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-5), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "2"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "3"));

        Assert.Equal(1, (await RunAsync()).ReviewsOpened);

        // Nothing new: nothing happens, in either mode.
        var again = await RunAsync();
        Assert.Equal(0, again.ReviewsOpened);
        Assert.Equal(0, again.ReviewsRefreshed);

        var rebuilt = await RunAsync(rebuild: true);
        Assert.Equal(0, rebuilt.ReviewsOpened);
        Assert.Single(await ReviewsAsync("alice"));

        // A fourth action: the same review, with the new number.
        Clock.Advance(TimeSpan.FromMinutes(1));
        await WriteAsync(Fact(FactType.GroupInstanceWarn, Start.AddHours(-1), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "4"));

        var third = await RunAsync();
        Assert.Equal(0, third.ReviewsOpened);
        Assert.Equal(1, third.ReviewsRefreshed);

        var review = Assert.Single(await ReviewsAsync("alice"));
        Assert.Equal(4, Evidence(review).GetProperty("actions").GetInt32());
        Assert.Equal(Start.AddHours(-1), review.WindowEnd);
        Assert.Equal(Clock.UtcNow, review.UpdatedAt);

        Assert.Single(await FactsAsync(FactType.ReviewOpened));
    }

    [Fact]
    public async Task SamePerson_AClosedReview_IsNotReopenedByTheSameFacts_ButANewRunOfThemOpensAnother()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-5), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "2"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "3"));

        await RunAsync();
        var first = Assert.Single(await ReviewsAsync("alice"));
        await CloseAsync(first.Id);

        // The same facts, re-run both ways: still one review, still closed.
        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
        Assert.Equal(0, (await RunAsync(rebuild: true)).ReviewsOpened);
        Assert.Equal(ReviewState.Closed, Assert.Single(await ReviewsAsync("alice")).State);

        // One more action after the close is not a pattern on its own: the count starts again.
        Clock.Advance(TimeSpan.FromHours(1));
        await WriteAsync(Fact(FactType.GroupInstanceKick, Start.AddMinutes(30), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "4"));
        Assert.Equal(0, (await RunAsync()).ReviewsOpened);

        // Three after the close, across places: a new review, about the new facts only.
        Clock.Advance(TimeSpan.FromHours(1));
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddMinutes(60), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "5"),
            Fact(FactType.GroupInstanceKick, Start.AddMinutes(90), subjectId: "usr_p", actorId: "alice", worldId: World, instanceId: "6"));
        Assert.Equal(1, (await RunAsync()).ReviewsOpened);

        var reviews = await ReviewsAsync("alice");
        Assert.Equal(2, reviews.Count);

        var second = reviews.Single(r => r.State == ReviewState.Open);
        Assert.Equal(3, Evidence(second).GetProperty("actions").GetInt32());
        Assert.Equal(Start.AddMinutes(30), second.WindowStart);
    }

    [Fact]
    public async Task SamePerson_DoesNotCountAModeratorActingOnThemselves()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-5), subjectId: "alice", actorId: "alice", worldId: World, instanceId: "1"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "alice", actorId: "alice", worldId: World, instanceId: "2"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "alice", actorId: "alice", worldId: World, instanceId: "3"));

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
    }

    // ── Far above the team ─────────────────────────────────────────────────────────────────

    /// <summary>Ten ordinary days: alice and bob each do two kicks a day. The team's usual is 2.</summary>
    private async Task WriteOrdinaryHistoryAsync()
    {
        var facts = new List<FactRecord>();

        for (var day = 1; day <= 10; day++)
        {
            foreach (var moderator in new[] { "alice", "bob" })
            {
                facts.Add(Fact(FactType.GroupInstanceKick, Start.AddDays(-day).AddHours(1), actorId: moderator, worldId: World, instanceId: $"{day}"));
                facts.Add(Fact(FactType.GroupInstanceKick, Start.AddDays(-day).AddHours(2), actorId: moderator, worldId: World, instanceId: $"{day}"));
            }
        }

        await WriteAsync(facts.ToArray());
    }

    private async Task WriteBusyDayAsync(string moderator, int actions)
    {
        var facts = new List<FactRecord>();
        for (var i = 0; i < actions; i++)
            facts.Add(Fact(FactType.GroupInstanceKick, Start.AddMinutes(-actions + i), actorId: moderator, worldId: World, instanceId: $"busy-{i % 3}"));

        await WriteAsync(facts.ToArray());
    }

    [Fact]
    public async Task FarAboveTeam_Opens_WhenOneDayIsFarAboveTheNextBusiestAndTheUsual()
    {
        await WriteOrdinaryHistoryAsync();
        await WriteBusyDayAsync("alice", 12);
        await WriteBusyDayAsync("bob", 2);

        var result = await RunAsync();

        Assert.Equal(1, result.ReviewsOpened);

        var review = Assert.Single(await ReviewsAsync());
        Assert.Equal("alice", review.ModeratorId);
        Assert.Equal(ReviewSignal.FarAboveTeam, review.Signal);
        Assert.Equal(DayOf(Start).ToString("yyyy-MM-dd"), review.About);
        Assert.StartsWith("12 actions on", review.Summary);
        Assert.Contains("next busiest moderator did 2", review.Summary);

        var evidence = Evidence(review);
        Assert.Equal(12, evidence.GetProperty("actions").GetInt32());
        Assert.Equal("bob", evidence.GetProperty("nextBusiest").GetProperty("moderatorId").GetString());
        Assert.Equal(2, evidence.GetProperty("nextBusiest").GetProperty("actions").GetInt32());
        Assert.Equal(2m, evidence.GetProperty("teamUsualPerDay").GetDecimal());
        Assert.Equal(2m, evidence.GetProperty("ownUsualPerDay").GetDecimal());
        Assert.Equal(10, evidence.GetProperty("teamDays").GetInt32());
        Assert.Equal(8m, evidence.GetProperty("threshold").GetProperty("bar").GetDecimal());

        // The baselines the comparison was made against are on record too.
        await using var context = Database.NewContext();
        var baseline = Assert.Single(context.ModeratorBaselines.Where(b => b.ModeratorId == "alice"));
        Assert.Equal(10, baseline.ActiveDays);
        Assert.Equal(20m, baseline.Actions);
        Assert.Equal(2m, baseline.ActionsPerActiveDay);
    }

    [Fact]
    public async Task FarAboveTeam_DoesNotOpen_OnARaidNight_WhenEverybodyIsBusy()
    {
        await WriteOrdinaryHistoryAsync();
        await WriteBusyDayAsync("alice", 12);
        await WriteBusyDayAsync("bob", 10);

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
        Assert.Empty(await ReviewsAsync());
    }

    [Fact]
    public async Task FarAboveTeam_DoesNotOpen_WithoutATeamBaseline()
    {
        await WriteBusyDayAsync("alice", 12);

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
    }

    [Fact]
    public async Task FarAboveTeam_DoesNotOpen_BelowTheMinimumActions()
    {
        await WriteOrdinaryHistoryAsync();
        await WriteBusyDayAsync("alice", 9);

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
    }

    [Fact]
    public async Task FarAboveTeam_ThresholdsAreSettings()
    {
        await SetThresholdsAsync(ReviewThresholds.Default with { FarAboveTeamMinActions = 20 });
        await WriteOrdinaryHistoryAsync();
        await WriteBusyDayAsync("alice", 12);

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
    }

    [Fact]
    public async Task FarAboveTeam_ARerunRefreshes_AndAClosedDayIsNeverReopened()
    {
        await WriteOrdinaryHistoryAsync();
        await WriteBusyDayAsync("alice", 12);

        Assert.Equal(1, (await RunAsync()).ReviewsOpened);

        // More on the same day: the open review's number moves, no second review.
        Clock.Advance(TimeSpan.FromMinutes(5));
        await WriteAsync(Fact(FactType.GroupInstanceKick, Start.AddMinutes(1), actorId: "alice", worldId: World, instanceId: "x"));

        var refreshed = await RunAsync();
        Assert.Equal(0, refreshed.ReviewsOpened);
        Assert.Equal(1, refreshed.ReviewsRefreshed);

        var review = Assert.Single(await ReviewsAsync());
        Assert.Equal(13, Evidence(review).GetProperty("actions").GetInt32());

        await CloseAsync(review.Id, "Crasher wave, handled alone.");

        // Even more that day, and a rebuild: the day was reviewed; it stays reviewed.
        Clock.Advance(TimeSpan.FromMinutes(5));
        await WriteAsync(Fact(FactType.GroupInstanceKick, Start.AddMinutes(2), actorId: "alice", worldId: World, instanceId: "y"));

        Assert.Equal(0, (await RunAsync()).ReviewsOpened);
        Assert.Equal(0, (await RunAsync(rebuild: true)).ReviewsOpened);
        Assert.Single(await ReviewsAsync());
    }

    [Fact]
    public async Task ThresholdsJson_IsSparse_AndClamped()
    {
        var read = ReviewThresholds.Read("""{"samePersonActions": 1, "farAboveTeamMultiplier": 0.5}""");

        // Missing fields take the defaults; present ones are kept inside sane bounds.
        Assert.Equal(ReviewThresholds.Default.FarAboveTeamMinActions, read.FarAboveTeamMinActions);
        Assert.Equal(2, read.SamePersonActions);
        Assert.Equal(1.5m, read.FarAboveTeamMultiplier);

        Assert.Same(ReviewThresholds.Default, ReviewThresholds.Read(null));
        Assert.Same(ReviewThresholds.Default, ReviewThresholds.Read("not json"));

        var roundTrip = ReviewThresholds.Read(ReviewThresholds.Default.ToJson());
        Assert.Equal(ReviewThresholds.Default, roundTrip);

        Assert.True(JsonDocument.Parse(ReviewThresholds.Default.ToJson()).RootElement.TryGetProperty("samePersonDays", out _));
    }
}
