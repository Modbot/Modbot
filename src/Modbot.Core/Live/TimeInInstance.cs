using Modbot.Core.Data.Entities;

namespace Modbot.Core.Live;

/// <summary>
/// Somebody, in an instance, at a moment — the question "how long had they been there by then".
/// </summary>
/// <param name="WorldId">Optional, but a instance number is only unique within a world, so give it where it is known.</param>
public readonly record struct InstanceMoment(string SubjectId, string? WorldId, string InstanceId, DateTimeOffset At);

/// <summary>
/// How long somebody had been in an instance when something happened to them.
/// </summary>
/// <param name="HowLong">From the arrival Modbot knows about to the moment asked about.</param>
/// <param name="SeenArriving">
/// True when a moderator's companion watched them walk in, so this is exactly how long they were
/// there. False when a companion only ever found them already present, so they had been there
/// <em>at least</em> this long and arrived at some earlier time nobody saw.
/// </param>
public sealed record TimeThere(TimeSpan HowLong, bool SeenArriving);

/// <summary>
/// How long somebody had been in an instance by a given moment, from presence facts alone.
/// </summary>
/// <remarks>
/// <para><strong>Built on <see cref="InstanceWatching"/> rather than beside it.</strong> That rule
/// already answers "who is in this instance, and since when" and is careful in the ways this needs
/// to be: a roster is only believed while a companion was actually reporting from inside the
/// instance, a gap in watching starts a fresh stretch instead of carrying the old one across it,
/// and somebody first met in an arrival burst is recorded as having been here <em>before</em> that
/// moment rather than as having arrived at it. Re-deriving any of that here would be a second
/// copy of the same judgement, free to drift.</para>
/// <para><strong>Silence is the failure mode.</strong> When nobody's companion was reporting,
/// Modbot does not know how long the person had been there, and the answer is null — never zero,
/// never the nearest guess. A made-up duration in a moderation record is worse than an absent one.</para>
/// <para>Pure: no database and no clock.</para>
/// </remarks>
public static class TimeInInstance
{
    /// <summary>
    /// How far back a stay is looked for.
    /// </summary>
    /// <remarks>
    /// A person continuously in one instance for longer than a day is not a real person in a real
    /// instance, and the bound is what keeps the lookup one small query instead of an unbounded
    /// scan. An instance genuinely running longer reports the part of the stay it can see, which is
    /// the same shape of answer as a watch that started late.
    /// </remarks>
    public static readonly TimeSpan LongestStay = TimeSpan.FromHours(24);

    /// <summary>
    /// How close to the moment the subject's own departure is read as the moment itself.
    /// </summary>
    /// <remarks>
    /// A kick throws the person out, so their companion-reported leave lands within a second or two
    /// of VRChat's audit entry — and VRChat's log timestamps are whole seconds, so it can land on
    /// either side of it. Counted plainly, that leave says the person was not in the instance at the
    /// moment they were thrown out of it, and every kick would answer "nothing known". Ten seconds
    /// is the allowance <see cref="InstanceWatching.ArrivalBurstAllowance"/> and the linked-fact
    /// window already use for the same second-resolution slop.
    /// </remarks>
    public static readonly TimeSpan LeaveAllowance = TimeSpan.FromSeconds(10);

    /// <summary>The payload field holding whole seconds. Absent when Modbot does not know.</summary>
    public const string SecondsKey = "inInstanceSeconds";

    /// <summary>The payload field saying whether the number is exact or a lower bound.</summary>
    public const string SeenArrivingKey = "seenArriving";

    /// <param name="instance">The instance being asked about.</param>
    /// <param name="marks">
    /// Presence facts in this instance, plus any placing its possible watchers elsewhere — the same
    /// set <see cref="InstanceWatching.Work"/> takes.
    /// </param>
    /// <param name="deviceOwners">Device id to the VRChat account linked to whoever it was issued to.</param>
    /// <param name="moment">Who, and when.</param>
    /// <returns>How long they had been there, or null when Modbot cannot say.</returns>
    public static TimeThere? Work(
        InstanceKey instance,
        IEnumerable<PresenceMark> marks,
        IReadOnlyDictionary<Guid, string> deviceOwners,
        InstanceMoment moment)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(deviceOwners);

        var kept = marks.Where(m => !IsTheirOwnExit(m, moment));

        // Closed at the moment asked about, so the watch stretch ends there and the answer is the
        // instance as it stood then rather than as it stands now.
        var people = InstanceWatching.Work(instance, kept, deviceOwners, closedAt: moment.At);

        var roster = people.LastSeen.Count > 0 ? people.LastSeen : people.Here;

        var them = roster.FirstOrDefault(p => string.Equals(p.UserId, moment.SubjectId, StringComparison.Ordinal));
        if (them is null)
            return null;

        var howLong = moment.At - them.Since;
        if (howLong < TimeSpan.Zero)
            howLong = TimeSpan.Zero;

        // "They had been here at least no time at all" is the entry's own sentence said twice. A
        // measured zero is different: somebody seen arriving and thrown out in the same second was
        // there for no time, and that is worth recording.
        if (!them.SeenArriving && howLong <= TimeSpan.Zero)
            return null;

        return new TimeThere(howLong, them.SeenArriving);
    }

    /// <summary>The subject's own leave, at or around the moment — which is the moment itself.</summary>
    private static bool IsTheirOwnExit(PresenceMark mark, InstanceMoment moment)
        => mark.Type is FactType.InstanceLeft or FactType.InstanceLogStopped
           && string.Equals(mark.SubjectId, moment.SubjectId, StringComparison.Ordinal)
           && mark.At >= moment.At - LeaveAllowance;
}
