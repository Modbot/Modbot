namespace Modbot.Client.Instances;

/// <summary>
/// What kind of thing was observed, which is also a statement about how precisely it is known.
/// </summary>
public enum PresenceKind
{
    /// <summary>
    /// Somebody arrived while the moderator was already watching. <c>OccurredAt</c> is exact.
    /// </summary>
    Joined,

    /// <summary>
    /// Somebody was already in the instance when the moderator arrived. They are here <em>at</em>
    /// this time; they arrived at some unknown, earlier time. This distinction is the entire
    /// defence against phantom bursts turning one moderator walking into a room into forty fake
    /// arrivals.
    /// </summary>
    PresenceObserved,

    /// <summary>Somebody left while the moderator was still watching. Exact.</summary>
    Left,

    /// <summary>Somebody changed avatar while the moderator was watching. Exact.</summary>
    AvatarChanged,

    /// <summary>
    /// VRChat's log stopped growing while the moderator was in this instance, so this client can
    /// no longer see who is there. The subject is the moderator; the time is the last line the log
    /// wrote. Sent once per stop, never repeated.
    /// </summary>
    /// <remarks>
    /// The ordinary way a session ends is that the log simply stops (research note §7): VRChat is
    /// closed, crashes, or the machine sleeps, and no leave of any kind is written. Without this the
    /// server would go on believing a moderator was watching a room they had long since stopped
    /// seeing, and would show everyone they last saw as still being there.
    /// </remarks>
    LogStopped,
}

/// <summary>
/// One thing the client saw happen, after the phantom-burst rules have been applied but before
/// routing decides whether any server is entitled to hear about it.
/// </summary>
/// <param name="OccurredAtLocal">
/// VRChat's own timestamp: the moderator's local wall clock, with no offset. Converting it to an
/// instant and then to server time happens later, in one place.
/// </param>
/// <param name="DisplayName">
/// Captured opportunistically. Identity is the id; the name is history — it is what lets a
/// moderator searching for a name somebody used six months ago find them. Never used as identity,
/// because names are mutable and collide.
/// </param>
public sealed record ObservedPresence(
    PresenceKind Kind,
    DateTime OccurredAtLocal,
    string SubjectId,
    string? DisplayName,
    InstanceLocation Instance,
    string? AvatarName = null);
