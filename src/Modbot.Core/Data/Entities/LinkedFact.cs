namespace Modbot.Core.Data.Entities;

/// <summary>
/// One fact that belongs to the same decision as another, and which of the two is the main one.
/// </summary>
/// <remarks>
/// <para>
/// Some single decisions leave more than one fact behind. A moderator who bans somebody standing
/// in one of the group's instances gets a ban entry <em>and</em> an instance-kick entry from
/// VRChat, seconds apart; a ban pressed in Modbot leaves Modbot's own record of who decided it
/// beside VRChat's record that it happened; a Discord ban arrives as a ban and a leave. One
/// decision, several facts. Anything counting actions has to count it once.
/// </para>
/// <para>
/// <strong>This is a derived table, not part of the fact.</strong> Facts are never mutated
/// (see <see cref="ModbotEvent"/>), and a link is something Modbot worked out rather than
/// something a source said, so it is written beside the facts and can be thrown away and rebuilt
/// from them. That is also what makes out-of-order arrival easy: whichever of the pair lands
/// second writes the row, and neither fact is touched.
/// </para>
/// <para>
/// Only the <em>follower</em> gets a row. The main fact is the one every count already counts, so
/// "is this fact a follower" is one lookup and "what else happened in this decision" is one more.
/// </para>
/// </remarks>
public class LinkedFact
{
    /// <summary>The follower: the fact that must not be counted a second time.</summary>
    public long FactId { get; set; }

    /// <summary>
    /// The follower's own <c>occurred_at</c>. Kept here because <c>modbot_event</c> is partitioned
    /// by it, and a join that does not name it reads every partition.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The fact that counts for this decision.</summary>
    public long MainFactId { get; set; }

    /// <summary>The main fact's <c>occurred_at</c>, for the same reason as <see cref="OccurredAt"/>.</summary>
    public DateTimeOffset MainOccurredAt { get; set; }

    /// <summary>When Modbot worked the link out, from <c>IModbotClock</c>.</summary>
    public DateTimeOffset LinkedAt { get; set; }
}
