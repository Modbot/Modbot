using Modbot.VRChat;

namespace Modbot.Api.Features.Calendar;

/// <summary>An event as a person fills it in (calendar design §2).</summary>
/// <param name="StartsAt">The first start as wall-clock time in <paramref name="TimeZone"/>: <c>2026-09-20T20:00</c>.</param>
/// <param name="EndsAt">The first end, the same way.</param>
/// <param name="TimeZone">An IANA name, such as <c>Europe/London</c>.</param>
/// <param name="Repeat"><c>none</c>, <c>daily</c>, <c>weekly</c> or <c>monthly</c>.</param>
/// <param name="RepeatDays">For weekly: <c>MO</c> to <c>SU</c>.</param>
/// <param name="RepeatUntil">The last date an occurrence may start on, <c>2026-12-31</c>, or null.</param>
/// <param name="Draft">Save without publishing anything or opening anything.</param>
public sealed record CalendarEventRequest(
    string Title,
    string? Description,
    string StartsAt,
    string EndsAt,
    string TimeZone,
    string? Repeat,
    IReadOnlyList<string>? RepeatDays,
    string? RepeatUntil,
    string? WorldId,
    string? AccessType,
    string? Region,
    string? ImageUrl,
    string? VRChatImageId,
    string? Category,
    IReadOnlyList<string>? Languages,
    IReadOnlyList<string>? Platforms,
    IReadOnlyList<string>? Tags,
    string? Visibility,
    bool NotifyMembers,
    bool PublishToVRChat,
    bool PublishToDiscord,
    bool PostToChannel,
    string? ChannelId,
    bool AutoOpen,
    int? OpenMinutesBefore,
    bool Draft);

/// <summary>One place an event is published, and how that went.</summary>
/// <param name="Place"><c>vrchat</c>, <c>discordEvent</c> or <c>channelPost</c>.</param>
/// <param name="State"><c>waiting</c>, <c>published</c>, <c>failed</c> or <c>removed</c>.</param>
/// <param name="MissingGroupPermission">
/// Set when VRChat refused the last write because Modbot's VRChat account lacks a group permission.
/// </param>
public sealed record CalendarPlaceView(
    string Place,
    string State,
    string? Error,
    DateTimeOffset? ErrorAt,
    DateTimeOffset UpdatedAt,
    MissingGroupPermission? MissingGroupPermission = null);

/// <summary>The instance Modbot opened, or tried to, for the current occurrence.</summary>
/// <param name="InstanceId">The instance in <c>vrchat_instance</c>, for the instance popup.</param>
public sealed record CalendarOpeningView(
    DateTimeOffset OccurrenceStartsAt,
    DateTimeOffset AttemptedAt,
    Guid? InstanceId,
    string? JoinLink,
    bool Closed,
    string? Error);

public sealed record CalendarOccurrenceView(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <param name="StartsAtLocal">The first start as wall-clock time in the event's zone, for the form.</param>
/// <param name="Occurrences">The occurrences inside the range asked for.</param>
/// <param name="MadeOnVRChat">Made on VRChat (on vrchat.com or in the game) and read in by Modbot.</param>
public sealed record CalendarEventView(
    Guid Id,
    string Title,
    string Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string StartsAtLocal,
    string EndsAtLocal,
    string TimeZone,
    string Repeat,
    IReadOnlyList<string> RepeatDays,
    string? RepeatUntil,
    string? WorldId,
    string? WorldName,
    string? WorldThumbnailUrl,
    string AccessType,
    string Region,
    string? ImageUrl,
    string? VRChatImageId,
    string Category,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> Tags,
    string Visibility,
    bool NotifyMembers,
    bool PublishToVRChat,
    bool PublishToDiscord,
    bool PostToChannel,
    string? ChannelId,
    bool AutoOpen,
    int OpenMinutesBefore,
    string State,
    DateTimeOffset? OccurrenceStartsAt,
    DateTimeOffset? OccurrenceEndsAt,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool MadeOnVRChat,
    IReadOnlyList<CalendarPlaceView> Places,
    CalendarOpeningView? Opening,
    IReadOnlyList<CalendarOccurrenceView> Occurrences);

/// <param name="Categories">VRChat's category words.</param>
/// <param name="Platforms">VRChat's platform words.</param>
/// <param name="CanSeeResults">
/// True with See analytics as well: what each time an event ran did, and Past events.
/// </param>
public sealed record CalendarView(
    IReadOnlyList<CalendarEventView> Events,
    bool CanManage,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Platforms,
    DateTimeOffset Now,
    bool CanSeeResults = false);

public sealed record CalendarWorldView(string WorldId, string? Name, string? ThumbnailUrl);

/// <param name="From">The start of the range the page shows. Left out with <paramref name="Upcoming"/>.</param>
/// <param name="To">The end of the range the page shows.</param>
/// <param name="Upcoming">Read what the next event needs instead of a range: this month, and the next when nothing is left in this one.</param>
/// <param name="Refresh">Ask VRChat again even for a month read in the last five minutes.</param>
public sealed record CalendarVRChatReadRequest(DateTimeOffset? From, DateTimeOffset? To, bool Upcoming, bool Refresh);

/// <param name="Outcome"><c>read</c>, <c>remembered</c>, <c>notConfigured</c>, <c>waiting</c> or <c>failed</c>.</param>
/// <param name="Error">What went wrong, in VRChat's words when it gave any.</param>
public sealed record CalendarVRChatReadView(string Outcome, string? Error);

/// <param name="Path">The feed's path on this server, always known.</param>
/// <param name="Url">The whole address, when the public address is set.</param>
public sealed record CalendarFeedView(string? Path, string? Url);
