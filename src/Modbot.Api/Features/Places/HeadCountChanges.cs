using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Places;

/// <summary>
/// Says of each head count reading whether it went up, or down because somebody was kicked, or
/// down because somebody left.
/// </summary>
/// <remarks>
/// <para>
/// A head count is a number read on a poll, and a kick is a fact from the group's audit log, so
/// "the kick that made this drop" is a matter of timing. A reading is stored only when the count
/// changed, and the poll that stored it ran about <see cref="Poll"/> after the one before it, so a
/// drop stored at T happened within one poll before T -- not anywhere since the previous stored
/// reading, which in a quiet instance can be hours earlier, and an old kick in those hours has
/// nothing to do with a drop tonight. Both clocks are loose -- the reading is stamped when Modbot
/// read the page, the kick when VRChat wrote its log, and the page can lag the instance by a poll
/// -- so a kick is matched to a drop when it fell within <see cref="Slack"/> on either side of
/// that window: [T − Poll − Slack, T + Slack], or from the previous reading less the slack when
/// that is later. A kick further off than that is more likely another drop's.
/// </para>
/// <para>
/// One kick colours one drop. Two drops a few seconds apart have windows that overlap by the
/// slack, and without that rule one kick between them would paint both. Drops are matched oldest
/// first, each taking the earliest kick still unclaimed in its window.
/// </para>
/// <para>
/// A reading is unsure when the page had no <c>userCount</c> and <c>n_users</c> stood in, which runs
/// up to about thirty above the real count. A change between an unsure reading and a sure one is
/// the number's source changing, not people arriving or leaving, so it is no change at all: null,
/// drawn as a held stretch.
/// </para>
/// </remarks>
public static class HeadCountChanges
{
    /// <summary>How long a drop can have been in the making before the poll that stored it.</summary>
    public static readonly TimeSpan Poll = InstanceHeadCountSync.ReadEvery;

    /// <summary>How far a kick may sit outside a drop's window and still be its cause.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The readings with <see cref="HeadCountPoint.Change"/> filled in.
    /// </summary>
    /// <param name="readings">Oldest first.</param>
    /// <param name="kicks">When each kick in this instance happened, in any order.</param>
    public static IReadOnlyList<HeadCountPoint> Classify(
        IReadOnlyList<HeadCountPoint> readings,
        IReadOnlyList<DateTimeOffset> kicks)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(kicks);

        if (readings.Count == 0)
            return readings;

        var unclaimed = kicks.Order().ToList();
        var result = new List<HeadCountPoint>(readings.Count) { readings[0] with { Change = null } };

        for (var i = 1; i < readings.Count; i++)
        {
            var previous = readings[i - 1];
            var reading = readings[i];

            string? change = null;

            if (reading.Unsure != previous.Unsure)
            {
                change = null;
            }
            else if (reading.People > previous.People)
            {
                change = HeadCountChange.Up;
            }
            else if (reading.People < previous.People)
            {
                var since = reading.At - Poll;
                if (previous.At > since)
                    since = previous.At;

                change = ClaimKick(unclaimed, since - Slack, reading.At + Slack)
                    ? HeadCountChange.Kick
                    : HeadCountChange.Left;
            }

            result.Add(reading with { Change = change });
        }

        return result;
    }

    /// <summary>Takes the earliest kick in [<paramref name="from"/>, <paramref name="to"/>], if there is one.</summary>
    private static bool ClaimKick(List<DateTimeOffset> unclaimed, DateTimeOffset from, DateTimeOffset to)
    {
        for (var i = 0; i < unclaimed.Count; i++)
        {
            if (unclaimed[i] > to)
                return false;

            if (unclaimed[i] >= from)
            {
                unclaimed.RemoveAt(i);
                return true;
            }
        }

        return false;
    }
}
