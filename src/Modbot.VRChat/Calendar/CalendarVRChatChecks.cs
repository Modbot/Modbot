using Modbot.Core.Data.Entities;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// What can be known to be wrong with an event before it is sent to VRChat's calendar, so every
/// problem is shown at once rather than one VRChat refusal at a time (calendar design §17.2,
/// added 2026-10-02).
/// </summary>
/// <remarks>
/// <para>
/// On 2026-10-02 a test on a live install went three rounds: VRChat refused the picture id, the
/// id was fixed, then VRChat refused for Manage Group Calendar, which Modbot's own copy of the
/// account's group permissions could have said in the first round. A missing permission comes
/// first, because nothing else matters until it is given.
/// </para>
/// <para>
/// <strong>The picture id is never checked for its shape</strong> (foundation §3.1.1: VRChat ids
/// are opaque). Only what cannot be an id at all is refused: a space inside it, or the characters
/// that end an address's path segment, since VRChat puts the id in a path. What a person pastes is
/// cut down to the file id inside it when the event is saved
/// (<see cref="Core.Files.VRChatFileIds.Find"/>, calendar design §15.1), and the save refuses what
/// <see cref="PictureIdProblem"/> refuses while the event goes to VRChat, so the two never disagree.
/// Until 2026-10-02 this class also took the id out of a pasted address itself; that moved to the
/// save, which takes any VRChat link, not only the API host's.
/// </para>
/// </remarks>
public static class CalendarVRChatChecks
{
    /// <summary>The same words the form uses, so the event says the same thing in both places.</summary>
    public const string NoTitle = "An event needs a title.";

    public const string NoDescription = "VRChat's calendar needs a description.";

    public const string NotAPictureId = "The VRChat image id is not a VRChat file id.";

    /// <summary>
    /// <see cref="NotAPictureId"/> when the id cannot be one -- a space in it, or a slash, question
    /// mark or hash, as a link that is not a VRChat picture address has -- or null.
    /// </summary>
    public static string? PictureIdProblem(string? id) =>
        id is { Length: > 0 } && id.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '?' or '#')
            ? NotAPictureId
            : null;

    /// <summary>
    /// Whether Modbot's VRChat account lacks Manage Group Calendar as the group was last read:
    /// true only when it was read and the permission is not there. Not read yet is not a problem;
    /// VRChat's own answer says.
    /// </summary>
    public static bool LacksCalendarPermission(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return VRChatGroupPermissions.Holds(settings.VRChatAccountPermissions, VRChatGroupPermissions.ManageCalendar) == false;
    }

    /// <summary>
    /// The problems with the event's own fields that VRChat would refuse, in the order the form
    /// shows the fields: the title, the description, the picture id. The permission is apart
    /// (<see cref="LacksCalendarPermission"/>), because it is about the account, not the event.
    /// </summary>
    public static List<string> FieldProblems(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(calendarEvent.Title))
            problems.Add(NoTitle);

        if (string.IsNullOrWhiteSpace(calendarEvent.Description))
            problems.Add(NoDescription);

        if (PictureIdProblem(calendarEvent.VRChatImageId) is { } picture)
            problems.Add(picture);

        return problems;
    }
}
