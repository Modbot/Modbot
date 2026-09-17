using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

/// <summary>
/// The one place an instance's head count is set, so the instance row and its change log cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two sources, and which one wins.</strong> The instance's own page
/// (<c>GET /instances/{location}</c>) gives <c>n_users</c>, read about every thirty seconds by
/// <c>InstanceHeadCountSync</c>. The group's list (<c>GET /groups/{groupId}/instances</c>) gives
/// <c>memberCount</c> every ten seconds. The page is preferred: in the one probe made,
/// <c>memberCount</c> read 2 while the instance's page said 3 (research: vrchat-instance-findings.md
/// section 3), so the list's number may count group members only.
/// </para>
/// <para>
/// The list's number is used when the page has never been read, when the last read failed, or
/// when the last good read is older than <see cref="PageReadGoesStaleAfter"/> -- for example
/// because <c>instances.read</c> is cold-stopped. An instance is never shown without a count just
/// because its page could not be read.
/// </para>
/// </remarks>
public static class HeadCounts
{
    /// <summary>The head count came from the instance's own page.</summary>
    public const string FromPage = "page";

    /// <summary>The head count came from the group's instance list.</summary>
    public const string FromList = "list";

    /// <summary>
    /// How old a good page read may be before the group list's number takes over again. About four
    /// missed reads at the thirty-second rate.
    /// </summary>
    public static readonly TimeSpan PageReadGoesStaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>The count to show for an instance: the head count, or the list's number before there is one.</summary>
    public static int? Shown(VRChatInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.HeadCount ?? instance.LastUserCount;
    }

    /// <summary>Whether the group list's number should set the head count right now.</summary>
    public static bool ListMayUpdate(VRChatInstance instance, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return instance.HeadCountSource != FromPage
            || instance.PageReadAt is not { } read
            || now - read > PageReadGoesStaleAfter;
    }

    /// <summary>
    /// Sets an instance's head count, and writes a change-log row when the number actually changed.
    /// </summary>
    /// <returns>True when a row was written.</returns>
    public static bool Record(
        ModbotContext db,
        VRChatInstance instance,
        int headCount,
        string source,
        DateTimeOffset at,
        int? userCount = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(instance);

        instance.HeadCountSource = source;

        if (instance.PeakUserCount is null || headCount > instance.PeakUserCount)
            instance.PeakUserCount = headCount;

        if (instance.HeadCount == headCount)
            return false;

        instance.HeadCount = headCount;

        db.InstanceHeadCounts.Add(new InstanceHeadCount
        {
            InstanceId = instance.Id,
            CountedAt = at,
            HeadCount = headCount,
            UserCount = source == FromPage ? userCount : null,
            MemberCount = instance.LastUserCount,
            Source = source,
        });

        return true;
    }
}
