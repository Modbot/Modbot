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
/// <param name="CancelledAt">When it was cancelled; its times after that never ran. Null when it was not.</param>
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
    IReadOnlyList<CalendarOccurrenceView> Occurrences,
    DateTimeOffset? CancelledAt = null);

/// <param name="Categories">VRChat's category words.</param>
/// <param name="Platforms">VRChat's platform words.</param>
/// <param name="CanSeeResults">
/// True with See analytics as well: what each time an event ran did, and Past events.
/// </param>
/// <param name="Ready">Which places are set up, so a ticked place that is not can say so.</param>
public sealed record CalendarView(
    IReadOnlyList<CalendarEventView> Events,
    bool CanManage,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Platforms,
    DateTimeOffset Now,
    bool CanSeeResults = false,
    CalendarReadyView? Ready = null);

/// <summary>Whether the places an event can go are set up (calendar design §14.3).</summary>
/// <param name="VRChat">
/// The VRChat calendar and opening the instance: a managed group is chosen and Modbot has a VRChat
/// account to sign in as.
/// </param>
/// <param name="Discord">
/// The Discord event and the channel post: a server id is set and the bot is connected.
/// </param>
public sealed record CalendarReadyView(bool VRChat, bool Discord);

/// <param name="PostInChannel">
/// Post a short message in the event's channel that it is cancelled. Needs a channel on the event.
/// Posted once, by the calendar's Discord loop.
/// </param>
public sealed record CalendarCancelRequest(bool PostInChannel);

/// <summary>An event as the form holds it, to be drawn the way each place would show it.</summary>
/// <param name="EventId">The event being edited, or null for a new one.</param>
public sealed record CalendarPreviewRequest(Guid? EventId, CalendarEventRequest Event);

/// <summary>
/// The event the way each place would show it, drawn by the code that sends it (calendar design
/// §14.2). Every place is drawn whether or not it is ticked; the form shows the ticked ones.
/// </summary>
/// <param name="DiscordEvent">The Discord server event. Null on a server without the Discord bot built in.</param>
/// <param name="ChannelPost">The card in the channel. Null on a server without the Discord bot built in.</param>
/// <param name="VRChat">What VRChat's calendar is sent.</param>
/// <param name="Feed">What a phone or desktop calendar reads from the calendar feed.</param>
public sealed record CalendarPreviewView(
    Modbot.Core.Calendar.CalendarDiscordEventPreview? DiscordEvent,
    Modbot.Core.Calendar.CalendarChannelPostPreview? ChannelPost,
    CalendarVRChatPreviewView VRChat,
    CalendarFeedPreviewView Feed);

/// <summary>What VRChat's calendar is sent, in VRChat's own words for each field.</summary>
/// <param name="Update">True when the event is on VRChat already, so the next write is an update.</param>
/// <param name="Category">VRChat's category word.</param>
/// <param name="Visibility"><c>group</c> or <c>public</c>.</param>
/// <param name="Platforms">VRChat's platform words.</param>
/// <param name="ImageId">The VRChat file id for the picture, when one was given.</param>
/// <param name="Repeat">The repeat VRChat is sent, or null for an event that does not repeat.</param>
/// <param name="Notify">Whether VRChat is asked to notify the group's members. Only a first create can.</param>
public sealed record CalendarVRChatPreviewView(
    bool Update,
    string Title,
    string Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Category,
    string? Visibility,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> Tags,
    string? ImageId,
    CalendarVRChatRepeatView? Repeat,
    bool Notify);

/// <param name="Frequency">VRChat's word: <c>daily</c>, <c>weekly</c> or <c>monthly</c>.</param>
/// <param name="Days">For weekly, VRChat's day names.</param>
/// <param name="Until">The last day, as VRChat is sent it, or null.</param>
/// <param name="TimeZone">The time zone the repeat is counted in.</param>
public sealed record CalendarVRChatRepeatView(string Frequency, IReadOnlyList<string> Days, string? Until, string? TimeZone);

/// <summary>What a calendar program reads from the feed for this event.</summary>
/// <param name="CalendarName">The name the feed gives the calendar: the group's.</param>
/// <param name="Notes">The description, or null.</param>
/// <param name="Location">The world's name, or its id when Modbot has no name; null without a world.</param>
/// <param name="StartsAt">The first start.</param>
/// <param name="EndsAt">The first end.</param>
/// <param name="Repeat">The repeat rule as the feed writes it (<c>RRULE</c>), or null.</param>
public sealed record CalendarFeedPreviewView(
    string CalendarName,
    string Title,
    string? Notes,
    string? Location,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZone,
    string? Repeat);

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
