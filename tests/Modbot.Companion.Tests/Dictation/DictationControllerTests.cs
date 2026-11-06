using Modbot.Companion.Dictation;

namespace Modbot.Companion.Tests.Dictation;

/// <summary>What a tap on Dictate does, with a made-up microphone and a made-up speech engine.</summary>
public class DictationControllerTests
{
    private static readonly float[] Speech = Enumerable.Repeat(0.3f, 16_000).ToArray();
    private static readonly float[] Silence = new float[16_000];

    [Fact]
    public async Task ATapWithTheModelReadyStartsListening()
    {
        var rig = new Rig();

        rig.Controller.Toggle();

        Assert.Equal(DictationState.Listening, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Starts);
        await rig.Controller.WhenSettled;
    }

    [Fact]
    public async Task ASecondTapWritesWhatWasSaidOutAndGoesBackToIdle()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "  hello there  " } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Equal(["hello there"], said);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Null(rig.Controller.Problem);
        Assert.Equal(1, rig.Recorder.Stops);
    }

    [Fact]
    public async Task ARecordingThatWasQuietIsNotWrittenOutAndNothingIsSent()
    {
        var rig = new Rig { Recorder = { Heard = Silence }, Recognizer = { Says = "thank you" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(0, rig.Recognizer.Calls);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
    }

    [Fact]
    public async Task ARecordingTheEngineHeardNothingInSendsNothing()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "   " } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(1, rig.Recognizer.Calls);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
    }

    [Fact]
    public async Task ATapWithoutTheModelFetchesItAndGoesBackToIdleWithoutListening()
    {
        var rig = new Rig { ModelPresent = false };
        var states = new List<DictationState>();
        rig.Controller.Changed += () => states.Add(rig.Controller.State);

        rig.Controller.Toggle();
        Assert.Equal(DictationState.Preparing, rig.Controller.State);

        rig.Prepared.SetResult(null);
        await rig.Controller.WhenSettled;

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal(DictationState.Idle, states[^1]);
        Assert.Equal(0, rig.Recorder.Starts);
        Assert.Null(rig.Controller.Problem);
    }

    [Fact]
    public async Task ADownloadThatFailsLeavesASentenceAndGoesBackToIdle()
    {
        var rig = new Rig { ModelPresent = false };

        rig.Controller.Toggle();
        rig.Prepared.SetResult("A test copy downloads nothing.");
        await rig.Controller.WhenSettled;

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal("A test copy downloads nothing.", rig.Controller.Problem);
    }

    [Fact]
    public async Task TapsAreIgnoredWhileTheModelIsBeingFetched()
    {
        var rig = new Rig { ModelPresent = false };

        rig.Controller.Toggle();
        rig.Controller.Toggle();
        rig.Controller.Toggle();

        Assert.Equal(DictationState.Preparing, rig.Controller.State);
        Assert.Equal(0, rig.Recorder.Starts);
        Assert.Equal(1, rig.PrepareCalls);

        rig.Prepared.SetResult(null);
        await rig.Controller.WhenSettled;
    }

    [Fact]
    public async Task TapsAreIgnoredWhileTheSoundIsBeingWrittenOut()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Hold = new TaskCompletionSource() } };

        rig.Controller.Toggle();
        rig.Controller.Toggle();
        Assert.Equal(DictationState.Writing, rig.Controller.State);

        rig.Controller.Toggle();
        Assert.Equal(DictationState.Writing, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Starts);

        rig.Recognizer.Hold!.SetResult();
        await rig.Controller.WhenSettled;
        Assert.Equal(DictationState.Idle, rig.Controller.State);
    }

    [Fact]
    public async Task TheLimitActsLikeASecondTap()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "that is a long one" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        rig.Recorder.RaiseLimit();
        await rig.Controller.WhenSettled;

        Assert.Equal(["that is a long one"], said);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
    }

    [Fact]
    public async Task TheLimitWhileNotListeningDoesNothing()
    {
        var rig = new Rig();

        rig.Recorder.RaiseLimit();
        await rig.Controller.WhenSettled;

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal(0, rig.Recorder.Stops);
    }

    [Fact]
    public async Task AnEngineThatThrowsLeavesAProblemAndGoesBackToIdle()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Throws = new InvalidOperationException("broken") } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Contains("broken", rig.Controller.Problem);
    }

    [Fact]
    public void AMicrophoneThatWillNotOpenLeavesAProblemAndStaysIdle()
    {
        var rig = new Rig { Recorder = { StartProblem = "No microphone." } };

        rig.Controller.Toggle();

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal("No microphone.", rig.Controller.Problem);
    }

    [Fact]
    public void AbandonClosesTheMicrophoneAndWritesNothingOut()
    {
        var rig = new Rig { Recorder = { Heard = Speech } };

        rig.Controller.Toggle();
        rig.Controller.Abandon();

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(0, rig.Recognizer.Calls);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Controller = new DictationController(
                Recorder,
                Recognizer,
                () => ModelPresent,
                _ =>
                {
                    PrepareCalls++;
                    return Prepared.Task;
                },
                (_, _) => { });
        }

        public FakeRecorder Recorder { get; } = new();

        public FakeRecognizer Recognizer { get; } = new();

        public bool ModelPresent { get; init; } = true;

        public TaskCompletionSource<string?> Prepared { get; } = new();

        public int PrepareCalls { get; private set; }

        public DictationController Controller { get; }
    }

    private sealed class FakeRecorder : IDictationRecorder
    {
        public event Action? LimitReached;

        public float[] Heard { get; set; } = [];

        public string? StartProblem { get; set; }

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public string? Start()
        {
            if (StartProblem is not null)
                return StartProblem;

            Starts++;
            return null;
        }

        public float[] Stop()
        {
            Stops++;
            return Heard;
        }

        public void RaiseLimit() => LimitReached?.Invoke();
    }

    private sealed class FakeRecognizer : IDictationRecognizer
    {
        public string Says { get; set; } = string.Empty;

        public Exception? Throws { get; set; }

        public TaskCompletionSource? Hold { get; set; }

        public int Calls { get; private set; }

        public string Recognize(float[] samples)
        {
            Calls++;
            Hold?.Task.GetAwaiter().GetResult();

            if (Throws is not null)
                throw Throws;

            return Says;
        }
    }
}
