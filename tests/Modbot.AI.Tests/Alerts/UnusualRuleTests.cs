using Modbot.AI.Alerts;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Tests.Alerts;

/// <summary>
/// The one rule that decides what is unusual (AI insights design §8.2). Pure: no database, no
/// clock, so every line of it can be pinned down here.
/// </summary>
public class UnusualRuleTests
{
    /// <summary>A quiet hour: four joins most days, never more than six.</summary>
    private static readonly decimal[] Quiet = [4, 3, 5, 4, 4, 6, 3, 4, 5, 4, 4, 3, 5, 4];

    /// <summary>A busy hour that barely moves: three hundred joins, give or take five.</summary>
    private static readonly decimal[] Busy = [300, 298, 302, 300, 301, 299, 300, 303, 297, 300, 300, 301, 299, 300];

    private static UnusualVerdict Check(
        decimal now,
        IReadOnlyList<decimal> earlier,
        string sensitivity = AlertSensitivities.Normal,
        int minimum = 5,
        AlertDirection direction = AlertDirection.Above,
        int leastEarlier = UnusualRule.LeastEarlierWindows)
        => UnusualRule.Check(new UnusualCheck(now, earlier, sensitivity, minimum, direction, leastEarlier));

    [Fact]
    public void ASpikeFarAboveNormalFires()
    {
        var verdict = Check(40, Quiet);

        Assert.True(verdict.Unusual);
        Assert.Equal(4m, verdict.Normal);
        Assert.True(verdict.Score > 4m);
    }

    [Fact]
    public void AnOrdinaryHourDoesNotFire()
    {
        Assert.False(Check(5, Quiet).Unusual);
        Assert.False(Check(7, Quiet).Unusual);
    }

    [Fact]
    public void AQuietGroupDoesNotFireOnTwoJoins()
    {
        // Nothing ever happens in this hour, so two joins clear every test but the minimum count.
        decimal[] never = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.False(Check(2, never, minimum: 5).Unusual);
        Assert.True(Check(5, never, minimum: 5).Unusual);
    }

    [Fact]
    public void ABigSteadyFigureDoesNotFireOnASmallWobble()
    {
        // Six above a spread of one is four spreads, but it is nowhere near half again normal.
        Assert.False(Check(306, Busy).Unusual);
        Assert.True(Check(900, Busy).Unusual);
    }

    [Fact]
    public void SensitivityMovesTheBar()
    {
        // Normal is four and the spread is one, so the bar is ten, eight and six and a half.
        Assert.False(Check(7, Quiet, AlertSensitivities.Normal).Unusual);
        Assert.True(Check(7, Quiet, AlertSensitivities.High).Unusual);

        Assert.False(Check(9, Quiet, AlertSensitivities.Low).Unusual);
        Assert.True(Check(9, Quiet, AlertSensitivities.Normal).Unusual);

        Assert.True(Check(40, Quiet, AlertSensitivities.Low).Unusual);
    }

    [Fact]
    public void OffNeverFires()
    {
        Assert.False(Check(400, Quiet, AlertSensitivities.Off).Unusual);
        Assert.False(Check(400, Quiet, "nonsense").Unusual);
    }

    [Fact]
    public void WithTooLittleHistoryNothingFires()
    {
        decimal[] fourDays = [4, 3, 5, 4];

        Assert.False(Check(40, fourDays).Unusual);
        Assert.True(Check(40, fourDays, leastEarlier: 4).Unusual);
    }

    [Fact]
    public void OnePastSpikeDoesNotHideTheNextOne()
    {
        // The middle ignores the outlier; an average would put normal at nine and the bar out of reach.
        decimal[] withASpike = [4, 3, 5, 4, 80, 4, 3, 4, 5, 4, 4, 3, 5, 4];

        Assert.Equal(4m, UnusualRule.Middle(withASpike));
        Assert.True(Check(40, withASpike).Unusual);
    }

    [Fact]
    public void ADropIsJudgedAgainstWhatNormalWas()
    {
        decimal[] weeks = [500, 520, 480, 510];

        Assert.True(Check(100, weeks, minimum: 20, direction: AlertDirection.Below, leastEarlier: 3).Unusual);
        Assert.False(Check(480, weeks, minimum: 20, direction: AlertDirection.Below, leastEarlier: 3).Unusual);

        // A group with five active members has nothing to drop from.
        decimal[] tiny = [5, 4, 6, 5];
        Assert.False(Check(0, tiny, minimum: 20, direction: AlertDirection.Below, leastEarlier: 3).Unusual);
    }

    [Fact]
    public void ADropFiresSoonerAtAHigherSensitivity()
    {
        decimal[] weeks = [100, 100, 100, 100];

        Assert.False(Check(70, weeks, AlertSensitivities.Low, 20, AlertDirection.Below, 3).Unusual);
        Assert.False(Check(70, weeks, AlertSensitivities.Normal, 20, AlertDirection.Below, 3).Unusual);
        Assert.True(Check(70, weeks, AlertSensitivities.High, 20, AlertDirection.Below, 3).Unusual);
    }

    [Fact]
    public void MuchWorseOverridesTheQuietTime()
    {
        Assert.False(UnusualRule.MuchWorseThan(6m, 5m));
        Assert.True(UnusualRule.MuchWorseThan(10m, 5m));
        Assert.True(UnusualRule.MuchWorseThan(1m, 0m));
    }

    [Fact]
    public void ABusyInstanceIsMeasuredAgainstThisGroupsOwnInstances()
    {
        Assert.Equal(20m, UnusualRule.BusyEnough(10m, AlertSensitivities.Low, 8));
        Assert.Equal(15m, UnusualRule.BusyEnough(10m, AlertSensitivities.Normal, 8));
        Assert.Equal(10m, UnusualRule.BusyEnough(10m, AlertSensitivities.High, 8));

        // Never below the minimum, whatever the group's instances usually hold.
        Assert.Equal(8m, UnusualRule.BusyEnough(2m, AlertSensitivities.High, 8));
        Assert.Equal(decimal.MaxValue, UnusualRule.BusyEnough(10m, AlertSensitivities.Off, 8));
    }

    [Fact]
    public void TheMiddleOfNothingIsZero()
    {
        Assert.Equal(0m, UnusualRule.Middle([]));
        Assert.Equal(3m, UnusualRule.Middle([1, 5]));
        Assert.Equal(5m, UnusualRule.Middle([9, 1, 5]));
    }
}
