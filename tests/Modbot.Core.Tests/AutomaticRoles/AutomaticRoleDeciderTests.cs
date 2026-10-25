using Modbot.Core.AutomaticRoles;

namespace Modbot.Core.Tests.AutomaticRoles;

/// <summary>Whether Modbot gives a role, takes it or leaves it alone.</summary>
public class AutomaticRoleDeciderTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Started.AddHours(1);

    private static readonly RoleCheck Holds = RoleCheck.Met;
    private static readonly RoleCheck ClipsOff = RoleCheck.Failed(AutomaticRoleReasons.ClipsTurnedOff);
    private static readonly RoleCheck CapReached = RoleCheck.Failed(AutomaticRoleReasons.DailyBanCapReached);

    private static RoleDecision Decide(
        RoleCheck[] checks,
        bool holdsRole = false,
        bool modbotGaveIt = false,
        bool onTakeBackList = false,
        DateTimeOffset? lastTakenAt = null,
        DateTimeOffset? now = null,
        DateTimeOffset? startedAt = null)
        => AutomaticRoleDecider.Decide(new RoleDecisionInput(
            checks, holdsRole, modbotGaveIt, onTakeBackList, lastTakenAt, now ?? Later, startedAt ?? Started));

    [Fact]
    public void ARuleThatHoldsGivesARoleThePersonLacks()
    {
        Assert.Equal(RoleAction.Give, Decide([Holds]).Action);
    }

    [Fact]
    public void ARuleThatHoldsLeavesARoleThePersonHas()
    {
        Assert.Equal(RoleAction.Nothing, Decide([Holds], holdsRole: true, modbotGaveIt: true).Action);
    }

    [Fact]
    public void ARoleIsNotGivenBackUnderAMinuteAfterItWasTaken()
    {
        var decision = Decide([Holds], lastTakenAt: Later.AddSeconds(-59));

        Assert.Equal(RoleAction.Nothing, decision.Action);
    }

    [Fact]
    public void ARoleIsGivenBackAtAMinuteAfterItWasTaken()
    {
        var decision = Decide([Holds], lastTakenAt: Later.AddMinutes(-1));

        Assert.Equal(RoleAction.Give, decision.Action);
    }

    [Fact]
    public void ARoleModbotNeverTookIsGivenAtOnce()
    {
        Assert.Equal(RoleAction.Give, Decide([Holds], lastTakenAt: null).Action);
    }

    [Fact]
    public void ARoleModbotGaveIsTakenStraightAwayWithTheFailedReason()
    {
        var decision = Decide([ClipsOff], holdsRole: true, modbotGaveIt: true);

        Assert.Equal(RoleAction.Take, decision.Action);
        Assert.Equal("Clips turned off", decision.Reason);
    }

    [Fact]
    public void TheFirstFailingReasonIsTheOneGiven()
    {
        var decision = Decide([ClipsOff, CapReached], holdsRole: true, modbotGaveIt: true);

        Assert.Equal("Clips turned off", decision.Reason);
    }

    [Fact]
    public void NothingIsTakenInTheFirstTwoMinutesAfterModbotStarts()
    {
        var decision = Decide([ClipsOff], holdsRole: true, modbotGaveIt: true, now: Started.AddMinutes(2).AddSeconds(-1));

        Assert.Equal(RoleAction.Nothing, decision.Action);
    }

    [Fact]
    public void ARoleIsTakenOnceModbotHasBeenUpTwoMinutes()
    {
        var decision = Decide([ClipsOff], holdsRole: true, modbotGaveIt: true, now: Started.AddMinutes(2));

        Assert.Equal(RoleAction.Take, decision.Action);
    }

    [Fact]
    public void ARoleGivenByHandIsLeftAlone()
    {
        var decision = Decide([ClipsOff], holdsRole: true, modbotGaveIt: false, onTakeBackList: false);

        Assert.Equal(RoleAction.Nothing, decision.Action);
    }

    [Fact]
    public void ARoleGivenByHandIsTakenWhenThePersonIsOnTheTakeBackList()
    {
        var decision = Decide([ClipsOff], holdsRole: true, modbotGaveIt: false, onTakeBackList: true);

        Assert.Equal(RoleAction.Take, decision.Action);
        Assert.Equal("Clips turned off", decision.Reason);
    }

    [Fact]
    public void ARoleThePersonDoesNotHoldIsNotTaken()
    {
        Assert.Equal(RoleAction.Nothing, Decide([ClipsOff], holdsRole: false, modbotGaveIt: true).Action);
    }

    [Fact]
    public void TwoRulesGiveTheSameRoleAndOneHoldsSoThePersonKeepsIt()
    {
        var decision = Decide([ClipsOff, Holds], holdsRole: true, modbotGaveIt: true);

        Assert.Equal(RoleAction.Nothing, decision.Action);
    }

    [Fact]
    public void TwoRulesGiveTheSameRoleAndNeitherHoldsSoItIsTaken()
    {
        var decision = Decide([ClipsOff, CapReached], holdsRole: true, modbotGaveIt: true);

        Assert.Equal(RoleAction.Take, decision.Action);
    }

    [Fact]
    public void ARoleIsGivenWhenAnyOfTwoRulesHolds()
    {
        Assert.Equal(RoleAction.Give, Decide([ClipsOff, Holds]).Action);
    }

    [Fact]
    public void ARoleModbotGaveIsTakenWhenNoRuleIsForThePersonAnyMore()
    {
        var decision = Decide([], holdsRole: true, modbotGaveIt: true);

        Assert.Equal(RoleAction.Take, decision.Action);
        Assert.Equal("No longer on the rule's list", decision.Reason);
    }

    [Fact]
    public void ARoleGivenByHandIsLeftAloneWhenNoRuleIsForThePersonAnyMore()
    {
        var decision = Decide([], holdsRole: true, modbotGaveIt: false, onTakeBackList: true);

        Assert.Equal(RoleAction.Nothing, decision.Action);
    }

    [Fact]
    public void NothingIsGivenWhenNoRuleIsForThePerson()
    {
        Assert.Equal(RoleAction.Nothing, Decide([]).Action);
    }

    [Fact]
    public void TheStartupWaitAlsoHoldsBackATakeForARuleThatNoLongerApplies()
    {
        var decision = Decide([], holdsRole: true, modbotGaveIt: true, now: Started.AddSeconds(30));

        Assert.Equal(RoleAction.Nothing, decision.Action);
    }
}
