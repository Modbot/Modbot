using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.AutomaticRoles;

/// <summary>Whether a moderator meets an automatic role rule: each condition holds and fails as planned.</summary>
public class AutomaticRoleRuleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ModeratorFacts Facts(
        bool isModerator = true,
        bool companionRunning = true,
        bool clipsReady = true,
        bool inGroupInstance = true,
        bool passedTrial = false,
        string[]? discordRoles = null,
        string[]? vrchatRoles = null,
        DateTimeOffset[]? bans = null,
        DateTimeOffset[]? overturned = null,
        DateTimeOffset[]? missed = null)
        => new(
            Guid.NewGuid(),
            Noon,
            isModerator,
            companionRunning,
            clipsReady,
            inGroupInstance,
            passedTrial,
            discordRoles ?? [],
            vrchatRoles ?? [],
            bans ?? [],
            overturned ?? [],
            missed ?? []);

    private static RoleCheck Check(AutomaticRole rule, ModeratorFacts facts) => AutomaticRoleRule.Check(rule, facts);

    [Fact]
    public void ARuleWithNothingSwitchedOnHolds()
    {
        Assert.True(Check(new AutomaticRole(), Facts(companionRunning: false, clipsReady: false, inGroupInstance: false)).Holds);
    }

    [Fact]
    public void CompanionRunningHoldsAndFails()
    {
        var rule = new AutomaticRole { CompanionRunning = true };

        Assert.True(Check(rule, Facts(companionRunning: true)).Holds);

        var failed = Check(rule, Facts(companionRunning: false));
        Assert.False(failed.Holds);
        Assert.Equal("Companion not running", failed.Reason);
    }

    [Fact]
    public void ClipsReadyHoldsAndFails()
    {
        var rule = new AutomaticRole { ClipsReady = true };

        Assert.True(Check(rule, Facts(clipsReady: true)).Holds);

        var failed = Check(rule, Facts(clipsReady: false));
        Assert.False(failed.Holds);
        Assert.Equal("Clips turned off", failed.Reason);
    }

    [Fact]
    public void InGroupInstanceHoldsAndFails()
    {
        var rule = new AutomaticRole { InGroupInstance = true };

        Assert.True(Check(rule, Facts(inGroupInstance: true)).Holds);

        var failed = Check(rule, Facts(inGroupInstance: false));
        Assert.False(failed.Holds);
        Assert.Equal("Not in a group instance", failed.Reason);
    }

    [Fact]
    public void AConditionSwitchedOffIsNotChecked()
    {
        var rule = new AutomaticRole { CompanionRunning = false, ClipsReady = false, InGroupInstance = false };

        Assert.True(Check(rule, Facts(companionRunning: false, clipsReady: false, inGroupInstance: false)).Holds);
    }

    [Fact]
    public void MinimumLevelIsMetByThePassedTrialAlone()
    {
        var rule = new AutomaticRole { MinimumLevel = true, MinimumLevelPassedTrial = true };

        Assert.True(Check(rule, Facts(passedTrial: true)).Holds);
        Assert.Equal("Minimum level not met", Check(rule, Facts(passedTrial: false)).Reason);
    }

    [Fact]
    public void MinimumLevelIsMetByTheStaffRoleAlone()
    {
        var rule = new AutomaticRole { MinimumLevel = true, MinimumLevelStaffRoleId = "role_staff" };

        Assert.True(Check(rule, Facts(discordRoles: ["role_staff"])).Holds);
        Assert.Equal("Minimum level not met", Check(rule, Facts(discordRoles: ["role_other"])).Reason);
    }

    [Fact]
    public void MinimumLevelWithBothIsMetByEitherOne()
    {
        var rule = new AutomaticRole
        {
            MinimumLevel = true,
            MinimumLevelPassedTrial = true,
            MinimumLevelStaffRoleId = "role_staff",
        };

        Assert.True(Check(rule, Facts(passedTrial: true)).Holds);
        Assert.True(Check(rule, Facts(discordRoles: ["role_staff"])).Holds);
        Assert.False(Check(rule, Facts()).Holds);
    }

    [Fact]
    public void MinimumLevelNamingNeitherNeverHolds()
    {
        var rule = new AutomaticRole { MinimumLevel = true };

        Assert.Equal("Minimum level not met", Check(rule, Facts(passedTrial: true, discordRoles: ["role_staff"])).Reason);
    }

    [Fact]
    public void ATrialRuleIsNotMetByTheStaffRoleWhenThePassedTrialBoxIsOff()
    {
        var rule = new AutomaticRole { MinimumLevel = true, MinimumLevelStaffRoleId = "role_staff" };

        Assert.False(Check(rule, Facts(passedTrial: true)).Holds);
    }

    [Fact]
    public void OverturnedBansAllowsUpToTheMaximumInTheLookBack()
    {
        var rule = new AutomaticRole { OverturnedBans = true, OverturnedBansMax = 1, OverturnedBansDays = 30 };

        Assert.True(Check(rule, Facts(overturned: [Noon.AddDays(-1)])).Holds);

        var failed = Check(rule, Facts(overturned: [Noon.AddDays(-1), Noon.AddDays(-2)]));
        Assert.False(failed.Holds);
        Assert.Equal("Too many overturned bans", failed.Reason);
    }

    [Fact]
    public void OverturnedBansOutsideTheLookBackDoNotCount()
    {
        var rule = new AutomaticRole { OverturnedBans = true, OverturnedBansMax = 0, OverturnedBansDays = 7 };

        Assert.True(Check(rule, Facts(overturned: [Noon.AddDays(-8)])).Holds);
        Assert.False(Check(rule, Facts(overturned: [Noon.AddDays(-6)])).Holds);
    }

    [Fact]
    public void DailyBanCapAllowsUpToTheMaximumInTwentyFourHours()
    {
        var rule = new AutomaticRole { DailyBanCap = true, DailyBanCapMax = 2 };

        Assert.True(Check(rule, Facts(bans: [Noon.AddHours(-1), Noon.AddHours(-2)])).Holds);

        var failed = Check(rule, Facts(bans: [Noon.AddHours(-1), Noon.AddHours(-2), Noon.AddHours(-3)]));
        Assert.False(failed.Holds);
        Assert.Equal("Daily ban cap reached", failed.Reason);
    }

    [Fact]
    public void ABanOlderThanTwentyFourHoursIsNotInTheCap()
    {
        var rule = new AutomaticRole { DailyBanCap = true, DailyBanCapMax = 1 };

        Assert.True(Check(rule, Facts(bans: [Noon.AddHours(-1), Noon.AddHours(-25)])).Holds);
    }

    [Fact]
    public void ABanExactlyTwentyFourHoursAgoStillCounts()
    {
        var rule = new AutomaticRole { DailyBanCap = true, DailyBanCapMax = 0 };

        Assert.False(Check(rule, Facts(bans: [Noon.AddHours(-24)])).Holds);
    }

    [Fact]
    public void ZeroMeansTheFirstOneTakesTheRole()
    {
        var bans = new AutomaticRole { DailyBanCap = true, DailyBanCapMax = 0 };
        var missed = new AutomaticRole { MissedClips = true, MissedClipsMax = 0, MissedClipsDays = 7 };
        var overturned = new AutomaticRole { OverturnedBans = true, OverturnedBansMax = 0, OverturnedBansDays = 7 };

        Assert.True(Check(bans, Facts()).Holds);
        Assert.False(Check(bans, Facts(bans: [Noon.AddHours(-1)])).Holds);
        Assert.True(Check(missed, Facts()).Holds);
        Assert.False(Check(missed, Facts(missed: [Noon.AddDays(-1)])).Holds);
        Assert.True(Check(overturned, Facts()).Holds);
        Assert.False(Check(overturned, Facts(overturned: [Noon.AddDays(-1)])).Holds);
    }

    [Fact]
    public void MissedClipsAllowsUpToTheMaximumInTheLookBack()
    {
        var rule = new AutomaticRole { MissedClips = true, MissedClipsMax = 2, MissedClipsDays = 7 };

        Assert.True(Check(rule, Facts(missed: [Noon.AddDays(-1), Noon.AddDays(-2)])).Holds);

        var failed = Check(rule, Facts(missed: [Noon.AddDays(-1), Noon.AddDays(-2), Noon.AddDays(-3)]));
        Assert.False(failed.Holds);
        Assert.Equal("Too many missed clips", failed.Reason);
    }

    [Fact]
    public void MissedClipsOutsideTheLookBackDoNotCount()
    {
        var rule = new AutomaticRole { MissedClips = true, MissedClipsMax = 0, MissedClipsDays = 7 };

        Assert.True(Check(rule, Facts(missed: [Noon.AddDays(-7).AddMinutes(-1)])).Holds);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(1, null)]
    [InlineData(null, null)]
    public void AMissingNumberOnOverturnedBansNeverHolds(int? max, int? days)
    {
        var rule = new AutomaticRole { OverturnedBans = true, OverturnedBansMax = max, OverturnedBansDays = days };

        var failed = Check(rule, Facts());
        Assert.False(failed.Holds);
        Assert.Equal("A number on the rule is missing", failed.Reason);
    }

    [Fact]
    public void AMissingNumberOnTheDailyCapNeverHolds()
    {
        var failed = Check(new AutomaticRole { DailyBanCap = true }, Facts());

        Assert.False(failed.Holds);
        Assert.Equal("A number on the rule is missing", failed.Reason);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(1, null)]
    public void AMissingNumberOnMissedClipsNeverHolds(int? max, int? days)
    {
        var failed = Check(new AutomaticRole { MissedClips = true, MissedClipsMax = max, MissedClipsDays = days }, Facts());

        Assert.False(failed.Holds);
        Assert.Equal("A number on the rule is missing", failed.Reason);
    }

    [Fact]
    public void TheFirstConditionThatFailsIsTheReason()
    {
        var rule = new AutomaticRole
        {
            CompanionRunning = true,
            ClipsReady = true,
            InGroupInstance = true,
            MinimumLevel = true,
            MinimumLevelPassedTrial = true,
            DailyBanCap = true,
            DailyBanCapMax = 0,
            MissedClips = true,
            MissedClipsMax = 0,
            MissedClipsDays = 7,
        };

        var everythingWrong = Facts(
            companionRunning: false, clipsReady: false, inGroupInstance: false,
            bans: [Noon.AddHours(-1)], missed: [Noon.AddDays(-1)]);

        Assert.Equal("Companion not running", Check(rule, everythingWrong).Reason);
        Assert.Equal("Clips turned off", Check(rule, everythingWrong with { CompanionRunning = true }).Reason);
        Assert.Equal("Not in a group instance", Check(rule, everythingWrong with { CompanionRunning = true, ClipsReady = true }).Reason);
        Assert.Equal(
            "Minimum level not met",
            Check(rule, everythingWrong with { CompanionRunning = true, ClipsReady = true, InGroupInstance = true }).Reason);
        Assert.Equal(
            "Daily ban cap reached",
            Check(rule, everythingWrong with { CompanionRunning = true, ClipsReady = true, InGroupInstance = true, PassedTrial = true }).Reason);
    }

    [Fact]
    public void EveryConditionSwitchedOnAndMetHolds()
    {
        var rule = new AutomaticRole
        {
            CompanionRunning = true,
            ClipsReady = true,
            InGroupInstance = true,
            MinimumLevel = true,
            MinimumLevelPassedTrial = true,
            OverturnedBans = true,
            OverturnedBansMax = 0,
            OverturnedBansDays = 30,
            DailyBanCap = true,
            DailyBanCapMax = 5,
            MissedClips = true,
            MissedClipsMax = 1,
            MissedClipsDays = 7,
        };

        var result = Check(rule, Facts(passedTrial: true, bans: [Noon.AddHours(-3)]));

        Assert.True(result.Holds);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void AWholeTeamRuleAppliesToModeratorsOnly()
    {
        var rule = new AutomaticRole { WholeTeam = true };

        Assert.True(AutomaticRoleRule.AppliesTo(rule, Facts(isModerator: true)));
        Assert.False(AutomaticRoleRule.AppliesTo(rule, Facts(isModerator: false)));
    }

    [Fact]
    public void ARuleForADiscordRoleAppliesToItsMembersWhetherOrNotTheyAreModerators()
    {
        var rule = new AutomaticRole { WholeTeam = false, WhoDiscordRoleIds = ["role_a", "role_b"] };

        Assert.True(AutomaticRoleRule.AppliesTo(rule, Facts(isModerator: false, discordRoles: ["role_b"])));
        Assert.False(AutomaticRoleRule.AppliesTo(rule, Facts(isModerator: true, discordRoles: ["role_c"])));
    }

    [Fact]
    public void ARuleForAVRChatRoleAppliesToItsMembers()
    {
        var rule = new AutomaticRole { WholeTeam = false, WhoVRChatRoleIds = ["grol_a"] };

        Assert.True(AutomaticRoleRule.AppliesTo(rule, Facts(vrchatRoles: ["grol_a"])));
        Assert.False(AutomaticRoleRule.AppliesTo(rule, Facts(vrchatRoles: ["grol_b"])));
    }

    [Fact]
    public void ARuleForChosenRolesAppliesToNoOneWhenNoRoleIsChosen()
    {
        var rule = new AutomaticRole { WholeTeam = false };

        Assert.False(AutomaticRoleRule.AppliesTo(rule, Facts(discordRoles: ["role_a"], vrchatRoles: ["grol_a"])));
    }

    [Fact]
    public void ADiscordRoleIsNotMatchedAgainstVRChatRoles()
    {
        var rule = new AutomaticRole { WholeTeam = false, WhoDiscordRoleIds = ["same"] };

        Assert.False(AutomaticRoleRule.AppliesTo(rule, Facts(vrchatRoles: ["same"])));
    }
}
