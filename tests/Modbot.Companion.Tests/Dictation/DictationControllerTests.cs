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

        await rig.StartListening();

        Assert.Equal(DictationState.Listening, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Starts);
    }

    [Fact]
    public async Task ATapShowsStartingUntilTheMicrophoneDeliversItsFirstSound()
    {
        var rig = new Rig { Recorder = { FlowsAtOnce = false } };

        rig.Controller.Toggle();
        Assert.Equal(DictationState.Starting, rig.Controller.State);

        await rig.Controller.WhenSettled;
        Assert.Equal(1, rig.Recorder.Starts);
        Assert.Equal(DictationState.Starting, rig.Controller.State);

        rig.Recorder.RaiseFlowing();
        Assert.Equal(DictationState.Listening, rig.Controller.State);
    }

    [Fact]
    public async Task WordsSaidWhileTheButtonSaysStartingAreKept()
    {
        var rig = new Rig { Recorder = { FlowsAtOnce = false, Heard = Speech }, Recognizer = { Says = "hello there" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        await rig.StartListening();
        Assert.Equal(DictationState.Starting, rig.Controller.State);

        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Equal(["hello there"], said);
        Assert.Equal(1, rig.Recorder.Stops);
    }

    [Fact]
    public async Task TheMicrophoneStaysOpenForAMomentAfterTheTapToStop()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(300)) { Recorder = { Heard = Speech }, Recognizer = { Says = "hello" } };

        await rig.StartListening();
        rig.Controller.Toggle();

        Assert.Equal(DictationState.Writing, rig.Controller.State);
        Assert.Equal(0, rig.Recorder.Stops);

        await rig.Controller.WhenSettled;
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(1, rig.Recognizer.Calls);
    }

    [Fact]
    public async Task ATapWhileTheMicrophoneListensOnDoesNothingMore()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(300)) { Recorder = { Heard = Speech }, Recognizer = { Says = "hello" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        await rig.StartListening();
        rig.Controller.Toggle();
        rig.Controller.Toggle();
        Assert.Equal(DictationState.Writing, rig.Controller.State);
        await rig.Controller.WhenSettled;

        Assert.Equal(["hello"], said);
        Assert.Equal(1, rig.Recorder.Starts);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(1, rig.Recognizer.Calls);
    }

    [Fact]
    public async Task AbandonWhileTheMicrophoneListensOnWritesNothingOut()
    {
        var rig = new Rig(TimeSpan.FromHours(1)) { Recorder = { Heard = Speech }, Recognizer = { Says = "hello" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        await rig.StartListening();
        rig.Controller.Toggle();
        rig.Controller.Abandon();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(0, rig.Recognizer.Calls);
    }

    [Fact]
    public async Task ASecondTapWritesWhatWasSaidOutAndGoesBackToIdle()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "  hello there  " } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        await rig.StartListening();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Equal(["hello there"], said);
        Assert.Equal(DictationState.Sending, rig.Controller.State);
        rig.Controller.SendEnded(null);
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

        await rig.StartListening();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(0, rig.Recognizer.Calls);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
    }

    [Fact]
    public async Task ARecordingNobodySpokeInLeavesTheNoticeNothingHeard()
    {
        var rig = new Rig { Recorder = { Heard = Silence } };

        await rig.StartListening();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Equal("Nothing heard", rig.Controller.Notice);
    }

    [Fact]
    public async Task ASendThatFailsLeavesTheNoticeCouldNotSendAndGoesBackToIdle()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "hello" } };

        await rig.StartListening();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;
        rig.Controller.SendEnded("The server could not be reached");

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal("Could not send", rig.Controller.Notice);
    }

    [Fact]
    public async Task ATapWhileSendingActsLikeIdleAndStartsListening()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "hello" } };

        await rig.StartListening();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;
        await rig.StartListening();

        Assert.Equal(DictationState.Listening, rig.Controller.State);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, DictationController.LevelSteps)]
    public void TheLevelStepRunsFromSilenceToFull(float peak, int step) =>
        Assert.Equal(step, DictationController.StepOf(peak));

    [Fact]
    public async Task ARecordingTheEngineHeardNothingInSendsNothing()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "   " } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        await rig.StartListening();
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

        await rig.StartListening();
        rig.Controller.Toggle();
        rig.Controller.Toggle();

        Assert.Equal(DictationState.Preparing, rig.Controller.State);
        Assert.Equal(0, rig.Recorder.Starts);

        rig.Prepared.SetResult(null);
        await rig.Controller.WhenSettled;

        // The fetch starts on another thread, so it is counted once it has run, not straight after the tap.
        Assert.Equal(1, rig.PrepareCalls);
    }

    [Fact]
    public async Task TapsAreIgnoredWhileTheSoundIsBeingWrittenOut()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Hold = new TaskCompletionSource() } };

        await rig.StartListening();
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

        await rig.StartListening();
        rig.Recorder.RaiseLimit();
        await rig.Controller.WhenSettled;

        Assert.Equal(["that is a long one"], said);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(DictationState.Sending, rig.Controller.State);
        rig.Controller.SendEnded(null);
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

        await rig.StartListening();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Contains("broken", rig.Controller.Problem);
    }

    [Fact]
    public async Task AMicrophoneThatWillNotOpenLeavesAProblemAndStaysIdle()
    {
        var rig = new Rig { Recorder = { StartProblem = "No microphone." } };

        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal("No microphone.", rig.Controller.Problem);
        Assert.Equal("Could not listen", rig.Controller.Notice);
    }

    [Fact]
    public async Task AbandonClosesTheMicrophoneAndWritesNothingOut()
    {
        var rig = new Rig { Recorder = { Heard = Speech } };

        await rig.StartListening();
        rig.Controller.Abandon();

        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(0, rig.Recognizer.Calls);
    }

    [Fact]
    public async Task ASlowMicrophoneDoesNotHoldTheButtonUp()
    {
        var rig = new Rig { Recorder = { StartHold = new TaskCompletionSource(), FlowsAtOnce = false } };

        rig.Controller.Toggle();
        await rig.Recorder.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // These would wait for the microphone if it opened under the controller's lock.
        var state = await Task.Run(() => rig.Controller.State, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DictationState.Starting, state);

        rig.Recorder.StartHold.SetResult();
        await rig.Controller.WhenSettled;
        rig.Recorder.RaiseFlowing();

        Assert.Equal(DictationState.Listening, rig.Controller.State);
    }

    [Fact]
    public async Task ATapToStopWhileTheMicrophoneOpensKeepsTheWordsHeardAfterItOpens()
    {
        var rig = new Rig { Recorder = { StartHold = new TaskCompletionSource(), Heard = Speech }, Recognizer = { Says = "hello" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        await rig.Recorder.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Run(rig.Controller.Toggle, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DictationState.Writing, rig.Controller.State);

        rig.Recorder.StartHold.SetResult();
        await rig.Controller.WhenSettled;

        Assert.Equal(["hello"], said);
        Assert.Equal(1, rig.Recorder.Starts);
        Assert.Equal(1, rig.Recorder.Stops);
    }

    [Fact]
    public async Task ATapToStopRightAfterTheFirstStillOpensTheMicrophoneAndWritesWhatItHeard()
    {
        var rig = new Rig { Recorder = { Heard = Speech }, Recognizer = { Says = "hello" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        // The second tap may land before or after the microphone opens; the words are kept either way.
        rig.Controller.Toggle();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Equal(["hello"], said);
        Assert.Equal(1, rig.Recorder.Starts);
        Assert.Equal(1, rig.Recorder.Stops);
    }

    [Fact]
    public async Task ATapToStopWhenTheMicrophoneCannotOpenShowsTheProblem()
    {
        var rig = new Rig { Recorder = { StartProblem = "No microphone." } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal("No microphone.", rig.Controller.Problem);
        Assert.Equal(DictationController.CouldNotListen, rig.Controller.Notice);
        Assert.Equal(0, rig.Recorder.Stops);
    }

    [Fact]
    public async Task AbandonWhileTheMicrophoneOpensClosesItAgain()
    {
        var rig = new Rig { Recorder = { StartHold = new TaskCompletionSource(), Heard = Speech }, Recognizer = { Says = "hello" } };
        var said = new List<string>();
        rig.Controller.Finished += said.Add;

        rig.Controller.Toggle();
        await rig.Recorder.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        rig.Controller.Abandon();
        Assert.Equal(DictationState.Idle, rig.Controller.State);

        rig.Recorder.StartHold.SetResult();
        await rig.Controller.WhenSettled;

        Assert.Empty(said);
        Assert.Equal(DictationState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Starts);
        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(0, rig.Recognizer.Calls);
    }

    private sealed class Rig
    {
        /// <param name="listenOnFor">How long the microphone listens on after the tap to stop; none unless a test is about it.</param>
        public Rig(TimeSpan? listenOnFor = null)
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
                (_, _) => { },
                listenOnFor: listenOnFor ?? TimeSpan.Zero);
        }

        /// <summary>A tap, then waits until the microphone has been opened.</summary>
        public async Task StartListening()
        {
            Controller.Toggle();
            await Controller.WhenSettled;
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

        public event Action? Flowing;

        /// <summary>Whether the first sound arrives as soon as the microphone opens; otherwise a test raises it.</summary>
        public bool FlowsAtOnce { get; set; } = true;

        public float[] Heard { get; set; } = [];

        public float Level => 0;

        public string? StartProblem { get; set; }

        /// <summary>When set, opening waits on it, like a microphone that is slow to open.</summary>
        public TaskCompletionSource? StartHold { get; set; }

        /// <summary>Completes when opening has begun.</summary>
        public TaskCompletionSource StartEntered { get; } = new();

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public string? Start()
        {
            StartEntered.TrySetResult();
            StartHold?.Task.GetAwaiter().GetResult();

            if (StartProblem is not null)
                return StartProblem;

            Starts++;

            if (FlowsAtOnce)
                Flowing?.Invoke();

            return null;
        }

        public float[] Stop()
        {
            Stops++;
            return Heard;
        }

        public void RaiseLimit() => LimitReached?.Invoke();

        public void RaiseFlowing() => Flowing?.Invoke();
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
