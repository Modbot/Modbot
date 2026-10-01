namespace Modbot.VRChat.Session;

/// <summary>
/// The friends ids VRChat sent with Modbot's own account the last time it signed in, held until the
/// calendar's invite loop writes them down (calendar auto-invite design §3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No extra call.</strong> The sign-in already reads the account; this keeps the
/// <c>friends</c> ids that come with it, which would otherwise be thrown away. Nothing asks VRChat
/// for them.
/// </para>
/// <para>
/// The list may not be complete, so it only ever adds friends; a person missing from it is not
/// marked as not a friend. Held in memory because the gate has no database: a restart before the
/// invite loop runs loses it, and the next sign-in brings it again.
/// </para>
/// </remarks>
public sealed class SignInFriends
{
    private readonly Lock _lock = new();
    private SignInFriendsSeen? _waiting;

    /// <summary>Keeps the ids from a sign-in, replacing any not yet written down.</summary>
    public void Remember(IEnumerable<string>? ids, DateTimeOffset at)
    {
        if (ids is null)
            return;

        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();

        lock (_lock)
            _waiting = new SignInFriendsSeen(list, at);
    }

    /// <summary>The ids waiting to be written down, once; null when there are none.</summary>
    public SignInFriendsSeen? Take()
    {
        lock (_lock)
        {
            var waiting = _waiting;
            _waiting = null;
            return waiting;
        }
    }
}

/// <summary>Friends ids from one sign-in, and when it was.</summary>
public sealed record SignInFriendsSeen(IReadOnlyList<string> Ids, DateTimeOffset At);
