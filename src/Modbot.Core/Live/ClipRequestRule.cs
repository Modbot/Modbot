using Modbot.Core.Data.Entities;

namespace Modbot.Core.Live;

/// <summary>
/// Whether an action asks for a clip, from which of the moderator's computers, and when the wait
/// is over. Pure: no database, no clock, no companion list -- the caller passes in what it knows.
/// </summary>
/// <remarks>
/// <para>
/// A clip is asked for only when the moderator's companion was in the same instance as the person
/// acted on, within the last <see cref="DeviceMemory"/>. Otherwise nothing is asked and the action
/// shows "Not in the instance", which is not a miss: nobody could have recorded it.
/// </para>
/// </remarks>
public static class ClipRequestRule
{
    /// <summary>
    /// How long a companion is believed to still be where it last said it was. The same ten
    /// minutes the companions' location memory keeps.
    /// </summary>
    public static readonly TimeSpan DeviceMemory = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How old an action may be and still be asked about. The companion keeps at most ten minutes
    /// of recording, so an older action has nothing left to cut. This also stops a server that was
    /// down for an hour from asking about everything it missed.
    /// </summary>
    public static readonly TimeSpan MaxActionAge = TimeSpan.FromMinutes(10);

    /// <summary>The kind of action a fact is, or null when it is not one a clip is asked for.</summary>
    public static ClipAction? ActionOf(string factType) => factType switch
    {
        FactType.ActionBan or FactType.MemberBanned => ClipAction.Ban,
        FactType.ActionKick or FactType.MemberKicked or FactType.GroupInstanceKick => ClipAction.Kick,
        FactType.GroupInstanceWarn => ClipAction.Warning,
        _ => null,
    };

    /// <summary>Whether the group's setting asks for a clip on this kind of action.</summary>
    public static bool IsSwitchedOn(ClipAction action, bool onBans, bool onKicks, bool onWarnings) => action switch
    {
        ClipAction.Ban => onBans,
        ClipAction.Kick => onKicks,
        ClipAction.Warning => onWarnings,
        _ => false,
    };

    /// <summary>Whether the action is still recent enough to have anything left to cut.</summary>
    public static bool IsRecentEnough(DateTimeOffset actionAt, DateTimeOffset now) => now - actionAt <= MaxActionAge;

    /// <summary>
    /// Where the person was at the moment of the action, from their presence facts: the newest one
    /// at or before the action, unless that one says they had left. Null when unknown or gone.
    /// </summary>
    /// <param name="marks">The person's presence facts. Any order.</param>
    public static (string? WorldId, string InstanceId)? WhereWas(IEnumerable<PersonMark> marks, DateTimeOffset actionAt)
    {
        ArgumentNullException.ThrowIfNull(marks);

        var last = marks
            .Where(m => ClipsNearAPerson.PresenceTypes.Contains(m.Type)
                && m.At <= actionAt
                && m.At >= actionAt - TimeInInstance.LongestStay)
            .OrderByDescending(m => m.At)
            .Cast<PersonMark?>()
            .FirstOrDefault();

        return last is { } mark && mark.Type != FactType.InstanceLeft
            ? (mark.WorldId, mark.InstanceId)
            : null;
    }

    /// <summary>
    /// Which of the moderator's computers to ask: the first, in the order given, that is in the
    /// instance. Null means nobody was there, so nothing is asked.
    /// </summary>
    /// <param name="devices">The moderator's paired computers, the one to prefer first.</param>
    /// <param name="isInInstance">Whether that computer was in the instance in the last ten minutes.</param>
    public static Guid? DeviceToAsk(IEnumerable<Guid> devices, Func<Guid, bool> isInInstance)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(isInInstance);

        foreach (var device in devices)
        {
            if (isInInstance(device))
                return device;
        }

        return null;
    }

    /// <summary>Whether an answer sent now is still taken: inside the 15 minutes from the request.</summary>
    public static bool AnswerStillTaken(DateTimeOffset requestedAt, DateTimeOffset now)
        => now - requestedAt <= ClipRequest.AnswerWindow;
}
