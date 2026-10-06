using Modbot.Core.Time;

namespace Modbot.Companion.Overlay;

/// <summary>Somebody who has just left the instance, and when the panel noticed.</summary>
/// <param name="Member">Their row as it last was while they were here.</param>
/// <param name="LeftAt">When the panel saw them gone, on the app's own clock.</param>
public sealed record RecentLeaver(RosterMember Member, DateTimeOffset LeftAt)
{
    /// <summary>The seconds left before the row goes, counted up to a whole second and never below one while it shows.</summary>
    public int SecondsLeft(DateTimeOffset now)
    {
        var left = RecentLeavers.Stay - (now - LeftAt);
        return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalSeconds);
    }
}

/// <summary>
/// The people who left the moderator's instance in the last minute, so their rows can stay on the
/// Instance list, greyed, for a little while.
/// </summary>
/// <remarks>
/// <para><strong>Why it exists.</strong> A roster row that vanishes the moment somebody leaves
/// gives a moderator no time to tap it: the person they wanted to look at is gone before the hand
/// arrives. Keeping the row for <see cref="Stay"/> lets them open the person's card anyway.</para>
/// <para><strong>How somebody counts as having left.</strong> Each roster read for the instance is
/// compared with the one before it. Whoever was on the earlier one and is not on the later one has
/// left, as of now. The first read for an instance only sets what to compare with, so walking in
/// never shows the whole room as having just left. Somebody who is back on a later read is
/// present again and gets their ordinary row; they never appear twice.</para>
/// <para><strong>Time.</strong> Only <see cref="IModbotClock"/>, so a test can move it and a PC with
/// a wrong clock is no different from one with a right one.</para>
/// <para><strong>Nothing here is sent or asked for.</strong> It is a copy of rows the server
/// already told this client, held in memory only, and it goes when the moderator walks into
/// another instance or a pairing is removed.</para>
/// <para>Safe to use from more than one thread: a roster read finishes off the UI thread and the
/// panel is drawn on it.</para>
/// </remarks>
public sealed class RecentLeavers
{
    /// <summary>How long a row stays after somebody leaves.</summary>
    public static readonly TimeSpan Stay = TimeSpan.FromSeconds(60);

    private readonly IModbotClock _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RecentLeaver> _left = new(StringComparer.Ordinal);
    private Dictionary<string, RosterMember>? _before;

    public RecentLeavers(IModbotClock clock) => _clock = clock;

    /// <summary>Takes a roster read for the instance the moderator is in.</summary>
    public void Update(IReadOnlyList<RosterMember> present)
    {
        ArgumentNullException.ThrowIfNull(present);

        lock (_gate)
        {
            var now = _clock.UtcNow;

            var here = new Dictionary<string, RosterMember>(StringComparer.Ordinal);
            foreach (var member in present)
                here[member.SubjectId] = member;

            if (_before is { } before)
            {
                foreach (var (subject, member) in before)
                {
                    if (!here.ContainsKey(subject) && !_left.ContainsKey(subject))
                        _left[subject] = new RecentLeaver(member, now);
                }
            }

            // Back again: an ordinary row, and nothing greyed beside it.
            foreach (var subject in here.Keys)
                _left.Remove(subject);

            _before = here;
            Trim(now);
        }
    }

    /// <summary>Who has left within <see cref="Stay"/>, the first to go first.</summary>
    public IReadOnlyList<RecentLeaver> Current()
    {
        lock (_gate)
        {
            Trim(_clock.UtcNow);
            return [.. _left.Values.OrderBy(l => l.LeftAt).ThenBy(l => l.Member.SubjectId, StringComparer.Ordinal)];
        }
    }

    /// <summary>The row of somebody who left within <see cref="Stay"/>, or null.</summary>
    public RosterMember? Find(string subjectId)
    {
        lock (_gate)
        {
            Trim(_clock.UtcNow);
            return _left.TryGetValue(subjectId, out var leaver) ? leaver.Member : null;
        }
    }

    /// <summary>Forgets everyone: another instance, other people.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _left.Clear();
            _before = null;
        }
    }

    /// <summary>
    /// Lets go of everyone whose time is up. A clock set back by more than <see cref="Stay"/>
    /// counts as time up too, so nobody is held longer than a minute whichever way it moves.
    /// </summary>
    private void Trim(DateTimeOffset now)
    {
        List<string>? gone = null;

        foreach (var (subject, leaver) in _left)
        {
            var gap = now - leaver.LeftAt;
            if (gap >= Stay || gap < -Stay)
                (gone ??= []).Add(subject);
        }

        if (gone is null)
            return;

        foreach (var subject in gone)
            _left.Remove(subject);
    }
}
