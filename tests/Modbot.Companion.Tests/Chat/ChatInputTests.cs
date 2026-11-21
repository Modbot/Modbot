using Modbot.Companion.Chat;
using Modbot.Companion.Dictation;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Chat;

/// <summary>A pop-up chat's text box and microphone talk to that chat's conversation and no other.</summary>
public class ChatInputTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public async Task TypedWordsGoOnlyIntoTheirOwnChat()
    {
        var (first, firstClient) = Chat();
        var (second, secondClient) = Chat();

        await first.Input.SendAsync("a");

        Assert.Equal(["a"], firstClient.Texts);
        Assert.Empty(secondClient.Texts);
        Assert.NotEmpty(first.Session.Lines);
        Assert.Empty(second.Session.Lines);
    }

    [Fact]
    public async Task WordsFromTheMicrophoneGoOnlyIntoItsOwnChat()
    {
        var (first, firstClient) = Chat("from the first");
        var (second, secondClient) = Chat("from the second");

        await Say(second);

        Assert.Equal(["from the second"], secondClient.Texts);
        Assert.Empty(firstClient.Texts);
        Assert.Empty(first.Session.Lines);

        await Say(first);

        Assert.Equal(["from the first"], firstClient.Texts);
        Assert.Equal(["from the second"], secondClient.Texts);
    }

    [Fact]
    public async Task ASecondThingSaidInTheSameChatContinuesThatConversation()
    {
        var (chat, client) = Chat("one");

        await Say(chat);
        await Say(chat);

        Assert.Equal([null, client.Conversation], client.ConversationsSent);
    }

    [Fact]
    public async Task TheMicrophoneButtonStopsSendingWhenItsOwnAnswerBegins()
    {
        var (chat, _) = Chat("hello");

        await Say(chat);

        Assert.Equal(DictationState.Idle, chat.Input.Dictation.State);
    }

    [Fact]
    public async Task WhileOneChatListensAnotherOnesMicrophoneButtonSaysSoAndDoesNotOpenIt()
    {
        var recorder = new Recorder();
        var turns = new MicrophoneTurns();
        var first = Chat(recorder: turns.Wrap(recorder, "Chat 1")).Chat;
        var second = Chat(recorder: turns.Wrap(recorder, "Chat 2")).Chat;

        first.Input.Dictation.Toggle();
        await first.Input.Dictation.WhenSettled;
        recorder.RaiseFlowing();
        second.Input.Dictation.Toggle();
        await second.Input.Dictation.WhenSettled;

        Assert.Equal(DictationState.Listening, first.Input.Dictation.State);
        Assert.Equal(DictationState.Idle, second.Input.Dictation.State);
        Assert.Contains("is listening", second.Input.Dictation.Problem);
        Assert.Equal(DictationController.CouldNotListen, second.Input.Dictation.Notice);
        Assert.Equal(1, recorder.Starts);
    }

    private static async Task Say(OneChat chat)
    {
        chat.Input.Dictation.Toggle();
        await chat.Input.Dictation.WhenSettled;
        chat.Input.Dictation.Toggle();
        await chat.Input.Dictation.WhenSettled;
        await chat.Asking();
    }

    private static (OneChat Chat, Client Client) Chat(string says = "", IDictationRecorder? recorder = null)
    {
        var client = new Client();
        var session = new ChatSession(client, () => Pairing);
        var controller = new DictationController(
            recorder ?? new Recorder(),
            new Recognizer(says),
            () => true,
            _ => Task.FromResult<string?>(null),
            (_, _) => { },
            listenOnFor: TimeSpan.Zero);

        return (new OneChat(new ChatInput(controller, session), session, client), client);
    }

    private sealed record OneChat(ChatInput Input, ChatSession Session, Client Client)
    {
        /// <summary>Lets what a microphone sent finish: the client answers at once, so one yield is enough.</summary>
        public Task Asking() => Task.Delay(50);
    }

    private sealed class Recorder : IDictationRecorder
    {
        private static readonly float[] Speech = Enumerable.Repeat(0.3f, 16_000).ToArray();

        public int Starts { get; private set; }

        public float Level => 0;

        public event Action? LimitReached
        {
            add { }
            remove { }
        }

        public event Action? Flowing;

        public void RaiseFlowing() => Flowing?.Invoke();

        public string? Start()
        {
            Starts++;
            return null;
        }

        public float[] Stop() => Speech;
    }

    private sealed class Recognizer(string says) : IDictationRecognizer
    {
        public string Recognize(float[] samples) => says;
    }

    private sealed class Client : IChatClient
    {
        public Guid Conversation { get; } = Guid.NewGuid();

        public List<string> Texts { get; } = [];

        public List<Guid?> ConversationsSent { get; } = [];

        public Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken)
            => Task.FromResult<bool?>(true);

        public Task<ChatSendResult> SendAsync(
            ServerPairing pairing, string text, Guid? conversationId, Action<string> onAnswer, CancellationToken cancellationToken)
        {
            Texts.Add(text);
            ConversationsSent.Add(conversationId);
            onAnswer("An answer.");

            return Task.FromResult(new ChatSendResult(null, Conversation));
        }
    }
}
