using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.AutomaticRoles;

/// <summary>The line-by-line view of a rule: every switched-on condition, met or not, with its numbers.</summary>
public class AutomaticRoleExplainTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ModeratorFacts Facts(
        bool companionRunning = true,
        bool clipsReady = true,
        bool inGroupInstance = true,
        bool passedTrial = false,
        string[]? discordRoles = null,
        DateTimeOffset[]? bans = null,
        DateTimeOffset[]? overturned = null,
        DateTimeOffset[]? missed = null)
        => new(
            Guid.NewGuid(),
            Noon,
            true,
            companionRunning,
            clipsReady,
            inGroupInstance,
            passedTrial,
            discordRoles ?? [],
            [],
            bans ?? [],
            overturned ?? [],
            missed ?? []);

    [Fact]
    public void ARuleWithNothingSwitchedOnListsNothing()
    {
        Assert.Empty(AutomaticRoleRule.Explain(new AutomaticRole(), Facts()));
    }

    [Fact]
    public void OnlySwitchedOnConditionsAreListedInTheOrderTheyAreChecked()
    {
        var rule = new AutomaticRole
        {
            MissedClips = true,
            MissedClipsMax = 1,
            MissedClipsDays = 7,
            CompanionRunning = true,
            InGroupInstance = true,
        };

        var labels = AutomaticRoleRule.Explain(rule, Facts()).Select(c => c.Label).ToList();

        Assert.Equal(["Companion running", "In a group instance", "Missed clips"], labels);
    }

    [Fact]
    public void ARuleFailingTwoConditionsShowsBothAsNotMet()
    {
        var rule = new AutomaticRole { CompanionRunning = true, ClipsReady = true, InGroupInstance = true };

        var conditions = AutomaticRoleRule.Explain(rule, Facts(companionRunning: false, clipsReady: false));

        Assert.False(conditions[0].Met);
        Assert.Equal("Companion not running", conditions[0].Reason);
        Assert.False(conditions[1].Met);
        Assert.Equal("Clips turned off", conditions[1].Reason);
        Assert.True(conditions[2].Met);
        Assert.Null(conditions[2].Reason);
    }

    [Fact]
    public void AtMostZeroFailsOnTheFirstOverturnedBan()
    {
        var rule = new AutomaticRole { OverturnedBans = true, OverturnedBansMax = 0, OverturnedBansDays = 30 };

        var met = Assert.Single(AutomaticRoleRule.Explain(rule, Facts()));
        Assert.True(met.Met);
        Assert.Equal(0, met.Count);

        var failed = Assert.Single(AutomaticRoleRule.Explain(rule, Facts(overturned: [Noon.AddDays(-1)])));
        Assert.False(failed.Met);
        Assert.Equal("Too many overturned bans", failed.Reason);
        Assert.Equal(1, failed.Count);
        Assert.Equal(0, failed.Max);
        Assert.Equal(30, failed.Days);
    }

    [Fact]
    public void CountsOnlyCoverTheLookBack()
    {
        var rule = new AutomaticRole { MissedClips = true, MissedClipsMax = 2, MissedClipsDays = 7 };

        var condition = Assert.Single(AutomaticRoleRule.Explain(
            rule, Facts(missed: [Noon.AddDays(-1), Noon.AddDays(-6), Noon.AddDays(-8)])));

        Assert.True(condition.Met);
        Assert.Equal(2, condition.Count);
        Assert.Equal(2, condition.Max);
        Assert.Equal(7, condition.Days);
    }

    [Fact]
    public void TheDailyBanCapCountsTwentyFourHoursAndHasNoDays()
    {
        var rule = new AutomaticRole { DailyBanCap = true, DailyBanCapMax = 1 };

        var condition = Assert.Single(AutomaticRoleRule.Explain(
            rule, Facts(bans: [Noon.AddHours(-1), Noon.AddHours(-2), Noon.AddHours(-30)])));

        Assert.False(condition.Met);
        Assert.Equal("Daily ban cap reached", condition.Reason);
        Assert.Equal(2, condition.Count);
        Assert.Equal(1, condition.Max);
        Assert.Null(condition.Days);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(1, null)]
    [InlineData(null, null)]
    public void AMissingNumberIsNotMetAndHasNoCount(int? max, int? days)
    {
        var rule = new AutomaticRole { MissedClips = true, MissedClipsMax = max, MissedClipsDays = days };

        var condition = Assert.Single(AutomaticRoleRule.Explain(rule, Facts()));

        Assert.False(condition.Met);
        Assert.Equal("A number on the rule is missing", condition.Reason);
        Assert.Null(condition.Count);
    }

    [Fact]
    public void MinimumLevelIsMetByTheTrialOrTheStaffRole()
    {
        var rule = new AutomaticRole
        {
            MinimumLevel = true,
            MinimumLevelPassedTrial = true,
            MinimumLevelStaffRoleId = "staff",
        };

        Assert.False(Assert.Single(AutomaticRoleRule.Explain(rule, Facts())).Met);
        Assert.True(Assert.Single(AutomaticRoleRule.Explain(rule, Facts(passedTrial: true))).Met);
        Assert.True(Assert.Single(AutomaticRoleRule.Explain(rule, Facts(discordRoles: ["staff"]))).Met);
    }

    [Fact]
    public void CheckAnswersWithTheFirstConditionExplainDoesNotMeet()
    {
        var rule = new AutomaticRole
        {
            CompanionRunning = true,
            ClipsReady = true,
            DailyBanCap = true,
            DailyBanCapMax = 0,
        };

        var facts = Facts(clipsReady: false, bans: [Noon.AddHours(-1)]);

        var check = AutomaticRoleRule.Check(rule, facts);

        Assert.False(check.Holds);
        Assert.Equal(AutomaticRoleRule.Explain(rule, facts).First(c => !c.Met).Reason, check.Reason);
        Assert.Equal("Clips turned off", check.Reason);
    }
}
