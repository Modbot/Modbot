namespace Modbot.Companion.Chat;

/// <summary>One open pop-up chat: its number, and the conversation it holds.</summary>
public sealed class ChatPopup
{
    internal ChatPopup(int number, ChatSession session)
    {
        Number = number;
        Session = session;
    }

    /// <summary>Which pop-up this is, from 1: the lowest number not in use when it opened.</summary>
    public int Number { get; }

    /// <summary>This pop-up's own conversation. No other pop-up shares it.</summary>
    public ChatSession Session { get; }
}

/// <summary>
/// The pop-up chats that are open. Each dictation finished on the overlay opens a new one with a new
/// conversation of its own; closing one ends only its conversation. There is no limit.
/// </summary>
/// <remarks>
/// <para><strong>Numbers.</strong> A new pop-up takes the lowest number no open pop-up has, so a number
/// freed by closing one is used again. The number picks the pop-up's key in SteamVR and where it is
/// first placed (<see cref="ChatCascade"/>).</para>
/// <para><strong>Threads.</strong> Safe to call from any thread. <see cref="Closed"/> is raised outside
/// the lock, on whichever thread closed the pop-up.</para>
/// </remarks>
public sealed class ChatPopups
{
    private readonly Func<ChatSession> _makeSession;
    private readonly object _lock = new();
    private readonly SortedDictionary<int, ChatPopup> _open = [];

    /// <param name="makeSession">Makes the conversation for a new pop-up: a new one every call.</param>
    public ChatPopups(Func<ChatSession> makeSession)
    {
        ArgumentNullException.ThrowIfNull(makeSession);

        _makeSession = makeSession;
    }

    /// <summary>A pop-up was closed and its conversation ended.</summary>
    public event Action<ChatPopup>? Closed;

    /// <summary>The open pop-ups, lowest number first.</summary>
    public IReadOnlyList<ChatPopup> All
    {
        get
        {
            lock (_lock)
                return [.. _open.Values];
        }
    }

    /// <summary>Opens a new pop-up with a new conversation, and says which number it took.</summary>
    public ChatPopup Open()
    {
        var session = _makeSession();

        lock (_lock)
        {
            var number = 1;
            while (_open.ContainsKey(number))
                number++;

            var popup = new ChatPopup(number, session);
            _open.Add(number, popup);

            return popup;
        }
    }

    /// <summary>The open pop-up with this number, or null.</summary>
    public ChatPopup? Find(int number)
    {
        lock (_lock)
            return _open.GetValueOrDefault(number);
    }

    /// <summary>Closes one pop-up and ends its conversation, and no other. False when it was not open.</summary>
    public bool Close(int number)
    {
        ChatPopup? popup;

        lock (_lock)
        {
            if (!_open.Remove(number, out popup))
                return false;
        }

        popup.Session.Close();
        Closed?.Invoke(popup);

        return true;
    }

    /// <summary>Closes every pop-up.</summary>
    public void CloseAll()
    {
        foreach (var popup in All)
            Close(popup.Number);
    }
}
