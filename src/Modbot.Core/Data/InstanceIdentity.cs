using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

/// <summary>
/// Decides whether an instance Modbot is looking at is one it already knows or a new one.
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
    /// How soon an instance the group's list stopped carrying can come back and still be the same instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absence from the group's live list is how an instance is known to have ended, and it is the
    /// best signal there is -- but it rests on an assumption that has not been checked: that a
    /// instance with nobody in it drops off the list only when it has actually shut down. No empty
    /// group instance existed when the API was probed, so whether an empty instance stays listed is
    /// genuinely unknown (research: vrchat-instance-findings.md section 2.1).
    /// </para>
    /// <para>
    /// This window makes the question cost little either way. If the instance really had ended, an
    /// instance number handed out again within minutes is rare, and the price of being wrong is
    /// two short sessions recorded as one. If the instance had merely emptied, the row stays
    /// continuous, which is the truth. Being wrong in the other direction -- splitting every
    /// quiet stretch into a new instance -- would corrupt every figure about how long instances run.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan ReopensWithin = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Picks the open instance a sighting belongs to, or null when the sighting starts a new one.
    /// </summary>
    /// <param name="atLocation">
    /// The rows at this location worth considering: those still open, plus any closed recently
    /// enough to be reopened. Normally none or one; more than one only where an earlier close was
    /// missed, and then the most recently seen wins, because it is the only one that could still
    /// be running.
    /// </param>
    /// <param name="seenAt">When the sighting happened.</param>
    /// <returns>The instance to record against, or null to open a new one.</returns>
    public static VRChatInstance? Match(IEnumerable<VRChatInstance> atLocation, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(atLocation);

        VRChatInstance? best = null;

        foreach (var candidate in atLocation)
        {
            if (!IsStillTheSameInstance(candidate, seenAt))
                continue;

            if (best is null || candidate.LastSeenAt > best.LastSeenAt)
                best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Whether an instance the group's list closed has come back soon enough to be the same instance.
    /// </summary>
    /// <remarks>
    /// Only instances the list itself closed are eligible. An instance closed by the time rule has been
    /// quiet for three days and is not coming back; reopening one would undo the split the rule
    /// exists to make.
    /// </remarks>
    private static bool CanReopen(VRChatInstance candidate, DateTimeOffset seenAt) =>
        candidate.ClosedBy == "list"
        && candidate.ClosedAt is { } closedAt
        && seenAt >= closedAt
        && seenAt - closedAt < ReopensWithin;

    /// <summary>
    /// Whether an open row can still be the instance being seen now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An instance the group's live list has carried is governed by the list alone: while it is open it
    /// is open, however quiet it has been. Instances run all night with nobody from Modbot in them,
    /// and a moderator who leaves at midnight and returns at noon has not walked into a different
    /// instance just because Modbot stopped watching.
    /// </para>
    /// <para>
    /// Everywhere else there is no such signal, so time is all there is: a gap of
    /// <see cref="VRChatInstance.CountsAsNewAfter"/> means the number has almost certainly been
    /// handed out again.
    /// </para>
    /// <para>
    /// A sighting <em>earlier</em> than the row's last sighting is still the same instance. Reports
    /// arrive out of order -- a client that was offline sends its backlog when it reconnects --
    /// and treating a late-arriving older line as a new instance would split one evening in two.
    /// </para>
    /// </remarks>
    private static bool IsStillTheSameInstance(VRChatInstance candidate, DateTimeOffset seenAt)
    {
        if (candidate.ClosedAt is not null)
            return CanReopen(candidate, seenAt);

        if (candidate.SeenInGroupList)
            return true;

        if (seenAt <= candidate.LastSeenAt)
            return true;

        return seenAt - candidate.LastSeenAt < VRChatInstance.CountsAsNewAfter;
    }
}
