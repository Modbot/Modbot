using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

/// <summary>
/// Decides whether a room Modbot is looking at is one it already knows or a new one.
/// </summary>
/// <remarks>
/// <para>
/// This is pure: given the open rows at a location and the moment of the sighting, it says which
/// row to use, or that a new one is needed. Everything that talks to a database or a clock lives
/// in the caller, so the rule itself can be read and tested on its own -- which matters, because
/// getting it wrong does not throw. It quietly produces sessions that never happened.
/// </para>
/// <para>The rule is stated in full on <see cref="VRChatInstance"/>.</para>
/// </remarks>
public static class InstanceIdentity
{
    /// <summary>
    /// Picks the open room a sighting belongs to, or null when the sighting starts a new one.
    /// </summary>
    /// <param name="openAtLocation">
    /// Every row at this location that has no <c>ClosedAt</c>. Normally none or one; more than one
    /// only where an earlier close was missed, and then the most recently seen wins, because it is
    /// the only one that could still be running.
    /// </param>
    /// <param name="seenAt">When the sighting happened.</param>
    /// <returns>The room to record against, or null to open a new one.</returns>
    public static VRChatInstance? Match(IEnumerable<VRChatInstance> openAtLocation, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(openAtLocation);

        VRChatInstance? best = null;

        foreach (var candidate in openAtLocation)
        {
            if (candidate.ClosedAt is not null)
                continue;

            if (!IsStillTheSameRoom(candidate, seenAt))
                continue;

            if (best is null || candidate.LastSeenAt > best.LastSeenAt)
                best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Whether an open row can still be the room being seen now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A room the group's live list has carried is governed by the list alone: while it is open it
    /// is open, however quiet it has been. Rooms run all night with nobody from Modbot in them,
    /// and a moderator who leaves at midnight and returns at noon has not walked into a different
    /// room just because Modbot stopped watching.
    /// </para>
    /// <para>
    /// Everywhere else there is no such signal, so time is all there is: a gap of
    /// <see cref="VRChatInstance.CountsAsNewAfter"/> means the number has almost certainly been
    /// handed out again.
    /// </para>
    /// <para>
    /// A sighting <em>earlier</em> than the row's last sighting is still the same room. Reports
    /// arrive out of order -- a client that was offline sends its backlog when it reconnects --
    /// and treating a late-arriving older line as a new room would split one evening in two.
    /// </para>
    /// </remarks>
    private static bool IsStillTheSameRoom(VRChatInstance candidate, DateTimeOffset seenAt)
    {
        if (candidate.SeenInGroupList)
            return true;

        if (seenAt <= candidate.LastSeenAt)
            return true;

        return seenAt - candidate.LastSeenAt < VRChatInstance.CountsAsNewAfter;
    }
}
