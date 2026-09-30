using Modbot.Core.Users;

namespace Modbot.Api.Features.Places;

/// <summary>
/// Turns the sessions a companion saw in one instance into "how many members, how many of each rank"
/// at every moment that changed.
/// </summary>
/// <remarks>
/// <para>
/// A session is one person present from <c>Started</c> until <c>Ended</c>
/// (<see cref="PresenceCounts"/>). Every start adds the person, every end takes them away, and a
/// point is written after each moment at which anything happened. The last point is always all
/// zeros: a session nobody saw the end of ends at the companion's last report, and so does every
/// other, which is where the lines stop.
/// </para>
/// <para>
/// Who is a member and who holds which rank are looked up once, as they stand today. Modbot keeps
/// only the current rank and the current membership per person, so a person who was a Visitor that
/// night and is a User now counts as a User for the whole night.
/// </para>
/// </remarks>
public static class PeoplePresentSeries
{
    /// <summary>
    /// The points, oldest first. Empty when there were no sessions.
    /// </summary>
    /// <param name="sessions">Who was present from when until when.</param>
    /// <param name="members">The people, of those seen, who are members of the managed group today.</param>
    /// <param name="ranks">The rank each person holds today; a person missing here, or held at null, has no rank read yet.</param>
    public static IReadOnlyList<PeoplePresentPoint> Build(
        IReadOnlyList<PresenceSession> sessions,
        IReadOnlySet<string> members,
        IReadOnlyDictionary<string, TrustRank?> ranks)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(ranks);

        if (sessions.Count == 0)
            return [];

        // An end at the same instant as a start is applied first, so a person seen to leave and
        // come back in one report is counted once, not twice, at that moment.
        var changes = new List<(DateTimeOffset At, int Order, string UserId, int Delta)>(sessions.Count * 2);
        foreach (var s in sessions)
        {
            changes.Add((s.Started, 1, s.UserId, +1));
            changes.Add((s.Ended, 0, s.UserId, -1));
        }

        changes.Sort((a, b) =>
        {
            var byTime = a.At.CompareTo(b.At);
            return byTime != 0 ? byTime : a.Order.CompareTo(b.Order);
        });

        var counts = new Counts();
        var points = new List<PeoplePresentPoint>();

        for (var i = 0; i < changes.Count; i++)
        {
            var (at, _, userId, delta) = changes[i];

            counts.Apply(delta, members.Contains(userId), ranks.GetValueOrDefault(userId));

            var last = i == changes.Count - 1 || changes[i + 1].At != at;
            if (last)
                points.Add(counts.At(at));
        }

        return points;
    }

    private sealed class Counts
    {
        private int _members;
        private readonly int[] _byRank = new int[Enum.GetValues<TrustRank>().Length];
        private int _unknown;

        public void Apply(int delta, bool member, TrustRank? rank)
        {
            if (member)
                _members += delta;

            if (rank is { } held && (int)held >= 0 && (int)held < _byRank.Length)
                _byRank[(int)held] += delta;
            else
                _unknown += delta;
        }

        public PeoplePresentPoint At(DateTimeOffset at) => new(
            at,
            _members,
            _byRank[(int)TrustRank.Visitor],
            _byRank[(int)TrustRank.NewUser],
            _byRank[(int)TrustRank.User],
            _byRank[(int)TrustRank.KnownUser],
            _byRank[(int)TrustRank.TrustedUser],
            _byRank[(int)TrustRank.Legend],
            _byRank[(int)TrustRank.Nuisance],
            _byRank[(int)TrustRank.VRChatTeam],
            _unknown);
    }
}
