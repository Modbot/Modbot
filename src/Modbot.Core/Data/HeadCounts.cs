using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

/// <summary>
/// The one place an instance's head count is set, so the instance row and its change log cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two sources, and which one wins.</strong> The instance's own page
/// (<c>GET /instances/{location}</c>) is read about every thirty seconds by
/// <c>InstanceHeadCountSync</c>. The group's list (<c>GET /groups/{groupId}/instances</c>) gives
/// <c>memberCount</c> every ten seconds. The page is preferred: in the first probe made,
/// <c>memberCount</c> read 2 while the instance's page said 3 (research: vrchat-instance-findings.md
/// section 3), so the list's number may count group members only.
/// </para>
/// <para>
/// <strong>Which of the page's two numbers.</strong> The page carries <c>userCount</c> and
/// <c>n_users</c>, and <c>userCount</c> is the head count (<see cref="FromPageRead"/>). The first probe
/// had them 2 and 3 and <c>n_users</c> was taken, on that one reading. On 2026-09-26 a live group
/// showed otherwise: a club in a world that holds 80 read <c>n_users</c> 80 while <c>userCount</c> said
/// 51, and the Companion App saw about 52 in an instance that was not full. Over 281 page readings
/// that evening the two differed 229 times, <c>n_users</c> up to about thirty higher while it was
/// busy, and they came together again as it emptied. <c>n_users</c> is kept beside each reading, and
/// used only when a body has no <c>userCount</c>; a count taken that way is unsure, and screens show
/// it as "80?".
/// </para>
/// <para>
/// The list's number is used when the page has never been read, when the last read failed, or
/// when the last good read is older than <see cref="PageReadGoesStaleAfter"/> -- for example
/// because <c>instances.read</c> is cold-stopped. An instance is never shown without a count just
/// because its page could not be read. A count from the list is never unsure: it means group members,
/// which is a different thing, not a doubtful head count.
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

    /// <summary>Whether <see cref="Shown"/> is an unsure count, to be shown as "80?".</summary>
    public static bool ShownUnsure(VRChatInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.HeadCount is not null && instance.HeadCountUnsure;
    }

    /// <summary>
    /// The head count one read of an instance's page gives: <c>userCount</c>, or <c>n_users</c> when the
    /// body had no <c>userCount</c>, in which case it is unsure.
    /// </summary>
    public static (int HeadCount, bool Unsure) FromPageRead(int nUsers, int? userCount)
        => userCount is { } people ? (people, false) : (nUsers, true);

    /// <summary>Whether the group list's number should set the head count right now.</summary>
    public static bool ListMayUpdate(VRChatInstance instance, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return instance.HeadCountSource != FromPage
            || instance.PageReadAt is not { } read
            || now - read > PageReadGoesStaleAfter;
    }

    /// <summary>
    /// Raises an instance's peak to <paramref name="count"/> when it is higher, and keeps whether the
    /// peak is unsure.
    /// </summary>
    /// <remarks>
    /// A sure count that only equals an unsure peak confirms it. An unsure count that only equals a
    /// sure peak does not make it unsure.
    /// </remarks>
    public static void RaisePeak(VRChatInstance instance, int count, bool unsure)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.PeakUserCount is not { } peak || count > peak)
        {
            instance.PeakUserCount = count;
            instance.PeakUnsure = unsure;
        }
        else if (count == peak && !unsure)
        {
            instance.PeakUnsure = false;
        }
    }

    /// <summary>
    /// Sets an instance's head count, and writes a change-log row when the number, or whether it is
    /// unsure, actually changed.
    /// </summary>
    /// <param name="headCount">For a page read, what <see cref="FromPageRead"/> gave.</param>
    /// <param name="userCount">The page's <c>userCount</c>; null for the list, and for a page body without one.</param>
    /// <param name="nUsers">The page's <c>n_users</c>; null for the list.</param>
    /// <returns>True when a row was written.</returns>
    /// <remarks>
    /// A page reading is unsure exactly when it has no <paramref name="userCount"/>, so the row needs no
    /// flag of its own: <c>source = 'page' AND user_count IS NULL</c> is an unsure reading.
    /// </remarks>
    public static bool Record(
        ModbotContext db,
        VRChatInstance instance,
        int headCount,
        string source,
        DateTimeOffset at,
        int? userCount = null,
        int? nUsers = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(instance);

        var fromPage = source == FromPage;
        var unsure = fromPage && userCount is null;

        instance.HeadCountSource = source;

        RaisePeak(instance, headCount, unsure);

        if (instance.HeadCount == headCount && instance.HeadCountUnsure == unsure)
            return false;

        instance.HeadCount = headCount;
        instance.HeadCountUnsure = unsure;

        db.InstanceHeadCounts.Add(new InstanceHeadCount
        {
            InstanceId = instance.Id,
            CountedAt = at,
            HeadCount = headCount,
            UserCount = fromPage ? userCount : null,
            NUsers = fromPage ? nUsers : null,
            MemberCount = instance.LastUserCount,
            Source = source,
        });

        return true;
    }
}
