using Modbot.Companion.Dictation;
using Modbot.Companion.Feedback;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Feedback;

internal static class Heard
{
    public static readonly float[] Speech = Enumerable.Repeat(0.3f, 16_000).ToArray();
}

/// <summary>A microphone that hears whatever it is told to and counts how it was used.</summary>
internal sealed class FakeRecorder : IDictationRecorder
{
    public event Action? LimitReached;

    public event Action? Flowing;

    public float[] Sound { get; set; } = Heard.Speech;

    public float Level => 0;

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public string? Start()
    {
        Starts++;
        return null;
    }

    public float[] Stop()
    {
        Stops++;
        return Sound;
    }

    public void RaiseLimit() => LimitReached?.Invoke();

    public void RaiseFlowing() => Flowing?.Invoke();
}

internal sealed class FakeRecognizer : IDictationRecognizer
{
    public string Says { get; set; } = "the map is broken";

    public string Recognize(float[] samples) => Says;
}

/// <summary>The paired server's feedback requests, answering what it is told to and keeping what was sent.</summary>
internal sealed class FakeFeedbackClient : IFeedbackClient
{
    public bool? Available { get; set; } = true;

    public FeedbackDraft? Tidied { get; set; }

    public Exception? TidyThrows { get; set; }

    public string? SendProblem { get; set; }

    public List<string> Tidies { get; } = [];

    public List<(string Kind, string Title, string Details)> Sent { get; } = [];

    public Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken)
        => Task.FromResult(Available);

    public Task<FeedbackDraft?> TidyAsync(ServerPairing pairing, string text, CancellationToken cancellationToken)
    {
        Tidies.Add(text);

        if (TidyThrows is not null)
            throw TidyThrows;

        return Task.FromResult(Tidied);
    }

    public Task<FeedbackSendResult> SendAsync(ServerPairing pairing, string kind, string title, string details, CancellationToken cancellationToken)
    {
        Sent.Add((kind, title, details));
        return Task.FromResult(new FeedbackSendResult(SendProblem));
    }
}
