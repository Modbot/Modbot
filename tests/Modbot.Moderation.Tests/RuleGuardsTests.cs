using Modbot.Moderation;
using Modbot.Core.Data.Entities;

namespace Modbot.Moderation.Tests;

/// <summary>
/// The scope, trial and pause rules on their own (AI moderation design §13), with no database in
/// the way: these are the decisions the engine makes about every single match.
/// </summary>
public class RuleGuardsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ChannelScope.All, "c1", true)]
    [InlineData(ChannelScope.All, "c9", true)]
    [InlineData(ChannelScope.Only, "c1", true)]
    [InlineData(ChannelScope.Only, "c9", false)]
    [InlineData(ChannelScope.Except, "c1", false)]
    [InlineData(ChannelScope.Except, "c9", true)]
    public void AChannelLimitDecidesWhetherTheRuleRunsAtAll(string mode, string channelId, bool runs)
    {
        var rule = new ModerationTopic { ChannelMode = mode, Channels = """["c1","c2"]""" };

        Assert.Equal(runs, RuleGuards.RunsIn(rule, channelId));
    }

    [Fact]
    public void ProfileTextHasNoChannel_SoEveryRuleRunsOnIt()
    {
        var rule = new ModerationTopic { ChannelMode = ChannelScope.Only, Channels = """["c1"]""" };

        Assert.True(RuleGuards.RunsIn(rule, null));
    }

    [Fact]
    public void AnExemptRoleIsFoundByIdAndNamed()
    {
        var rule = new ModerationTopic { ExemptRoles = """["r-staff","r-mod"]""" };

        Assert.Equal("r-mod", RuleGuards.ExemptBy(rule, ["r-member", "r-mod"]));
        Assert.Null(RuleGuards.ExemptBy(rule, ["r-member"]));
        Assert.Null(RuleGuards.ExemptBy(rule, []));
        Assert.Null(RuleGuards.ExemptBy(rule, null));
    }

    [Fact]
    public void StoredIdsThatAreNotAJsonArrayReadAsNone()
    {
        var rule = new ModerationTopic { ExemptRoles = "not json" };

        Assert.Empty(RuleGuards.Ids(rule.ExemptRoles));
        Assert.Null(RuleGuards.ExemptBy(rule, ["r-mod"]));
    }

    [Fact]
    public void ARuleThatOnlyFlagsIsNeverInATrialAndNeverActs()
    {
        var rule = new ModerationTopic { TrialStartedAt = Now };

        Assert.False(RuleGuards.WantsAction(rule));
        Assert.False(RuleGuards.InTrial(rule));
        Assert.False(RuleGuards.ActsNow(rule));
    }

    [Fact]
    public void ARuleActsOnlyOnceItsTrialIsEndedAndWhileItIsNotPaused()
    {
        var rule = new ModerationTopic { DeleteMessage = true, TrialStartedAt = Now, TrialDays = 7 };

        Assert.True(RuleGuards.InTrial(rule));
        Assert.False(RuleGuards.ActsNow(rule));
        Assert.Equal(Now.AddDays(7), RuleGuards.TrialEndsAt(rule));

        rule.TrialEndedAt = Now.AddDays(3);
        Assert.False(RuleGuards.InTrial(rule));
        Assert.True(RuleGuards.ActsNow(rule));
        Assert.Null(RuleGuards.TrialEndsAt(rule));

        rule.PausedAt = Now.AddDays(4);
        Assert.False(RuleGuards.ActsNow(rule));
    }

    [Fact]
    public void ATimeoutOfZeroIsNotAnAction()
    {
        Assert.False(RuleGuards.WantsAction(new ModerationTopic { TimeoutMinutes = 0 }));
        Assert.True(RuleGuards.WantsAction(new ModerationTopic { TimeoutMinutes = 1 }));
    }

    [Theory]
    // From a standing start the first condition is the one that decides.
    [InlineData(10, 0, false)]
    [InlineData(11, 0, true)]
    [InlineData(50, 0, true)]
    // A rule that normally acts twice an hour: 336 actions over seven days.
    [InlineData(11, 336, false)]
    [InlineData(32, 336, false)]
    [InlineData(33, 336, true)]
    // A busy rule is not stopped for being busy at its usual rate.
    [InlineData(100, 16_800, false)]
    public void ARuleStopsItselfOnlyWhenBothNumbersSaySo(int hour, int week, bool pauses)
        => Assert.Equal(pauses, RunawayGuard.ShouldPause(hour, week));

    [Fact]
    public void ThePauseReasonNamesBothNumbers()
    {
        var reason = RunawayGuard.Reason(24, 336);

        Assert.Contains("24 times in an hour", reason, StringComparison.Ordinal);
        Assert.Contains("2 an hour over the last 7 days", reason, StringComparison.Ordinal);
    }
}
