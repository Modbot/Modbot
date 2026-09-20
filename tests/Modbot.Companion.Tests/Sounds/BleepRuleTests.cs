using Modbot.Companion.Sounds;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// The rule between what the client noticed and what the moderator hears: one event is one sound,
/// a rush is still one sound, more than one flagged arrival is the doubled alert, only something
/// worse cuts into the quiet gap, and the Test button always sounds.
/// </summary>
public class BleepRuleTests
{
    private readonly FakeClock _clock = new();

    private BleepRule Rule() => new(_clock);

    /// <summary>Past the longest quiet gap any of the five can leave behind it.</summary>
    private static readonly TimeSpan PastTheGap =
        Bleep.LengthOf(Tune.AlertTwice) + BleepRule.QuietGap + TimeSpan.FromSeconds(1);

    [Fact]
    public void TheFirstThingSounds()
    {
        Assert.Equal(Tune.Alert, Rule().Ask(NotificationKind.FlaggedJoin, "Rin"));
    }

    [Theory]
    [InlineData(NotificationKind.Joined, Tune.Chime)]
    [InlineData(NotificationKind.AlreadyThere, Tune.Chime)]
    [InlineData(NotificationKind.Left, Tune.Chime)]
    [InlineData(NotificationKind.ChangedAvatar, Tune.Chime)]
    [InlineData(NotificationKind.FlaggedJoin, Tune.Alert)]
    [InlineData(NotificationKind.LogStopped, Tune.Urgent)]
    [InlineData(NotificationKind.Problem, Tune.Urgent)]
    public void EachKindGetsItsOwnSound(NotificationKind kind, Tune tune)
    {
        Assert.Equal(tune, Rule().Ask(kind, "Rin"));
    }

    [Fact]
    public void TheSameThingNoticedTwiceIsOneSound()
    {
        var rule = Rule();

        Assert.NotNull(rule.Ask(NotificationKind.Problem, "modbot.example"));

        // Past the quiet gap, so it is the "already heard" rule refusing it and not the rush rule.
        _clock.Advance(PastTheGap);

        Assert.Null(rule.Ask(NotificationKind.Problem, "modbot.example"));
    }

    [Fact]
    public void TheSameThingMuchLaterSoundsAgain()
    {
        var rule = Rule();

        Assert.NotNull(rule.Ask(NotificationKind.Problem, "modbot.example"));
        _clock.Advance(BleepRule.SameThingWindow + TimeSpan.FromSeconds(1));

        Assert.NotNull(rule.Ask(NotificationKind.Problem, "modbot.example"));
    }

    [Fact]
    public void ARushOfOrdinaryArrivalsIsOneSound()
    {
        var rule = Rule();

        Assert.Equal(Tune.Chime, rule.Ask(NotificationKind.Joined, "Rin"));

        foreach (var name in new[] { "Kai", "Mio", "Sol", "Ash" })
            Assert.Null(rule.Ask(NotificationKind.Joined, name));
    }

    [Fact]
    public void SomethingNewAfterTheQuietGapSounds()
    {
        var rule = Rule();

        Assert.NotNull(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
        _clock.Advance(PastTheGap);

        Assert.NotNull(rule.Ask(NotificationKind.FlaggedJoin, "Kai"));
    }

    [Fact]
    public void TwoKindsAboutOnePersonAreTwoThings()
    {
        var rule = Rule();

        Assert.NotNull(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
        _clock.Advance(PastTheGap);

        Assert.NotNull(rule.Ask(NotificationKind.Problem, "Rin"));
    }

    [Fact]
    public void TwoFlaggedPeopleAtOnceAreTheDoubledAlert()
    {
        var rule = Rule();

        Assert.Equal(Tune.Alert, rule.Ask(NotificationKind.FlaggedJoin, "Rin"));

        // Half a second later, while the first alert's quiet gap is still running. The doubled
        // alert is the one thing that gets through it, because "there is more than one of them"
        // is exactly what the gap would otherwise hide.
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(Tune.AlertTwice, rule.Ask(NotificationKind.FlaggedJoin, "Kai"));
    }

    [Fact]
    public void TheDoubledAlertDoesNotKeepDoublingInTheSameGap()
    {
        var rule = Rule();

        Assert.Equal(Tune.Alert, rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(Tune.AlertTwice, rule.Ask(NotificationKind.FlaggedJoin, "Kai"));

        // Everybody after that is silent until the gap has run: worse gets through the gap, and
        // the same again is not worse.
        foreach (var name in new[] { "Mio", "Sol", "Ash" })
        {
            _clock.Advance(TimeSpan.FromMilliseconds(200));
            Assert.Null(rule.Ask(NotificationKind.FlaggedJoin, name));
        }
    }

    [Fact]
    public void AFloodOfFlaggedArrivalsIsNotAMachineGun()
    {
        var rule = Rule();
        var sounds = new List<DateTimeOffset>();

        // A hundred different flagged people, one every tenth of a second, for ten solid seconds.
        for (var i = 0; i < 100; i++)
        {
            if (rule.Ask(NotificationKind.FlaggedJoin, $"Person {i}") is not null)
                sounds.Add(_clock.UtcNow);

            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(sounds.Count <= 6, $"Ten seconds of arrivals made {sounds.Count} sounds.");

        // And only one pair of them is closer together than a quiet gap: the doubled alert cutting
        // in behind the first alert. Everything after that waits for the gap.
        var close = sounds.Zip(sounds.Skip(1), (a, b) => b - a).Count(apart => apart < BleepRule.QuietGap);
        Assert.True(close <= 1, $"{close} sounds came less than the quiet gap apart.");
    }

    [Fact]
    public void OneFlaggedPersonNoticedTwiceIsNotTwoPeople()
    {
        var rule = Rule();

        Assert.Equal(Tune.Alert, rule.Ask(NotificationKind.FlaggedJoin, "Rin"));

        // The same person again, inside the window that counts them: still the same thing, and
        // never the doubled alert.
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
    }

    [Fact]
    public void TwoFlaggedArrivalsFarApartAreTwoOrdinaryAlerts()
    {
        var rule = Rule();

        Assert.Equal(Tune.Alert, rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
        _clock.Advance(BleepRule.ManyFlaggedWindow + TimeSpan.FromSeconds(1));

        Assert.Equal(Tune.Alert, rule.Ask(NotificationKind.FlaggedJoin, "Kai"));
    }

    [Fact]
    public void SomethingWorseCutsIntoTheQuietGap()
    {
        var rule = Rule();

        Assert.Equal(Tune.Chime, rule.Ask(NotificationKind.Joined, "Rin"));

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Equal(Tune.Urgent, rule.Ask(NotificationKind.Problem, "modbot.example"));
    }

    [Fact]
    public void NothingLessSeriousCutsIntoTheQuietGap()
    {
        var rule = Rule();

        Assert.Equal(Tune.Urgent, rule.Ask(NotificationKind.Problem, "modbot.example"));

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Null(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
        Assert.Null(rule.Ask(NotificationKind.Joined, "Kai"));
    }

    [Fact]
    public void SeriousnessOnlyEverGoesUpInsideOneGap()
    {
        var rule = Rule();

        Assert.Equal(Tune.Chime, rule.Ask(NotificationKind.Joined, "Rin"));

        _clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(Tune.Alert, rule.Ask(NotificationKind.FlaggedJoin, "Kai"));

        _clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(Tune.AlertTwice, rule.Ask(NotificationKind.FlaggedJoin, "Mio"));

        _clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(Tune.Urgent, rule.Ask(NotificationKind.Problem, "modbot.example"));

        // Four sounds is the whole of what one gap can hold, because there is nothing worse than
        // the urgent one to follow them with.
        _clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Null(rule.Ask(NotificationKind.LogStopped, "Rin"));
    }

    [Fact]
    public void TheQuietGapIsCountedFromTheEndOfTheSoundAndNotItsStart()
    {
        var rule = Rule();

        rule.AlwaysSounds(Tune.AlertTwice);

        // The doubled alert is more than a second long. A gap counted from the moment it started
        // would have let the next sound begin while it was still playing.
        _clock.Advance(BleepRule.QuietGap + TimeSpan.FromMilliseconds(100));
        Assert.Null(rule.Ask(NotificationKind.Joined, "Rin"));

        _clock.Advance(Bleep.LengthOf(Tune.AlertTwice));
        Assert.Equal(Tune.Chime, rule.Ask(NotificationKind.Joined, "Rin"));
    }

    [Fact]
    public void TheTestButtonAlwaysSounds()
    {
        var rule = Rule();

        Assert.NotNull(rule.Ask(NotificationKind.Test));
        Assert.NotNull(rule.Ask(NotificationKind.Test));
    }

    [Fact]
    public void TheTestButtonStillSetsTheQuietGap()
    {
        var rule = Rule();

        Assert.NotNull(rule.Ask(NotificationKind.Test));
        Assert.Null(rule.Ask(NotificationKind.Joined, "Rin"));

        _clock.Advance(PastTheGap);
        Assert.NotNull(rule.Ask(NotificationKind.Joined, "Rin"));
    }

    [Fact]
    public void ASoundSomebodyPressedForStillSetsTheQuietGap()
    {
        var rule = Rule();

        rule.AlwaysSounds(Tune.Urgent);

        // And it is as serious as it sounds: nothing less than the urgent one follows it.
        Assert.Null(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));

        _clock.Advance(PastTheGap);
        Assert.NotNull(rule.Ask(NotificationKind.FlaggedJoin, "Rin"));
    }

    [Fact]
    public void SomethingWithNothingToNameItIsStillOneThing()
    {
        var rule = Rule();

        Assert.NotNull(rule.Ask(NotificationKind.Problem));
        _clock.Advance(PastTheGap);

        Assert.Null(rule.Ask(NotificationKind.Problem));
    }

    [Fact]
    public void ALongSessionDoesNotKeepEveryNameItEverHeard()
    {
        var rule = Rule();

        for (var i = 0; i < 200; i++)
        {
            _clock.Advance(BleepRule.SameThingWindow + TimeSpan.FromSeconds(1));
            Assert.NotNull(rule.Ask(NotificationKind.FlaggedJoin, $"Person {i}"));
        }
    }
}
