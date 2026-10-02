using Modbot.VRChat;

namespace Modbot.Api.Features.Calendar;

/// <summary>An event as a person fills it in (calendar design §2).</summary>
/// <param name="StartsAt">The first start as wall-clock time in <paramref name="TimeZone"/>: <c>2026-09-20T20:00</c>.</param>
/// <param name="EndsAt">The first end, the same way.</param>
/// <param name="TimeZone">An IANA name, such as <c>Europe/London</c>.</param>
/// <param name="Repeat"><c>none</c>, <c>daily</c>, <c>weekly</c> or <c>monthly</c>.</param>
/// <param name="RepeatDays">For weekly: <c>MO</c> to <c>SU</c>.</param>
/// <param name="RepeatUntil">The last date an occurrence may start on, <c>2026-12-31</c>, or null.</param>
/// <param name="VRChatImageId">
/// The VRChat file id of the picture for VRChat's calendar, as <c>POST /api/calendar/vrchat-picture</c>
/// answered it, or one an older event already has. Null for none.
/// </param>
/// <param name="Draft">Save without publishing anything or opening anything.</param>
/// <param name="WorldListId">
/// Pick the world from this world list, date by date, instead of <paramref name="WorldId"/>.
/// </param>
/// <param name="InviteHostUserId">The staff account invited first when the instance opens.</param>
/// <param name="InviteStaffUserIds">The staff accounts invited after the host.</param>
/// <param name="InviteListId">
/// The saved list whose people are invited last. Picking or changing it needs See members and See
/// profiles.
/// </param>
/// <param name="AnnounceFirstJoinInDiscord">Post once in the channel post's channel when the first person is in the instance.</param>
/// <param name="AnnounceFirstJoinInVRChat">Post once in the VRChat group's posts when the first person is in the instance.</param>
/// <param name="MentionRoleId">
/// A Discord role the channel post mentions, pinged once per date when the date's post first goes
/// up; null for none. Must be a role in the server that the bot may mention, and never @everyone.
/// </param>
/// <param name="RepeatEvery">
/// How many days, weeks or months apart the repeat falls, 1 to 52: 2 with <c>weekly</c> is every
/// other week. Null is 1.
/// </param>
/// <param name="RepeatTimes">
/// Stop after this many dates, the first included, 1 to 500; null for no count. Not together with
/// <paramref name="RepeatUntil"/>.
/// </param>
/// <param name="Featured">Ask VRChat to show the event as featured. Null keeps what the event has.</param>
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
    bool Draft,
    Guid? WorldListId = null,
    Guid? InviteHostUserId = null,
    IReadOnlyList<Guid>? InviteStaffUserIds = null,
    Guid? InviteListId = null,
    bool AnnounceFirstJoinInDiscord = false,
    bool AnnounceFirstJoinInVRChat = false,
    string? MentionRoleId = null,
    int? RepeatEvery = null,
    int? RepeatTimes = null,
    bool? Featured = null);

/// <summary>How far the current time's invites have got (calendar auto-invite design §10).</summary>
/// <param name="Total">Everybody on the queue who was not skipped: the M in "Invited N of M".</param>
/// <param name="Invited">VRChat invites and Discord messages that went out: the N.</param>
/// <param name="Skipped">Banned, or already in the instance. Not in <paramref name="Total"/>.</param>
/// <param name="NotAsked">Did not ask for event invites, so nothing was sent. In <paramref name="Total"/>.</param>
public sealed record CalendarInvitesView(
    int Total,
    int Invited,
    int VRChat,
    int Discord,
    int CouldNotReach,
    int NoWay,
    int Waiting,
    int Stopped,
    int Skipped,
    int NotAsked = 0);

/// <summary>A staff account an event can invite.</summary>
public sealed record CalendarStaffChoice(Guid Id, string Name, bool HasVRChat, bool HasDiscord);

/// <summary>A saved list an event can invite.</summary>
public sealed record CalendarListChoice(Guid Id, string Name);

/// <param name="Lists">Null without See members and See profiles: picking a list needs both.</param>
public sealed record CalendarInviteChoicesView(
    IReadOnlyList<CalendarStaffChoice> Staff,
    IReadOnlyList<CalendarListChoice>? Lists);

/// <summary>One place an event is published, and how that went.</summary>
/// <param name="Place"><c>vrchat</c>, <c>discordEvent</c> or <c>channelPost</c>.</param>
/// <param name="State"><c>waiting</c>, <c>published</c>, <c>failed</c> or <c>removed</c>.</param>
/// <param name="MissingGroupPermission">
/// Set when VRChat refused the last write because Modbot's VRChat account lacks a group permission.
/// </param>
/// <param name="CanTryAgain">
/// VRChat's calendar only: a create that got no answer and was not on VRChat's calendar either.
/// Modbot does not send it again on its own; <c>POST /api/calendar/events/{id}/vrchat/try-again</c> does.
/// </param>
public sealed record CalendarPlaceView(
    string Place,
    string State,
    string? Error,
    DateTimeOffset? ErrorAt,
    DateTimeOffset UpdatedAt,
    MissingGroupPermission? MissingGroupPermission = null,
    bool CanTryAgain = false);

/// <summary>The instance Modbot opened, or tried to, for the current occurrence.</summary>
/// <param name="InstanceId">The instance in <c>vrchat_instance</c>, for the instance popup.</param>
/// <param name="FirstJoinDiscordPostError">What Discord said when the post for the first person was refused.</param>
/// <param name="FirstJoinVRChatPostError">What VRChat said when the group post for the first person was refused.</param>
/// <param name="Checking">
/// VRChat gave no clear answer to the attempt, and Modbot is looking for an instance it may have
/// made. Open now is refused meanwhile.
/// </param>
/// <param name="InstanceMade">VRChat made the instance, whether or not Modbot has it on record yet.</param>
public sealed record CalendarOpeningView(
    DateTimeOffset OccurrenceStartsAt,
    DateTimeOffset AttemptedAt,
    Guid? InstanceId,
    string? JoinLink,
    bool Closed,
    string? Error,
    string? FirstJoinDiscordPostError = null,
    string? FirstJoinVRChatPostError = null,
    bool Checking = false,
    bool InstanceMade = false);

/// <summary>One date of an event.</summary>
/// <param name="PlannedStartsAt">
/// When the event's repeat says this date starts. The same as <paramref name="StartsAt"/> unless it
/// was moved on its own; what a change to this one date names it by.
/// </param>
/// <param name="Title">The date's own title, when it was given one.</param>
/// <param name="Description">The date's own description, when it was given one.</param>
/// <param name="VRChatError">What VRChat said when it refused this date's own change.</param>
public sealed record CalendarOccurrenceView(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset PlannedStartsAt,
    string? Title = null,
    string? Description = null,
    string? VRChatError = null);

/// <summary>A change to one date of a repeating event (calendar design §2.2).</summary>
/// <param name="PlannedStartsAt">The date, as its occurrence's <c>plannedStartsAt</c> names it.</param>
/// <param name="StartsAt">When that date starts now.</param>
/// <param name="EndsAt">When that date ends now.</param>
/// <param name="Title">Its own title. Empty, or the event's own, keeps the event's.</param>
/// <param name="Description">Its own description. Empty, or the event's own, keeps the event's.</param>
public sealed record CalendarDateRequest(
    DateTimeOffset PlannedStartsAt,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Title,
    string? Description);

/// <param name="PlannedStartsAt">The date to cancel, as its occurrence's <c>plannedStartsAt</c> names it.</param>
/// <param name="PostInChannel">
/// Also post in the event's channel that this date is cancelled, once, as a whole-event cancel can.
/// </param>
public sealed record CalendarDateCancelRequest(DateTimeOffset PlannedStartsAt, bool PostInChannel = false);

/// <summary>A picture uploaded to VRChat for an event's VRChat calendar entry.</summary>
/// <param name="FileId">The <c>file_…</c> id VRChat gave it, to save as the event's <c>vrChatImageId</c>.</param>
public sealed record CalendarVRChatPictureView(string FileId);

/// <param name="StartsAtLocal">The first start as wall-clock time in the event's zone, for the form.</param>
/// <param name="Occurrences">The occurrences inside the range asked for.</param>
/// <param name="MadeOnVRChat">Made on VRChat (on vrchat.com or in the game) and read in by Modbot.</param>
/// <param name="CancelledAt">When it was cancelled; its times after that never ran. Null when it was not.</param>
/// <param name="WorldId">The world; for an event that picks from a list, the one picked for the current date.</param>
/// <param name="WorldListId">The world list the world is picked from, or null.</param>
/// <param name="WorldListName">That list's name.</param>
/// <param name="WorldListEmpty">The list has no worlds, so there is nothing to pick from.</param>
/// <param name="CancelledDates">Dates of a repeating event cancelled on their own, inside the range asked for, at their planned times.</param>
/// <param name="InviteListName">The invite list's name, or null when it has none or the list is gone.</param>
/// <param name="Invites">How far the current time's invites have got; null before any were queued.</param>
/// <param name="RepeatEvery">How many days, weeks or months apart the repeat falls; 1 for every one.</param>
/// <param name="RepeatTimes">How many dates the repeat has before it stops, or null.</param>
/// <param name="Featured">Whether VRChat is asked to show the event as featured.</param>
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
    DateTimeOffset? CancelledAt = null,
    Guid? WorldListId = null,
    string? WorldListName = null,
    bool WorldListEmpty = false,
    IReadOnlyList<CalendarOccurrenceView>? CancelledDates = null,
    Guid? InviteHostUserId = null,
    IReadOnlyList<Guid>? InviteStaffUserIds = null,
    Guid? InviteListId = null,
    string? InviteListName = null,
    bool AnnounceFirstJoinInDiscord = false,
    CalendarInvitesView? Invites = null,
    bool AnnounceFirstJoinInVRChat = false,
    string? MentionRoleId = null,
    int RepeatEvery = 1,
    int? RepeatTimes = null,
    bool Featured = false);

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
    CalendarReadyView? Ready = null,
    bool PictureUploads = false);

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
/// <param name="Featured">Whether VRChat is asked to show the event as featured.</param>
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
    bool Notify,
    bool Featured = false);

/// <param name="Frequency">VRChat's word: <c>daily</c>, <c>weekly</c> or <c>monthly</c>.</param>
/// <param name="Days">For weekly, VRChat's day names.</param>
/// <param name="Until">The last day, as VRChat is sent it, or null.</param>
/// <param name="TimeZone">The time zone the repeat is counted in.</param>
/// <param name="Every">How many days, weeks or months apart, as VRChat's <c>interval</c>.</param>
/// <param name="Times">
/// How many times, as VRChat's "after N times" is sent: counted from where the series sent starts,
/// so the dates already past are not in it. Null when it has no count.
/// </param>
public sealed record CalendarVRChatRepeatView(
    string Frequency,
    IReadOnlyList<string> Days,
    string? Until,
    string? TimeZone,
    int Every = 1,
    int? Times = null);

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

/// <param name="InstanceId">The instance whose people count is used: the one the instance popup is open on.</param>
/// <param name="Instead">The world on screen, to put back and pick the one after it in its place (Pick another).</param>
public sealed record NextGameRequest(Guid? InstanceId, string? Instead);

/// <summary>A world picked as the next game, with what the page shows of it.</summary>
public sealed record NextGameWorldView(
    string WorldId,
    string? Name,
    string? ThumbnailUrl,
    int? MinPlayers,
    int? MaxPlayers,
    DateTimeOffset PickedAt);

/// <summary>Next game during an event that picks from a world list (world lists design §6).</summary>
/// <param name="InstanceId">The event's instance whose people were counted; null when there is none.</param>
/// <param name="People">How many people are in it now; null when unknown, and then players are ignored.</param>
/// <param name="PeopleUnsure">The count came from <c>n_users</c>, shown as "8?".</param>
/// <param name="Game">The world picked last as the next game, still standing; null before the first.</param>
/// <param name="NoneFits">Nothing in the list fits the people now: the answer to a pick that picked nothing.</param>
/// <param name="CanPick">The account may press Next game and Pick another.</param>
public sealed record NextGameView(
    Guid EventId,
    string EventTitle,
    Guid ListId,
    string ListName,
    Guid? InstanceId,
    int? People,
    bool PeopleUnsure,
    NextGameWorldView? Game,
    bool NoneFits,
    bool CanPick);

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
