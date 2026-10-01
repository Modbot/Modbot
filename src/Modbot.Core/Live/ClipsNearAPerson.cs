using Modbot.Core.Data.Entities;

namespace Modbot.Core.Live;

/// <summary>A saved clip, as its <see cref="FactType.InstanceClipSaved"/> fact describes it.</summary>
/// <param name="Id">The fact's id.</param>
/// <param name="WorldId">Null on a fact that did not carry one; matched loosely then.</param>
public readonly record struct ClipMark(long Id, string? WorldId, string InstanceId, DateTimeOffset SavedAt);

/// <summary>One presence fact about the person a case file is about.</summary>
public readonly record struct PersonMark(string Type, string? WorldId, string InstanceId, DateTimeOffset At);

/// <summary>
/// Which saved clips may have the person on a case file in them: clips saved in an instance while
/// the companions' reports put that person there.
/// </summary>
/// <remarks>
/// <para><strong>A list to choose from, not a verdict.</strong> The case file shows these as
/// "Clip saved on Alex's PC at 21:14" with an Attach beside each; a moderator decides whether the
/// clip shows anything. So the rule leans towards including: a clip of an instance the person was
/// in is offered, whether or not they were in frame.</para>
/// <para><strong>Where the person was, from presence alone.</strong> A clip is offered when, in the
/// same instance, the person has a presence fact inside the clip's own minutes — the
/// <see cref="LongestClip"/> before Save was pressed — or when the last thing known about them
/// there before Save is that they were in it (an arrival, an "already here" or an avatar change)
/// rather than that they left. That last part is the ordinary case: somebody who walked in at
/// eight and was clipped at nine has no fact at nine at all. Nothing older than
/// <see cref="TimeInInstance.LongestStay"/> is believed, for the reason that rule gives.</para>
/// <para>Pure: no database and no clock.</para>
/// </remarks>
public static class ClipsNearAPerson
{
    /// <summary>The longest clip a companion keeps: five minutes, the top of its slider.</summary>
    public static readonly TimeSpan LongestClip = TimeSpan.FromMinutes(5);

    /// <summary>How long before the ban, or before the case file was written, a clip is looked for.</summary>
    /// <remarks>
    /// A week covers a ban made a few days after what led to it. Further back, presence facts age
    /// out, and so does the fact that said a clip was saved.
    /// </remarks>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(7);

    /// <summary>How long after the ban a clip is still offered: a clip saved straight afterwards is still of what led to it.</summary>
    public static readonly TimeSpan LookAhead = TimeSpan.FromDays(1);

    /// <summary>The presence types that say where somebody was.</summary>
    public static readonly IReadOnlySet<string> PresenceTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        FactType.InstanceJoined,
        FactType.InstancePresenceObserved,
        FactType.InstanceLeft,
        FactType.AvatarChanged,
    };

    /// <summary>The clips, from <paramref name="clips"/>, that the person may be in, oldest first.</summary>
    /// <param name="clips">Saved clips.</param>
    /// <param name="marks">The person's presence facts. Any order.</param>
    public static IReadOnlyList<long> Pick(IEnumerable<ClipMark> clips, IEnumerable<PersonMark> marks)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(marks);

        var known = marks.Where(m => PresenceTypes.Contains(m.Type)).ToList();
        var picked = new List<(long Id, DateTimeOffset At)>();

        foreach (var clip in clips)
        {
            var there = known
                .Where(m => SameInstance(m, clip)
                    && m.At <= clip.SavedAt
                    && m.At >= clip.SavedAt - TimeInInstance.LongestStay)
                .ToList();

            if (there.Count == 0)
                continue;

            var during = there.Any(m => m.At >= clip.SavedAt - LongestClip);
            var last = there.MaxBy(m => m.At);

            if (during || last.Type != FactType.InstanceLeft)
                picked.Add((clip.Id, clip.SavedAt));
        }

        return [.. picked.OrderBy(p => p.At).ThenBy(p => p.Id).Select(p => p.Id)];
    }

    private static bool SameInstance(PersonMark mark, ClipMark clip)
        => string.Equals(mark.InstanceId, clip.InstanceId, StringComparison.Ordinal)
           && (mark.WorldId is null || clip.WorldId is null
               || string.Equals(mark.WorldId, clip.WorldId, StringComparison.Ordinal));
}
