namespace Modbot.Core.Data.Entities;

/// <summary>
/// One reading of the group's member count and online member count, as VRChat reported them.
/// The table is <c>group_member_count</c>.
/// </summary>
/// <remarks>
/// <para>
/// A row per poll, not per change. The group-info sync reads the group about every five minutes
/// and writes a <c>GroupInfoChanged</c> fact only when something moved, which is right for a
/// history of changes and wrong for a chart: a chart of readings wants the readings, including the
/// ones that said the same thing, because the time of each one is part of what it shows. Nothing
/// asks this table "how often did the count change", so the objection to a fact per poll does not
/// apply here.
/// </para>
/// <para>
/// Small by construction: two integers and a time, at most 288 rows a day. Rows older than the
/// presence retention window are deleted when an operator has set one; with none set, like every
/// other store, nothing is deleted.
/// </para>
/// <para>
/// Readings from before this table existed were filled in from the facts when the table was
/// created: one reading per fact that carried a count, at the time the poll saw it.
/// </para>
/// </remarks>
public class GroupMemberCount
{
    public long Id { get; set; }

    /// <summary>The group, by VRChat's id. Carried through untouched (spec 3.1.1).</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>When the poll read the group.</summary>
    public DateTimeOffset CountedAt { get; set; }

    /// <summary>VRChat's <c>memberCount</c>.</summary>
    public int MemberCount { get; set; }

    /// <summary>VRChat's <c>onlineMemberCount</c>: members online in VRChat at the time, anywhere.</summary>
    public int OnlineMemberCount { get; set; }
}
