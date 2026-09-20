using Modbot.Companion.Listening;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// The client's name, and the few seconds after it: what arms it, what it refuses, and the quiet
/// way the waiting ends when nobody says anything else.
/// </summary>
public class NameHeardTests
{
    private readonly FakeClock _clock = new();

    private NameHeard Rule() => new(_clock);

    [Fact]
    public void AClientThatHasNotHeardItsNameIsNotWaiting()
    {
        var rule = Rule();

        Assert.False(rule.Waiting);
    }

    [Fact]
    public void AnInstructionIsRefusedUntilTheNameHasBeenSaid()
    {
        var rule = Rule();

        // This is the whole of the promise. Nothing said to the room, to a friend or to VRChat is
        // acted on, however exactly it matches, until the client has been called by name.
        Assert.False(rule.Take());
    }

    [Fact]
    public void TheNameStartsTheWaitingAndAnInstructionIsThenTaken()
    {
        var rule = Rule();

        rule.Heard();
        Assert.True(rule.Waiting);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(rule.Take());
    }

    [Fact]
    public void EitherSpellingOfTheNameArmsIt()
    {
        // The two spellings are the same word to a person and two runs of pieces to the engine, so
        // whichever one the matcher lands on starts the same waiting. Both are in the list the
        // matcher is given (PhraseModelTests), and this is the other half: the rule does not care
        // which arrived.
        var model = PhraseModel.Default;

        foreach (var spelling in model.Names)
        {
            Assert.True(model.IsTheName(spelling.Pieces));

            var rule = Rule();
            rule.Heard();

            Assert.True(rule.Waiting);
            Assert.True(rule.Take());
        }
    }

    [Fact]
    public void SayingTheNameAndNothingElseDoesNothingAndLapsesQuietly()
    {
        var rule = Rule();

        rule.Heard();
        Assert.True(rule.Waiting);

        _clock.Advance(NameHeard.Window);

        Assert.False(rule.Waiting);
        Assert.False(rule.Take());
    }

    [Fact]
    public void AnInstructionArrivingAfterTheWaitingHasEndedIsRefused()
    {
        var rule = Rule();

        rule.Heard();

        // "Hide overlay" said to somebody in the instance half a minute after anybody said
        // "Modbot" must reach nothing.
        _clock.Advance(NameHeard.Window + TimeSpan.FromSeconds(25));

        Assert.False(rule.Take());
    }

    [Fact]
    public void TheLastMomentOfTheWaitingStillCounts()
    {
        var rule = Rule();

        rule.Heard();
        _clock.Advance(NameHeard.Window - TimeSpan.FromMilliseconds(1));

        Assert.True(rule.Waiting);
        Assert.True(rule.Take());
    }

    [Fact]
    public void OneNameIsOneInstruction()
    {
        var rule = Rule();

        rule.Heard();

        Assert.True(rule.Take());

        // Taking it ends the waiting there and then, so the same run of sound offered a second
        // time reaches nothing, and so does anything else said before the name comes again.
        Assert.False(rule.Waiting);
        Assert.False(rule.Take());
    }

    [Fact]
    public void SayingTheNameAgainStartsTheWaitingOver()
    {
        var rule = Rule();

        rule.Heard();
        _clock.Advance(NameHeard.Window - TimeSpan.FromSeconds(1));

        rule.Heard();
        _clock.Advance(NameHeard.Window - TimeSpan.FromSeconds(1));

        Assert.True(rule.Take());
    }

    [Fact]
    public void ForgettingStopsTheWaitingAtOnce()
    {
        var rule = Rule();

        rule.Heard();
        Assert.True(rule.Waiting);

        // Used when listening stops -- VRChat closing, or the switch being turned off. A client
        // whose microphone has closed must not still be waiting to be told something.
        rule.Forget();

        Assert.False(rule.Waiting);
        Assert.False(rule.Take());
    }

    [Fact]
    public void TheWaitingIsLongEnoughToSpeakInAndTooShortToBeAConversation()
    {
        // "Modbot, show overlay" takes about a second and a half to say, and the matcher is a beat
        // behind the speaker. Much shorter and a moderator who pauses loses it; much longer and the
        // words "hide overlay" in an ordinary sentence would hide a panel.
        Assert.InRange(NameHeard.Window, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
    }
}
