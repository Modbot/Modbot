using Modbot.Companion.Ingest;

namespace Modbot.Companion.Chat;

/// <summary>Where one line of the chat panel is.</summary>
public enum ChatLineState
{
    /// <summary>The AI has not written anything yet; the panel shows its typing animation.</summary>
    Typing,

    /// <summary>The answer is arriving and is not finished.</summary>
    Streaming,

    /// <summary>Nothing more will change in this line.</summary>
    Done,

    /// <summary>The text is the one short line saying what went wrong.</summary>
    Error,
}

/// <summary>One line of the chat panel: what the moderator said, or what the AI answered.</summary>
public sealed record ChatLine(bool Mine, string Text, ChatLineState State);

/// <summary>
/// What the chat panel shows and the conversation behind it, with nothing in it that draws or that
/// knows about a headset or a desktop window, so any way of opening the panel can use it.
/// </summary>
/// <remarks>
/// <para><strong>The conversation.</strong> The first message starts one on the server, the way the
/// Chat page does, and the id it answers with is kept so a second dictation while the panel is open
/// goes into the same conversation. <see cref="Close"/> forgets it: the next message starts a new one.
/// The conversation is a normal one and stays in the Chat page's list.</para>
/// <para><strong>One at a time.</strong> A message sent while an answer is still arriving waits for it,
/// so the two do not both hang off the same earlier message. Its lines are on the panel at once, with
/// the typing animation.</para>
/// <para><strong>Threads.</strong> Safe to call from any thread. <see cref="Changed"/> is raised outside
/// the lock, on whichever thread caused it, so a caller that draws must hand it to its own.</para>
/// </remarks>
public sealed class ChatSession
{
    private readonly IChatClient _client;
    private readonly Func<ServerPairing?> _pairing;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly List<Entry> _entries = [];

    private CancellationTokenSource _cancel = new();
    private int _generation;
    private Guid? _conversationId;

    public ChatSession(IChatClient client, Func<ServerPairing?> pairing)
    {
        _client = client;
        _pairing = pairing;
    }

    /// <summary>Something on the panel changed. Raised on whichever thread caused it.</summary>
    public event Action? Changed;

    /// <summary>The lines, oldest first, as they stand now.</summary>
    public IReadOnlyList<ChatLine> Lines
    {
        get
        {
            lock (_lock)
                return [.. _entries.Select(e => new ChatLine(e.Mine, e.Text, e.State))];
        }
    }

    /// <summary>The conversation the next message goes into; null before the first answer and after <see cref="Close"/>.</summary>
    public Guid? ConversationId
    {
        get
        {
            lock (_lock)
                return _conversationId;
        }
    }

    /// <summary>An answer is still on its way.</summary>
    public bool Busy
    {
        get
        {
            lock (_lock)
                return _entries.Any(e => !e.Mine && e.State is ChatLineState.Typing or ChatLineState.Streaming);
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the panel, with the typing animation under it, sends it, and
    /// lets the answer grow in that line. Finishes when the answer has, or has failed, or the panel
    /// was closed.
    /// </summary>
    public async Task AskAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        Entry answer;
        int generation;
        CancellationToken token;

        lock (_lock)
        {
            generation = _generation;
            token = _cancel.Token;
            _entries.Add(new Entry(true, text, ChatLineState.Done));
            answer = new Entry(false, string.Empty, ChatLineState.Typing);
            _entries.Add(answer);
        }

        Changed?.Invoke();

        try
        {
            await _oneAtATime.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await SendAsync(text, answer, generation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The panel was closed while the answer was coming: nothing is left to show.
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Closes the panel's conversation: the lines go, an answer still arriving is dropped, and the next
    /// message starts a new conversation.
    /// </summary>
    public void Close()
    {
        CancellationTokenSource old;

        lock (_lock)
        {
            old = _cancel;
            _cancel = new CancellationTokenSource();
            _generation++;
            _entries.Clear();
            _conversationId = null;
        }

        old.Cancel();
        old.Dispose();
        Changed?.Invoke();
    }

    private async Task SendAsync(string text, Entry answer, int generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var pairing = _pairing();
        if (pairing is null)
        {
            Set(answer, generation, ChatProblems.Unreachable, ChatLineState.Error);
            return;
        }

        Guid? conversation;
        lock (_lock)
            conversation = _conversationId;

        var result = await _client
            .SendAsync(pairing, text, conversation, whole => Set(answer, generation, whole, ChatLineState.Streaming), token)
            .ConfigureAwait(false);

        lock (_lock)
        {
            if (generation != _generation)
                return;

            if (result.ConversationGone)
                _conversationId = null;
            else if (result.ConversationId is { } id)
                _conversationId = id;
        }

        if (result.Problem is { } problem)
            Set(answer, generation, problem, ChatLineState.Error);
        else
            Set(answer, generation, null, ChatLineState.Done);
    }

    /// <summary>Changes the line, unless the panel was closed since the message was sent. A null text keeps the words.</summary>
    private void Set(Entry entry, int generation, string? text, ChatLineState state)
    {
        lock (_lock)
        {
            if (generation != _generation)
                return;

            // An answer that arrives after the line was settled changes nothing.
            if (entry.State is ChatLineState.Done or ChatLineState.Error)
                return;

            if (text is not null)
                entry.Text = text;

            entry.State = state;
        }

        Changed?.Invoke();
    }

    private sealed class Entry(bool mine, string text, ChatLineState state)
    {
        public bool Mine { get; } = mine;

        public string Text { get; set; } = text;

        public ChatLineState State { get; set; } = state;
    }
}
