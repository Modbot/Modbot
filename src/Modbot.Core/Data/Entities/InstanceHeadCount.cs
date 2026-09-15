namespace Modbot.Core.Data.Entities;

/// <summary>
/// One change in how many people a room holds. The table is <c>instance_head_count</c>.
/// </summary>
/// <remarks>
/// <para>
/// A row is written only when the head count changes, not on every read, so a room that sits at
/// twelve people all evening is one row, and the table answers "how full was it, and when" without
/// holding a reading every ten seconds.
/// </para>
/// <para>
/// Keyed on Modbot's own room id (<see cref="VRChatInstance.Id"/>), never VRChat's instance number,
/// which VRChat hands out again once a room closes. Rows go when their room does.
/// </para>
/// <para>
/// This covers rooms nobody from the team is standing in. The count comes from VRChat -- the
/// room's own page, or the group's list when the page cannot be read -- and needs no moderator's
/// client at all.
/// </para>
/// </remarks>
public class InstanceHeadCount
{
    public long Id { get; set; }

    /// <summary>The room, by Modbot's own id.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>When the new count was read.</summary>
    public DateTimeOffset CountedAt { get; set; }

    /// <summary>
    /// How many people were in the room. <c>n_users</c> from the room's own page when it could be
    /// read, otherwise the group list's <c>memberCount</c> -- <see cref="Source"/> says which.
    /// </summary>
    public int HeadCount { get; set; }

    /// <summary>
    /// The room page's <c>userCount</c>, kept beside <c>n_users</c> because the two disagreed in
    /// the one probe made (research: vrchat-instance-findings.md section 3). Null when the count
    /// came from the group's list.
    /// </summary>
    public int? UserCount { get; set; }

    /// <summary>The group list's <c>memberCount</c> at the time, when Modbot had one.</summary>
    public int? MemberCount { get; set; }

    /// <summary><c>room</c> when the head count is the room page's, <c>list</c> when it is the group list's.</summary>
    public string Source { get; set; } = string.Empty;
}
