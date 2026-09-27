namespace Modbot.Core.Data.Entities;

/// <summary>
/// One change in how many people an instance holds. The table is <c>instance_head_count</c>.
/// </summary>
/// <remarks>
/// <para>
/// A row is written only when the head count changes, not on every read, so an instance that sits at
/// twelve people all evening is one row, and the table answers "how full was it, and when" without
/// holding a reading every ten seconds.
/// </para>
/// <para>
/// Keyed on Modbot's own instance id (<see cref="VRChatInstance.Id"/>), never VRChat's instance number,
/// which VRChat hands out again once an instance closes. Rows go when their instance does.
/// </para>
/// <para>
/// This covers instances nobody from the team is standing in. The count comes from VRChat -- the
/// instance's own page, or the group's list when the page cannot be read -- and needs no moderator's
/// client at all.
/// </para>
/// </remarks>
public class InstanceHeadCount
{
    public long Id { get; set; }

    /// <summary>The instance, by Modbot's own id.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>When the new count was read.</summary>
    public DateTimeOffset CountedAt { get; set; }

    /// <summary>
    /// How many people were in the instance. From the instance's own page when it could be read --
    /// its <c>userCount</c>, or <c>n_users</c> when the body had no <c>userCount</c> -- otherwise the
    /// group list's <c>memberCount</c>. <see cref="Source"/> says which read it came from.
    /// </summary>
    /// <remarks>
    /// A page reading with no <see cref="UserCount"/> took its number from <c>n_users</c>, and is
    /// shown as unconfirmed ("80?"): <c>n_users</c> ran up to about thirty above the real count on a
    /// busy club (<see cref="HeadCounts"/>).
    /// </remarks>
    public int HeadCount { get; set; }

    /// <summary>
    /// The instance page's <c>userCount</c>: how many people are in it, and the head count whenever
    /// the body carried it. Null when the reading came from the group's list, or when the page's body
    /// had none and the head count fell back to <see cref="NUsers"/>.
    /// </summary>
    public int? UserCount { get; set; }

    /// <summary>
    /// The instance page's <c>n_users</c>, kept beside <see cref="UserCount"/>. Higher than the real
    /// count while an instance is busy, so it is the head count only when <c>userCount</c> is missing.
    /// Null when the reading came from the group's list, and on readings stored before it was kept.
    /// </summary>
    public int? NUsers { get; set; }

    /// <summary>The group list's <c>memberCount</c> at the time, when Modbot had one.</summary>
    public int? MemberCount { get; set; }

    /// <summary><c>page</c> when the head count is the instance page's, <c>list</c> when it is the group list's.</summary>
    public string Source { get; set; } = string.Empty;
}
