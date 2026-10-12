namespace Modbot.Api.Features.Calendar;

/// <summary>
/// What the event form holds, without its dates: the shape a template keeps and the form loads
/// (calendar templates plan).
/// </summary>
/// <remarks>
/// Each field is the one of the same name on <see cref="CalendarEventRequest"/> and means the same.
/// What differs is the dates: <paramref name="LengthMinutes"/> stands in for the start and the end,
/// and a repeat is its pattern only (<paramref name="Repeat"/>, <paramref name="RepeatDays"/>,
/// <paramref name="RepeatEvery"/>, <paramref name="RepeatTimes"/>), never a last date. There is no
/// time zone: a template says when an event lasts, not where in the world it starts. A save leaves
/// out what an event is only refused for later -- no title, no channel, no world -- so a template
/// can be filled in over several visits.
/// </remarks>
/// <param name="LengthMinutes">How long the event lasts, 1 minute to 7 days.</param>
public sealed record CalendarTemplateFields(
    string Title,
    string? Description,
    int LengthMinutes,
    string? Repeat,
    IReadOnlyList<string>? RepeatDays,
    int? RepeatEvery,
    int? RepeatTimes,
    string? WorldId,
    Guid? WorldListId,
    string? AccessType,
    string? Region,
    string? ImageUrl,
    string? VRChatImageId,
    Guid? CoverPictureId,
    string? Category,
    IReadOnlyList<string>? Languages,
    IReadOnlyList<string>? Platforms,
    IReadOnlyList<string>? Tags,
    string? Visibility,
    bool NotifyMembers,
    bool? Featured,
    bool PublishToVRChat,
    bool PublishToDiscord,
    bool? PublishToGoogle,
    bool PostToChannel,
    string? ChannelId,
    string? MentionRoleId,
    bool AutoOpen,
    int? OpenMinutesBefore,
    Guid? InviteHostUserId,
    IReadOnlyList<Guid>? InviteStaffUserIds,
    Guid? InviteListId,
    bool AnnounceFirstJoinInDiscord,
    bool AnnounceFirstJoinInVRChat,
    string? VRChatTitle,
    string? VRChatDescription,
    string? DiscordTitle,
    string? DiscordDescription,
    string? GoogleTitle,
    string? GoogleDescription);

/// <summary>A template as a person fills it in.</summary>
/// <param name="Name">What the list calls it, 1 to 100 characters.</param>
public sealed record CalendarTemplateRequest(string Name, CalendarTemplateFields Fields);

/// <summary>A saved template.</summary>
/// <param name="CreatedBy">The account that made it, by user name; null when that account is gone.</param>
public sealed record CalendarTemplateView(
    Guid Id,
    string Name,
    CalendarTemplateFields Fields,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Every template by name, and whether the caller may change them.</summary>
/// <param name="CanManage">The caller holds Manage templates.</param>
public sealed record CalendarTemplatesView(IReadOnlyList<CalendarTemplateView> Templates, bool CanManage);
