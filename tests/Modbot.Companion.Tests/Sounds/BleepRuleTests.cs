using Modbot.Companion.Sounds;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// The rule between what the client noticed and what the moderator hears: one event is one sound,
/// a rush is still one sound, and the Test button always sounds.
/// </summary>
public class BleepRuleTests
{
    private readonly FakeClock _clock = new();

    private BleepRule Rule() => new(_clock);

    [Fact]
    public void TheFirstThingSounds()
    {
        Assert.True(Rule().Ask(NotificationKind.FlaggedJoin, "Rin"));
    }

    [Fact]
    public void TheSameThingNoticedTwiceIsOneSound()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.Problem, "modbot.example"));

        // Past the quiet gap, so it is the "already heard" rule refusing it and not the rush rule.
        _clock.Advance(BleepRule.QuietGap + TimeSpan.FromSeconds(1));

        Assert.False(rule.Ask(NotificationKind.Problem, "modbot.example"));
    }

    [Fact]
    public void TheSameThingMuchLaterSoundsAgain()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.Problem, "modbot.example"));
        _clock.Advance(BleepRule.SameThingWindow + TimeSpan.FromSeconds(1));

        Assert.True(rule.Ask(NotificationKind.Problem, "modbot.example"));
    }

    [Fact]
    public void ARushOfArrivalsIsOneSound()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));

        foreach (var name in new[] { "Kai", "Mio", "Sol", "Ash" })
            Assert.False(rule.Ask(NotificationKind.FlaggedJoin, name));
    }

    [Fact]
    public void SomethingNewAfterTheQuietGapSounds()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
        _clock.Advance(BleepRule.QuietGap + TimeSpan.FromSeconds(1));

        Assert.True(rule.Ask(NotificationKind.FlaggedJoin, "Kai"));
    }

    [Fact]
    public void TwoKindsAboutOnePersonAreTwoThings()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
        _clock.Advance(BleepRule.QuietGap + TimeSpan.FromSeconds(1));

        Assert.True(rule.Ask(NotificationKind.Problem, "Rin"));
    }

    [Fact]
    public void TheTestButtonAlwaysSounds()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.Test));
        Assert.True(rule.Ask(NotificationKind.Test));
    }

    [Fact]
    public void TheTestButtonStillSetsTheQuietGap()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.Test));
        Assert.False(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));

        _clock.Advance(BleepRule.QuietGap + TimeSpan.FromSeconds(1));
        Assert.True(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
    }

    [Fact]
    public void SomethingWithNothingToNameItIsStillOneThing()
    {
        var rule = Rule();

        Assert.True(rule.Ask(NotificationKind.Problem));
        _clock.Advance(BleepRule.QuietGap + TimeSpan.FromSeconds(1));

        Assert.False(rule.Ask(NotificationKind.Problem));
    }

    [Fact]
    public void ALongSessionDoesNotKeepEveryNameItEverHeard()
    {
        var rule = Rule();

        for (var i = 0; i < 200; i++)
        {
            _clock.Advance(BleepRule.SameThingWindow + TimeSpan.FromSeconds(1));
            Assert.True(rule.Ask(NotificationKind.FlaggedJoin, $"Person {i}"));
        }
    }
}
