using Modbot.Companion.Dictation;

namespace Modbot.Companion.Tests.Feedback;

/// <summary>Dictate and Feedback sharing one microphone: one holds it at a time.</summary>
public class MicrophoneTurnsTests
{
    [Fact]
    public void TheSecondToAskIsRefusedWhileTheFirstHoldsTheMicrophone()
    {
        var inner = new FakeRecorder();
        var turns = new MicrophoneTurns();
        var dictate = turns.Wrap(inner, "Dictate");
        var feedback = turns.Wrap(inner, "Feedback");

        Assert.Null(dictate.Start());
        Assert.Equal("Dictate is listening.", feedback.Start());
        Assert.Equal(1, inner.Starts);
    }

    [Fact]
    public void StoppingHandsTheMicrophoneToTheOther()
    {
        var inner = new FakeRecorder();
        var turns = new MicrophoneTurns();
        var dictate = turns.Wrap(inner, "Dictate");
        var feedback = turns.Wrap(inner, "Feedback");

        dictate.Start();
        dictate.Stop();

        Assert.Null(feedback.Start());
        Assert.Equal(2, inner.Starts);
    }

    [Fact]
    public void AnotherUserCannotStopTheMicrophoneItDoesNotHold()
    {
        var inner = new FakeRecorder();
        var turns = new MicrophoneTurns();
        var dictate = turns.Wrap(inner, "Dictate");
        var feedback = turns.Wrap(inner, "Feedback");

        dictate.Start();

        Assert.Empty(feedback.Stop());
        Assert.Equal(0, inner.Stops);
    }

    [Fact]
    public void TheLimitIsToldOnlyToTheOneThatHoldsTheMicrophone()
    {
        var inner = new FakeRecorder();
        var turns = new MicrophoneTurns();
        var dictate = turns.Wrap(inner, "Dictate");
        var feedback = turns.Wrap(inner, "Feedback");
        var dictateLimits = 0;
        var feedbackLimits = 0;
        dictate.LimitReached += () => dictateLimits++;
        feedback.LimitReached += () => feedbackLimits++;

        feedback.Start();
        inner.RaiseLimit();

        Assert.Equal(0, dictateLimits);
        Assert.Equal(1, feedbackLimits);
    }
}
