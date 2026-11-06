using Modbot.Companion.Chat;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Chat;

/// <summary>The chat panel's lines and conversation, with a made-up server.</summary>
public class ChatSessionTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public async Task AMessageShowsAtOnceWithTheTypingLineUnderItThenTheAnswerGrows()
    {
        var client = new FakeClient();
        var session = new ChatSession(client, () => Pairing);

        var asking = session.AskAsync("  who is here  ");

        Assert.Equal(
            [new ChatLine(true, "who is here", ChatLineState.Done), new ChatLine(false, "", ChatLineState.Typing)],
            session.Lines);
        Assert.True(session.Busy);

        await client.Started.Task;
        client.Say("Two ");
        Assert.Equal(new ChatLine(false, "Two ", ChatLineState.Streaming), session.Lines[1]);

        client.Say("Two people.");
        client.Finish(new ChatSendResult(null, client.Conversation));
        await asking;

        Assert.Equal(new ChatLine(false, "Two people.", ChatLineState.Done), session.Lines[1]);
        Assert.False(session.Busy);
    }

    [Fact]
    public async Task ASecondMessageWhileOpenGoesIntoTheSameConversation()
    {
        var client = new FakeClient { Immediate = new ChatSendResult(null, Guid.NewGuid()) };
        var session = new ChatSession(client, () => Pairing);

        await session.AskAsync("one");
        await session.AskAsync("two");

        Assert.Equal([null, client.Immediate!.ConversationId], client.ConversationsSent);
        Assert.Equal(4, session.Lines.Count);
    }

    [Fact]
    public async Task ClosingStartsAFreshConversationNextTime()
    {
        var client = new FakeClient { Immediate = new ChatSendResult(null, Guid.NewGuid()) };
        var session = new ChatSession(client, () => Pairing);

        await session.AskAsync("one");
        session.Close();

        Assert.Empty(session.Lines);
        Assert.Null(session.ConversationId);

        await session.AskAsync("two");

        Assert.Equal([null, null], client.ConversationsSent);
    }

    [Fact]
    public async Task ClosingWhileAnAnswerIsComingDropsTheAnswer()
    {
        var client = new FakeClient();
        var session = new ChatSession(client, () => Pairing);

        var asking = session.AskAsync("one");
        await client.Started.Task;

        session.Close();
        client.Say("late words");
        await asking;

        Assert.Empty(session.Lines);
        Assert.Null(session.ConversationId);
    }

    [Fact]
    public async Task AProblemReplacesTheAnswerWithOneLine()
    {
        var client = new FakeClient { Immediate = new ChatSendResult(ChatProblems.SpendLimit) };
        var session = new ChatSession(client, () => Pairing);

        await session.AskAsync("one");

        Assert.Equal(new ChatLine(false, ChatProblems.SpendLimit, ChatLineState.Error), session.Lines[1]);
    }

    [Fact]
    public async Task AConversationTheServerForgotIsNotNamedAgain()
    {
        var client = new FakeClient { Immediate = new ChatSendResult(null, Guid.NewGuid()) };
        var session = new ChatSession(client, () => Pairing);
        await session.AskAsync("one");

        client.Immediate = new ChatSendResult(ChatProblems.Failed, null, ConversationGone: true);
        await session.AskAsync("two");

        Assert.Null(session.ConversationId);
    }

    [Fact]
    public async Task NoPairingIsOneShortLine()
    {
        var session = new ChatSession(new FakeClient(), () => null);

        await session.AskAsync("one");

        Assert.Equal(new ChatLine(false, ChatProblems.Unreachable, ChatLineState.Error), session.Lines[1]);
    }

    [Fact]
    public async Task ANewMessageWaitsForTheAnswerBeforeItAndShowsAtOnce()
    {
        var client = new FakeClient();
        var session = new ChatSession(client, () => Pairing);

        var first = session.AskAsync("one");
        await client.Started.Task;
        var second = session.AskAsync("two");

        Assert.Equal(4, session.Lines.Count);
        Assert.Equal(1, client.Sends);

        // The second one is sent after the first has finished, into the conversation the first made.
        var made = Guid.NewGuid();
        client.Immediate = new ChatSendResult(null, made);
        client.Finish(new ChatSendResult(null, made));
        await first;
        await second;

        Assert.Equal(2, client.Sends);
        Assert.Equal(made, client.ConversationsSent[1]);
    }

    [Fact]
    public async Task ABlankMessageIsNotSent()
    {
        var client = new FakeClient();
        var session = new ChatSession(client, () => Pairing);

        await session.AskAsync("   ");

        Assert.Empty(session.Lines);
        Assert.Equal(0, client.Sends);
    }

    [Fact]
    public async Task TheAvailabilityFollowsTheServerAndKeepsItsLastAnswerWhenTheServerCannotBeAsked()
    {
        var client = new FakeClient { Available = true };
        ServerPairing? pairing = Pairing;
        var availability = new ChatAvailability(client, () => pairing);
        var changes = 0;
        availability.Changed += () => changes++;

        Assert.False(availability.Available);

        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(availability.Available);

        client.Available = null;
        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(availability.Available);

        client.Available = false;
        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(availability.Available);

        client.Available = true;
        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        pairing = null;
        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(availability.Available);

        Assert.Equal(4, changes);
    }

    private sealed class FakeClient : IChatClient
    {
        private readonly object _lock = new();
        private TaskCompletionSource<ChatSendResult>? _pending;
        private Action<string>? _onAnswer;

        public bool? Available { get; set; }

        /// <summary>When set, a send answers with this at once instead of waiting for <see cref="Finish"/>.</summary>
        public ChatSendResult? Immediate { get; set; }

        public Guid Conversation { get; } = Guid.NewGuid();

        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Sends { get; private set; }

        public List<Guid?> ConversationsSent { get; } = [];

        public Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken)
            => Task.FromResult(Available);

        public async Task<ChatSendResult> SendAsync(
            ServerPairing pairing, string text, Guid? conversationId, Action<string> onAnswer, CancellationToken cancellationToken)
        {
            Task<ChatSendResult> waiting;

            lock (_lock)
            {
                Sends++;
                ConversationsSent.Add(conversationId);

                if (Immediate is { } now)
                    return now;

                _pending = new TaskCompletionSource<ChatSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _onAnswer = onAnswer;
                waiting = _pending.Task;
                Started.TrySetResult();
            }

            await using var _ = cancellationToken.Register(() => _pending.TrySetCanceled(cancellationToken));
            return await waiting;
        }

        public void Say(string whole) => _onAnswer!(whole);

        public void Finish(ChatSendResult result) => _pending!.SetResult(result);
    }
}
