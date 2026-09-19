namespace Modbot.Companion.LogReading;

/// <summary>
/// A line shape from VRChat's log that Modbot recognises.
/// </summary>
/// <remarks>
/// This is the complete list — anything not represented here is read past and forgotten. Nothing in
/// this file is what gets transmitted: these are the raw observations, and only some of them become
/// a <c>CompanionEvent</c> once the phantom-burst rules have been applied.
/// </remarks>
public abstract record VRChatLogEvent(DateTime Timestamp);

/// <summary>
/// <c>OnPlayerJoined &lt;displayName&gt; (&lt;userId&gt;)</c>. Carries a real identity, but on its
/// own says nothing about whether the person actually just arrived — see
/// <c>InstanceSessionTracker</c>.
/// </summary>
public sealed record PlayerJoinedEvent(DateTime Timestamp, string DisplayName, string UserId)
    : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>OnPlayerLeft &lt;displayName&gt; (&lt;userId&gt;)</c>. Genuine before <c>OnLeftRoom</c>,
/// phantom after it.
/// </summary>
public sealed record PlayerLeftEvent(DateTime Timestamp, string DisplayName, string UserId)
    : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>OnLeftRoom</c> — <strong>the local user</strong> left the instance. This is the marker that
/// opens the phantom leave burst. Not to be confused with <see cref="RemotePlayerLeftRoomEvent"/>.
/// </summary>
public sealed record LocalPlayerLeftRoomEvent(DateTime Timestamp) : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>OnPlayerLeftRoom</c> — <strong>a remote player</strong> left. One character away from
/// <see cref="LocalPlayerLeftRoomEvent"/> and the opposite meaning; confusing the two inverts the
/// entire burst rule.
/// </summary>
public sealed record RemotePlayerLeftRoomEvent(DateTime Timestamp) : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>OnPlayerEnteredRoom</c> — a remote player is arriving. Carries no identity; kept only as a
/// hint that a genuine arrival is imminent.
/// </summary>
public sealed record RemotePlayerEnteredRoomEvent(DateTime Timestamp) : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>Destination set: &lt;location&gt;</c> — the local user has committed to an instance. The
/// first place the full location string appears, and therefore the first point at which routing can
/// be decided.
/// </summary>
public sealed record DestinationSetEvent(DateTime Timestamp, string Location)
    : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>Joining &lt;location&gt;</c> — the local user is entering the instance now. This is what
/// opens the phantom join burst.
/// </summary>
public sealed record JoiningInstanceEvent(DateTime Timestamp, string Location)
    : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>Joining or Creating Room: &lt;world name&gt;</c> — the readable name of the world, written
/// a moment after the <c>Joining</c> line that carries its id.
/// </summary>
/// <remarks>
/// <para>Read since 2026-09-19, for one reason: a saved clip is named after the world it was
/// recorded in, and <c>wrld_4cf554b4-430c-…</c> is not a name a moderator can pick out of a folder
/// an hour later. "The Black Cat" is.</para>
/// <para>It is a <strong>display name</strong> and nothing else — it is not an id, it does not
/// identify the instance, and two different worlds may share one. So it is never used to decide
/// anything; the world id stays the identity, and this is only ever shown or written into a file
/// name.</para>
/// <para>It is also somebody's text. A world name is whatever its author typed, so it may carry
/// anything a person can type, and whoever puts it on a screen or into a file name is the one that
/// has to make it safe.</para>
/// </remarks>
public sealed record WorldNameEvent(DateTime Timestamp, string WorldName)
    : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>Initialized PlayerAPI "&lt;name&gt;" is local</c> — the only line that says which display
/// name belongs to the moderator running Modbot. Matching it against the join burst is what yields
/// the local user id, which is the burst's terminator.
/// </summary>
public sealed record LocalPlayerIdentifiedEvent(DateTime Timestamp, string DisplayName)
    : VRChatLogEvent(Timestamp);

/// <summary>
/// <c>Switching &lt;displayName&gt; to avatar &lt;avatarName&gt;</c>.
/// </summary>
/// <remarks>
/// The log carries an avatar <em>display name</em> and never an <c>avtr_…</c> id — VRChat withholds
/// those from clients on purpose, to frustrate avatar ripping. Research doc section 4.
/// </remarks>
public sealed record AvatarSwitchedEvent(DateTime Timestamp, string Payload)
    : VRChatLogEvent(Timestamp)
{
    private const string Separator = " to avatar ";

    /// <summary>
    /// Every way this line could be read, longest display name first.
    /// </summary>
    /// <remarks>
    /// Both halves are attacker-controlled text. A display name may contain <c>" to avatar "</c>,
    /// in which case the <em>last</em> separator is the right one; an avatar name may contain it
    /// too, in which case the <em>first</em> is. No single choice is safe, so the caller resolves
    /// the ambiguity against the roster it already has — a reading that names somebody actually
    /// present beats one that does not. Readings that name nobody present produce no event at all.
    /// </remarks>
    public IReadOnlyList<(string DisplayName, string AvatarName)> CandidateSplits()
    {
        var splits = new List<(string, string)>();

        for (var at = Payload.LastIndexOf(Separator, StringComparison.Ordinal);
             at >= 0;
             at = Payload.LastIndexOf(Separator, at - 1, at, StringComparison.Ordinal))
        {
            splits.Add((Payload[..at], Payload[(at + Separator.Length)..]));
            if (at == 0)
                break;
        }

        return splits;
    }
}
