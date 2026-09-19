using Modbot.Companion.Listening;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// One spoken sentence is one clip: the rule that stops a phrase firing again while the same
/// utterance is still arriving, or while a moderator is repeating themselves.
/// </summary>
public class PhraseHeardTests
{
    private readonly FakeClock _clock = new();

    private PhraseHeard Rule() => new(_clock);

    [Fact]
    public void TheFirstOneFires()
    {
        Assert.True(Rule().Ask());
    }

    [Fact]
    public void TheSameUtteranceArrivingAgainDoesNotFireTwice()
    {
        var rule = Rule();

        Assert.True(rule.Ask());

        // "Modbot, clip that" carries the pieces of "Modbot, clip this" inside it, and the matcher
        // is offered the same run of sound more than once as it catches up. Two clips, each a
        // second shorter than the last, is not what anybody asked for.
        Assert.False(rule.Ask());

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(rule.Ask());
    }

    [Fact]
    public void SomebodySayingItAgainBecauseTheyAreNotSureDoesNotFireTwice()
    {
        var rule = Rule();

        Assert.True(rule.Ask());

        _clock.Advance(PhraseHeard.QuietGap - TimeSpan.FromMilliseconds(1));
        Assert.False(rule.Ask());
    }

    [Fact]
    public void ASecondRealMomentLaterOnIsASecondClip()
    {
        var rule = Rule();

        Assert.True(rule.Ask());

        _clock.Advance(PhraseHeard.QuietGap);
        Assert.True(rule.Ask());
    }

    [Fact]
    public void TheGapIsShortEnoughToNeverSwallowASecondMoment()
    {
        // A clip covers two to five minutes. A gap anywhere near that long would mean a moderator
        // losing a real second moment; a gap anywhere near zero would mean three clips per
        // sentence. Seconds is the right order of magnitude, and this pins it.
        Assert.InRange(PhraseHeard.QuietGap, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ForgettingMakesTheNextOneFire()
    {
        var rule = Rule();

        Assert.True(rule.Ask());
        Assert.False(rule.Ask());

        // Used when listening stops and starts again -- VRChat closing and reopening, or the
        // switch being turned off and on. A fresh start is not "the same utterance".
        rule.Forget();
        Assert.True(rule.Ask());
    }
}
