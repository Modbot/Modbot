using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Core.Tests.Live;

/// <summary>Which actions ask for a clip, from which computer, and which statuses are misses.</summary>
public class ClipRequestRuleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FactType.ActionBan, ClipAction.Ban)]
    [InlineData(FactType.MemberBanned, ClipAction.Ban)]
    [InlineData(FactType.ActionKick, ClipAction.Kick)]
    [InlineData(FactType.MemberKicked, ClipAction.Kick)]
    [InlineData(FactType.GroupInstanceKick, ClipAction.Kick)]
    [InlineData(FactType.GroupInstanceWarn, ClipAction.Warning)]
    public void EachActionFactIsOneKindOfAction(string type, ClipAction expected)
        => Assert.Equal(expected, ClipRequestRule.ActionOf(type));

    [Fact]
    public void AFactThatIsNotAnActionAsksNothing()
        => Assert.Null(ClipRequestRule.ActionOf(FactType.InstanceJoined));

    [Fact]
    public void EachKindIsSwitchedOnByItsOwnSetting()
    {
        Assert.True(ClipRequestRule.IsSwitchedOn(ClipAction.Ban, true, false, false));
        Assert.False(ClipRequestRule.IsSwitchedOn(ClipAction.Kick, true, false, false));
        Assert.True(ClipRequestRule.IsSwitchedOn(ClipAction.Warning, false, false, true));
    }

    [Fact]
    public void AnActionOlderThanTenMinutesIsNotAskedAbout()
    {
        Assert.True(ClipRequestRule.IsRecentEnough(Noon.AddMinutes(-10), Noon));
        Assert.False(ClipRequestRule.IsRecentEnough(Noon.AddMinutes(-10).AddSeconds(-1), Noon));
    }

    [Fact]
    public void SomebodyWhoJoinedAndDidNotLeaveWasThere()
    {
        var there = ClipRequestRule.WhereWas(
            [new PersonMark(FactType.InstanceJoined, "wrld_a", "111", Noon.AddHours(-1))], Noon);

        Assert.Equal(("wrld_a", "111"), there);
    }

    [Fact]
    public void SomebodyWhoLeftBeforeTheActionWasNotThere()
    {
        var there = ClipRequestRule.WhereWas(
            [
                new PersonMark(FactType.InstanceJoined, "wrld_a", "111", Noon.AddHours(-1)),
                new PersonMark(FactType.InstanceLeft, "wrld_a", "111", Noon.AddMinutes(-5)),
            ],
            Noon);

        Assert.Null(there);
    }

    [Fact]
    public void ThePlaceIsTheNewestOneAtOrBeforeTheAction()
    {
        var there = ClipRequestRule.WhereWas(
            [
                new PersonMark(FactType.InstanceJoined, "wrld_a", "111", Noon.AddHours(-2)),
                new PersonMark(FactType.InstanceJoined, "wrld_b", "222", Noon.AddHours(-1)),
                new PersonMark(FactType.InstanceJoined, "wrld_c", "333", Noon.AddMinutes(1)),
            ],
            Noon);

        Assert.Equal(("wrld_b", "222"), there);
    }

    [Fact]
    public void TheFirstComputerInTheInstanceIsAsked()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Equal(second, ClipRequestRule.DeviceToAsk([first, second], id => id == second));
    }

    [Fact]
    public void NoComputerInTheInstanceAsksNobody()
        => Assert.Null(ClipRequestRule.DeviceToAsk([Guid.NewGuid()], _ => false));

    [Fact]
    public void AnAnswerIsTakenUpToFifteenMinutesAfterTheRequest()
    {
        Assert.True(ClipRequestRule.AnswerStillTaken(Noon, Noon.AddMinutes(15)));
        Assert.False(ClipRequestRule.AnswerStillTaken(Noon, Noon.AddMinutes(15).AddSeconds(1)));
    }

    [Fact]
    public void OnlyTheFourFailuresCountAsMissed()
    {
        var missed = Enum.GetValues<ClipStatus>().Where(ClipStatusLabels.IsMissed).ToHashSet();

        Assert.Equal(
            [ClipStatus.UploadFailed, ClipStatus.CouldNotCut, ClipStatus.DidNotAnswer, ClipStatus.ClipsOff],
            missed);
    }

    [Fact]
    public void EveryStatusHasItsWords()
    {
        Assert.Equal("Clip attached", ClipStatusLabels.Of(ClipStatus.Attached));
        Assert.Equal("Clip failed to upload", ClipStatusLabels.Of(ClipStatus.UploadFailed));
        Assert.Equal("Clip couldn't be cut", ClipStatusLabels.Of(ClipStatus.CouldNotCut));
        Assert.Equal("Companion didn't answer", ClipStatusLabels.Of(ClipStatus.DidNotAnswer));
        Assert.Equal("Clips was off", ClipStatusLabels.Of(ClipStatus.ClipsOff));
        Assert.Equal("Not in the instance", ClipStatusLabels.Of(ClipStatus.NotInInstance));
    }
}
