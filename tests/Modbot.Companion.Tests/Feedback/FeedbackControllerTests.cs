using Modbot.Companion.Dictation;
using Modbot.Companion.Feedback;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Feedback;

/// <summary>What a tap on Feedback does, with a made-up microphone, speech engine and server.</summary>
public class FeedbackControllerTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public async Task WithAIOnTheDraftIsTheServersTidiedKindTitleAndDetails()
    {
        var rig = new Rig();
        rig.Client.Tidied = new FeedbackDraft("bug", "Map will not open", "The map does not open.", true);

        await rig.SpeakAsync();

        Assert.Equal(FeedbackState.Draft, rig.Controller.State);
        Assert.Equal(new FeedbackDraft("bug", "Map will not open", "The map does not open.", true), rig.Controller.Draft);
        Assert.Equal(["the map is broken"], rig.Client.Tidies);
    }

    [Fact]
    public async Task WithAIOffTheDraftIsTheWordsAsSaidAndNoKindIsChosen()
    {
        var rig = new Rig();
        rig.Client.Tidied = null;

        await rig.SpeakAsync();

        Assert.Equal(FeedbackState.Draft, rig.Controller.State);
        Assert.Equal(new FeedbackDraft(null, null, "the map is broken", false), rig.Controller.Draft);
    }

    [Fact]
    public async Task AServerThatCannotBeAskedToTidyStillGivesTheWordsAsSaid()
    {
        var rig = new Rig();
        rig.Client.TidyThrows = new HttpRequestException("no route");

        await rig.SpeakAsync();

        Assert.Equal(FeedbackState.Draft, rig.Controller.State);
        Assert.Equal(new FeedbackDraft(null, null, "the map is broken", false), rig.Controller.Draft);
    }

    [Fact]
    public async Task CancellingTheDraftSendsNothingAndKeepsNothing()
    {
        var rig = new Rig();
        await rig.SpeakAsync();

        rig.Controller.Cancel();

        Assert.Equal(FeedbackState.Idle, rig.Controller.State);
        Assert.Null(rig.Controller.Draft);
        Assert.Empty(rig.Client.Sent);
    }

    [Fact]
    public async Task SendingSavesWhatThePersonChoseAndGoesBackToIdleWithTheNoticeSent()
    {
        var rig = new Rig();
        await rig.SpeakAsync();

        await rig.Controller.SendAsync("Idea", " A better map ", " Make the map bigger. ");

        Assert.Equal([("idea", "A better map", "Make the map bigger.")], rig.Client.Sent);
        Assert.Equal(FeedbackState.Idle, rig.Controller.State);
        Assert.Null(rig.Controller.Draft);
        Assert.Equal(FeedbackController.Sent, rig.Controller.Notice);
    }

    [Fact]
    public async Task ASendThatFailsKeepsTheDraftAndSaysWhy()
    {
        var rig = new Rig();
        rig.Client.SendProblem = "Could not send. Try again.";
        await rig.SpeakAsync();

        await rig.Controller.SendAsync("bug", "", "The map is broken.");

        Assert.Equal(FeedbackState.Draft, rig.Controller.State);
        Assert.Equal("The map is broken.", rig.Controller.Draft?.Details);
        Assert.Equal("Could not send. Try again.", rig.Controller.Problem);
    }

    [Theory]
    [InlineData(null, "The map is broken.")]
    [InlineData("rant", "The map is broken.")]
    [InlineData("bug", "   ")]
    public async Task NothingIsSentWithoutAKindOrWithoutDetails(string? kind, string details)
    {
        var rig = new Rig();
        await rig.SpeakAsync();

        await rig.Controller.SendAsync(kind, "Title", details);

        Assert.Empty(rig.Client.Sent);
        Assert.Equal(FeedbackState.Draft, rig.Controller.State);
    }

    [Fact]
    public async Task TapsAreIgnoredWhileTheDraftIsUp()
    {
        var rig = new Rig();
        await rig.SpeakAsync();

        rig.Controller.Toggle();

        Assert.Equal(FeedbackState.Draft, rig.Controller.State);
        Assert.Equal(1, rig.Recorder.Starts);
    }

    [Fact]
    public async Task AbandonDropsTheDraftWithoutSendingIt()
    {
        var rig = new Rig();
        await rig.SpeakAsync();

        rig.Controller.Abandon();

        Assert.Equal(FeedbackState.Idle, rig.Controller.State);
        Assert.Empty(rig.Client.Sent);
    }

    [Fact]
    public async Task ATapShowsStartingUntilTheMicrophoneDeliversItsFirstSound()
    {
        var rig = new Rig();

        rig.Controller.Toggle();
        Assert.Equal(FeedbackState.Starting, rig.Controller.State);

        await rig.Controller.WhenSettled;
        Assert.Equal(FeedbackState.Starting, rig.Controller.State);

        rig.Recorder.RaiseFlowing();
        Assert.Equal(FeedbackState.Listening, rig.Controller.State);
    }

    [Fact]
    public async Task FeedbackAndDictateCannotListenAtTheSameTime()
    {
        var rig = new Rig();
        var dictate = rig.DictateController();

        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;
        rig.Recorder.RaiseFlowing();
        dictate.Toggle();
        await dictate.WhenSettled;

        Assert.Equal(FeedbackState.Listening, rig.Controller.State);
        Assert.Equal(DictationState.Idle, dictate.State);
        Assert.Equal("Feedback is listening.", dictate.Problem);
        Assert.Equal(1, rig.Recorder.Starts);

        await rig.Controller.WhenSettled;
    }

    [Fact]
    public async Task DictateKeepsTheMicrophoneAgainstFeedbackAndHandsItBackWhenDone()
    {
        var rig = new Rig();
        var dictate = rig.DictateController();

        dictate.Toggle();
        await dictate.WhenSettled;
        rig.Recorder.RaiseFlowing();
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;

        Assert.Equal(DictationState.Listening, dictate.State);
        Assert.Equal(FeedbackState.Idle, rig.Controller.State);
        Assert.Equal("Dictate is listening.", rig.Controller.Problem);

        dictate.Toggle();
        await dictate.WhenSettled;
        dictate.SendEnded(null);
        rig.Controller.Toggle();
        await rig.Controller.WhenSettled;
        rig.Recorder.RaiseFlowing();

        Assert.Equal(FeedbackState.Listening, rig.Controller.State);
        await rig.Controller.WhenSettled;
    }

    private sealed class Rig
    {
        public Rig()
        {
            var turns = new MicrophoneTurns();
            Turns = turns;
            Controller = new FeedbackController(
                turns.Wrap(Recorder, "Feedback"),
                Recognizer,
                () => true,
                _ => Task.FromResult<string?>(null),
                Client,
                () => Pairing,
                (_, _) => { },
                listenOnFor: TimeSpan.Zero);
        }

        public MicrophoneTurns Turns { get; }

        public FakeRecorder Recorder { get; } = new();

        public FakeRecognizer Recognizer { get; } = new();

        public FakeFeedbackClient Client { get; } = new();

        public FeedbackController Controller { get; }

        public DictationController DictateController()
            => new(Turns.Wrap(Recorder, "Dictate"), Recognizer, () => true, _ => Task.FromResult<string?>(null), (_, _) => { }, listenOnFor: TimeSpan.Zero);

        /// <summary>Two taps, then waits for the writing out and the tidying. The tidying starts inside the first wait, so there are two.</summary>
        public async Task SpeakAsync()
        {
            Controller.Toggle();
            await Controller.WhenSettled;
            Controller.Toggle();
            await Controller.WhenSettled;
            await Controller.WhenSettled;
        }
    }
}
