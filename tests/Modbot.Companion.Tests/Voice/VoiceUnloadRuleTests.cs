using Modbot.Companion.Voice;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// When the voice engine is let go of again: long enough after the last line that a busy instance
/// is one load, soon enough that a quiet one gives the memory back.
/// </summary>
public class VoiceUnloadRuleTests
{
    private readonly FakeClock _clock = new();

    private VoiceUnloadRule Rule() => new(_clock);

    [Fact]
    public void NothingLoadedIsNothingToLetGoOf()
    {
        var rule = Rule();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.False(rule.ShouldUnload(loaded: false, busy: false));
    }

    [Fact]
    public void KeptWhileTheWindowIsStillRunning()
    {
        var rule = Rule();
        rule.Used();

        _clock.Advance(VoiceUnloadRule.KeepLoadedFor - TimeSpan.FromSeconds(1));

        Assert.False(rule.ShouldUnload(loaded: true, busy: false));
    }

    [Fact]
    public void LetGoOfOnceTheWindowHasRunOut()
    {
        var rule = Rule();
        rule.Used();

        _clock.Advance(VoiceUnloadRule.KeepLoadedFor);

        Assert.True(rule.ShouldUnload(loaded: true, busy: false));
    }

    [Fact]
    public void ABurstKeepsItLoaded()
    {
        // Several people walk in, and more follow over the next couple of minutes. Each line
        // starts the window again, so the whole burst is one load rather than one load a name.
        var rule = Rule();

        for (var line = 0; line < 5; line++)
        {
            rule.Used();
            _clock.Advance(TimeSpan.FromSeconds(30));
            Assert.False(rule.ShouldUnload(loaded: true, busy: false));
        }

        _clock.Advance(VoiceUnloadRule.KeepLoadedFor);
        Assert.True(rule.ShouldUnload(loaded: true, busy: false));
    }

    [Fact]
    public void NeverWhileALineIsWaitingOrBeingSaid()
    {
        var rule = Rule();
        rule.Used();

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.False(rule.ShouldUnload(loaded: true, busy: true));
        Assert.True(rule.ShouldUnload(loaded: true, busy: false));
    }

    [Fact]
    public void TurningTheVoiceOffLetsGoOfItWithoutWaiting()
    {
        var rule = Rule();
        rule.Used();
        rule.DropWhenQuiet();

        Assert.True(rule.ShouldUnload(loaded: true, busy: false));
    }

    [Fact]
    public void EvenThenNotInTheMiddleOfALine()
    {
        // Turning the voice off drops what was waiting, but a line already being played finishes.
        var rule = Rule();
        rule.DropWhenQuiet();

        Assert.False(rule.ShouldUnload(loaded: true, busy: true));
    }

    [Fact]
    public void TurningItOnAgainStartsAFreshWindow()
    {
        var rule = Rule();
        rule.DropWhenQuiet();
        rule.Used();

        Assert.False(rule.ShouldUnload(loaded: true, busy: false));
    }

    [Fact]
    public void TheWindowIsLongerThanTheLongestALineWaitsInTheQueue()
    {
        // A line the queue is still holding must never be waiting on an engine the window has
        // already let go of.
        Assert.True(VoiceUnloadRule.KeepLoadedFor > AnnouncementQueue.AlertMaxAge);
        Assert.True(VoiceUnloadRule.KeepLoadedFor > AnnouncementQueue.PresenceMaxAge);
    }
}
