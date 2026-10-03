namespace Modbot.Core.Data.Entities;

/// <summary>The words stored in <see cref="CalendarEvent.State"/> (calendar design §2.1).</summary>
public static class CalendarEventStates
{
    public const string Draft = "draft";
    public const string Scheduled = "scheduled";
    public const string Open = "open";
    public const string Finished = "finished";
    public const string Cancelled = "cancelled";

    /// <summary>States in which an event is live: published, opened on time, in the feed.</summary>
    public static bool IsLive(string state) => state is Scheduled or Open;
}

/// <summary>The words stored in <see cref="CalendarEvent.Repeat"/>.</summary>
public static class CalendarRepeats
{
    public const string None = "none";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";

    public static readonly IReadOnlyList<string> All = [None, Daily, Weekly, Monthly];

    /// <summary>The most days, weeks or months apart a repeat may fall (<see cref="CalendarEvent.RepeatEvery"/>).</summary>
    public const int MaxEvery = 52;

    /// <summary>The most dates a counted repeat may have (<see cref="CalendarEvent.RepeatTimes"/>).</summary>
    public const int MaxTimes = 500;

    /// <summary>Two-letter day names, the spelling iCalendar and VRChat both use.</summary>
    public static readonly IReadOnlyList<string> Days = ["MO", "TU", "WE", "TH", "FR", "SA", "SU"];
}

/// <summary>
/// One planned event, and the rule it repeats by. The table is <c>calendar_event</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule is stored, not the occurrences</strong> (calendar design §2). A weekly event is
/// one row however many weeks it runs. <see cref="StartsAt"/> and <see cref="EndsAt"/> are the
/// first occurrence; <see cref="TimeZone"/> is what keeps later ones at the same wall-clock time
/// through daylight-saving changes; <see cref="OccurrenceStartsAt"/> is the one Modbot is dealing
/// with now.
/// </para>
/// <para>
/// Every time here comes from <c>IModbotClock</c>, and the world id is opaque text (foundation
/// §3.1.1).
/// </para>
/// </remarks>
public class CalendarEvent
{
    public const int MaxTitleLength = 100;
    public const int MaxDescriptionLength = 1000;

    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    // ── When ─────────────────────────────────────────────────────────────────────────────

    /// <summary>When the first occurrence starts.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>When the first occurrence ends. Every occurrence lasts as long.</summary>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>An IANA name, such as <c>Europe/London</c>, or <c>UTC</c>.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>One of <see cref="CalendarRepeats"/>.</summary>
    public string Repeat { get; set; } = CalendarRepeats.None;

    /// <summary>For a weekly event, which days, as <c>MO</c> to <c>SU</c>. Empty means the first start's day.</summary>
    public List<string> RepeatDays { get; set; } = [];

    /// <summary>
    /// How many days, weeks or months apart the repeat falls: 1 is every week, 2 every other week.
    /// Counted from the first start's own day, week (Monday to Sunday) or month, as iCalendar's
    /// <c>INTERVAL</c> and VRChat's <c>interval</c> count it (added 2026-10-02).
    /// </summary>
    public int RepeatEvery { get; set; } = 1;

    /// <summary>The last date an occurrence may start on, in the event's own time zone. Null repeats forever.</summary>
    public DateOnly? RepeatUntil { get; set; }

    /// <summary>
    /// How many dates the repeat has before it stops, the first start included; null for no count.
    /// Never set together with <see cref="RepeatUntil"/>. A date cancelled on its own still counts,
    /// as an <c>EXDATE</c> does under iCalendar's <c>COUNT</c> (added 2026-10-02).
    /// </summary>
    public int? RepeatTimes { get; set; }

    // ── Where ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The world. For an event that picks from a list, the world picked for the current date
    /// (world lists design §5), so every place that shows the world reads it the same way.
    /// </summary>
    public string? WorldId { get; set; }

    /// <summary>The world list the world is picked from, date by date. Null when the world is set by hand.</summary>
    public Guid? WorldListId { get; set; }

    /// <summary>
    /// The date <see cref="WorldId"/> was picked for, for an event that picks from a list. When it is
    /// not <see cref="OccurrenceStartsAt"/>, the current date still needs its pick.
    /// </summary>
    public DateTimeOffset? WorldPickedFor { get; set; }

    /// <summary>The instance's group access: <c>members</c>, <c>plus</c> or <c>public</c>.</summary>
    public string AccessType { get; set; } = "members";

    /// <summary><c>us</c>, <c>use</c>, <c>eu</c> or <c>jp</c>.</summary>
    public string Region { get; set; } = "us";

    /// <summary>A picture link for the Discord event and the channel post.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>A VRChat file id for VRChat's calendar. Modbot never uploads one.</summary>
    public string? VRChatImageId { get; set; }

    /// <summary>
    /// The picture cropped in the form for Discord (calendar design §15.4, added 2026-10-02): the
    /// Discord event's cover and the channel post's picture, in place of <see cref="ImageUrl"/>.
    /// </summary>
    public Guid? CoverPictureId { get; set; }

    // ── VRChat's calendar fields ─────────────────────────────────────────────────────────

    /// <summary>VRChat's category word, such as <c>hangout</c>.</summary>
    public string Category { get; set; } = "hangout";

    public List<string> Languages { get; set; } = [];

    /// <summary>VRChat's platform words: <c>standalonewindows</c>, <c>android</c>, <c>ios</c>.</summary>
    public List<string> Platforms { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    /// <summary>Who sees it on VRChat's calendar: <c>group</c> or <c>public</c>.</summary>
    public string Visibility { get; set; } = "group";

    /// <summary>Whether VRChat tells group members when the event is created there.</summary>
    public bool NotifyMembers { get; set; }

    /// <summary>
    /// Whether VRChat shows the event as featured. A form field since 2026-10-02; before that it was
    /// only kept as VRChat said it, for an event read from VRChat's calendar.
    /// </summary>
    public bool Featured { get; set; }

    // ── VRChat's settings Modbot's form does not have ────────────────────────────────────
    //
    // Kept as VRChat said them, for an event read from VRChat's calendar, and sent back unchanged
    // with every update: VRChat's update body sends "uses instance overflow" as false when it is
    // left out, so an edit from Modbot would otherwise switch it off. Null for an event made in
    // Modbot, which sends what it always has. Why the form leaves them out: calendar repeats and
    // VRChat settings design §6.

    /// <summary>True when the event was made on VRChat (on vrchat.com or in the game) and read in by Modbot.</summary>
    public bool MadeOnVRChat { get; set; }

    public int? VRChatHostEarlyJoinMinutes { get; set; }

    public int? VRChatGuestEarlyJoinMinutes { get; set; }

    public int? VRChatCloseInstanceAfterEndMinutes { get; set; }

    /// <summary>The group roles VRChat shows the event to; null or empty for everyone it is visible to.</summary>
    public List<string>? VRChatRoleIds { get; set; }

    public bool? VRChatUsesInstanceOverflow { get; set; }

    // ── Where it goes ────────────────────────────────────────────────────────────────────

    public bool PublishToVRChat { get; set; }

    public bool PublishToDiscord { get; set; }

    public bool PostToChannel { get; set; }

    /// <summary>
    /// Whether the event goes to the Google calendar in Settings → Google Calendar (Google Calendar
    /// design §3.2, added 2026-10-03). An event visible only to the group's members never goes,
    /// ticked or not (decision 1).
    /// </summary>
    public bool PublishToGoogle { get; set; }

    public string? ChannelId { get; set; }

    /// <summary>
    /// The Discord role the channel post mentions, or null for none (calendar design §3.3.1). Off
    /// unless a moderator picks one. The role is pinged once per date, on the date's first post;
    /// an edit never pings. Never the server's @everyone role, whose id is the server's.
    /// </summary>
    public string? MentionRoleId { get; set; }

    public bool AutoOpen { get; set; }

    /// <summary>How many minutes before the start the instance is opened.</summary>
    public int OpenMinutesBefore { get; set; } = 10;

    // ── Who is invited when the instance opens (calendar auto-invite design) ─────────────

    /// <summary>The staff account invited first. Reached through its linked accounts.</summary>
    public Guid? InviteHostUserId { get; set; }

    /// <summary>The staff accounts invited after the host, in this order.</summary>
    public List<Guid> InviteStaffUserIds { get; set; } = [];

    /// <summary>The saved list whose people are invited last, worked out when the instance opens.</summary>
    public Guid? InviteListId { get; set; }

    /// <summary>Post once in the channel post's channel when the first person is in the instance.</summary>
    public bool AnnounceFirstJoinInDiscord { get; set; }

    /// <summary>Post once in the VRChat group's posts when the first person is in the instance.</summary>
    public bool AnnounceFirstJoinInVRChat { get; set; }

    /// <summary>Whether opening the instance also invites anybody.</summary>
    public bool InvitesAnybody => InviteHostUserId is not null || InviteStaffUserIds.Count > 0 || InviteListId is not null;

    // ── Where it is now ──────────────────────────────────────────────────────────────────

    /// <summary>One of <see cref="CalendarEventStates"/>.</summary>
    public string State { get; set; } = CalendarEventStates.Draft;

    /// <summary>The start of the occurrence Modbot is dealing with: the one running, or the next.</summary>
    public DateTimeOffset? OccurrenceStartsAt { get; set; }

    /// <summary>Goes up on every change a person makes. The feed's <c>SEQUENCE</c>.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a person last changed it. What quick edits are folded against.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Set when deleted: a cancel that is also hidden from the calendar page.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Dates of a repeating event cancelled or changed on their own (calendar design §2.2). Loaded
    /// with the event every time, because every place an event goes has to follow them.
    /// </summary>
    public List<CalendarDateChange> DateChanges { get; set; } = [];
}

/// <summary>
/// One date of a repeating event, cancelled or changed on its own. The table is
/// <c>calendar_date_change</c>.
/// </summary>
/// <remarks>
/// <para>
/// A date is known by <see cref="PlannedStartsAt"/>: when the event's repeat says it starts. That
/// stays the same when the date is moved, so the Discord event, the channel post and VRChat's own
/// copy of the date are the same ones before and after the move -- the way a calendar program's
/// <c>RECURRENCE-ID</c> names the date an override replaces.
/// </para>
/// <para>
/// The <c>VRChat…</c> fields are the publisher's: what it last sent to VRChat for this date.
/// </para>
/// </remarks>
public class CalendarDateChange
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    /// <summary>When the event's repeat says this date starts.</summary>
    public DateTimeOffset PlannedStartsAt { get; set; }

    /// <summary>The date does not happen.</summary>
    public bool Cancelled { get; set; }

    /// <summary>When it starts instead. Null keeps the planned time.</summary>
    public DateTimeOffset? StartsAt { get; set; }

    /// <summary>When it ends instead. Null keeps the planned length.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Its own title. Null uses the event's.</summary>
    public string? Title { get; set; }

    /// <summary>Its own description. Null uses the event's.</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a person last changed it. What quick edits are folded against before VRChat hears.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// For a cancelled date: the channel to post in that it is cancelled, when the moderator ticked
    /// it (calendar design §14.4). Null when no post was asked for, or when Discord refused it.
    /// </summary>
    public string? CancelPostChannelId { get; set; }

    /// <summary>The cancel post's message id once it is posted. Posted once, never again.</summary>
    public string? CancelPostId { get; set; }

    /// <summary>
    /// When the cancel post was taken off Discord, a day after the date was due to end, or found
    /// already gone (added 2026-10-01). Null while it is still up, or was never posted.
    /// </summary>
    public DateTimeOffset? CancelPostRemovedAt { get; set; }

    /// <summary>VRChat's id for this one date of the series, once Modbot has found it.</summary>
    public string? VRChatId { get; set; }

    /// <summary>
    /// Where the last update sent to VRChat put this date. Kept when the date is put back as planned,
    /// so it can still be found on VRChat at the time VRChat has it.
    /// </summary>
    public DateTimeOffset? VRChatSentStartsAt { get; set; }

    /// <summary>A hash of what was last sent to VRChat for this date, or found already there.</summary>
    public string? VRChatSentFingerprint { get; set; }

    /// <summary>A hash of what VRChat last refused for this date. Not sent again until it changes.</summary>
    public string? VRChatFailedFingerprint { get; set; }

    public string? VRChatError { get; set; }

    public DateTimeOffset? VRChatErrorAt { get; set; }

    /// <summary>
    /// A hash of what was last sent to Google Calendar for this date on its own (Google Calendar
    /// design §3.5). Not null means Google may hold the date as changed, so the row is kept until
    /// Google has been sent the planned date back.
    /// </summary>
    public string? GoogleSentFingerprint { get; set; }

    /// <summary>A hash of what Google last refused for this date. Not sent again until it changes.</summary>
    public string? GoogleFailedFingerprint { get; set; }

    public string? GoogleError { get; set; }

    public DateTimeOffset? GoogleErrorAt { get; set; }
}

/// <summary>The words stored in <see cref="CalendarEventPlace.Place"/>.</summary>
public static class CalendarPlaces
{
    public const string VRChat = "vrchat";
    public const string DiscordEvent = "discordEvent";
    public const string ChannelPost = "channelPost";

    /// <summary>
    /// The short message in the event's channel that says it is cancelled. Made by the cancel, only
    /// when the moderator ticked it, and posted once (added 2026-10-01). Its state turns to
    /// <see cref="CalendarPlaceStates.Removed"/> once the message is taken off Discord, a day after
    /// the date it named was due to end; the message id stays, so it is never posted again.
    /// </summary>
    public const string CancelPost = "cancelPost";

    /// <summary>
    /// The event on the Google calendar in Settings (Google Calendar design §3.2, added
    /// 2026-10-03). <see cref="CalendarEventPlace.ExternalId"/> is the Google event id Modbot gave
    /// it, kept after it is removed so the next one is never the same id;
    /// <see cref="CalendarEventPlace.GoogleCalendarId"/> is the calendar it was written to.
    /// </summary>
    public const string Google = "googleCalendar";
}

/// <summary>
/// A channel card whose event or date is over, kept until Modbot takes it off Discord. The table is
/// <c>calendar_old_post</c> (added 2026-10-01).
/// </summary>
/// <remarks>
/// <para>
/// A card's place row is about the date the event is on now, and a repeating event's row moves on
/// to the next date, so the old card's message id would be lost. It is written here when the card
/// gets its last word (Finished or Cancelled), and the card is deleted from Discord a day after
/// <see cref="EndsAt"/> (calendar design §3.3).
/// </para>
/// <para>
/// Only cards Modbot posted are ever written here. Cards that got their last word before this list
/// existed were not kept anywhere, and stay up.
/// </para>
/// </remarks>
public class CalendarOldPost
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    /// <summary>The channel the card is in.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The card's message id.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>When the event, or the date the card was about, was due to end.</summary>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>When it was taken off Discord, or found already gone. Null while it is still up.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>
/// One date of an event whose channel post has pinged the event's role (calendar design §3.3.1).
/// A post made again for a date that is here -- after the first was deleted, after the post was
/// turned off and on, or after the event was moved away from the date and back -- shows the role
/// and pings nobody. The table is <c>calendar_role_ping</c>.
/// </summary>
public class CalendarRolePing
{
    public Guid EventId { get; set; }

    /// <summary>The planned start of the date that was pinged.</summary>
    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset PingedAt { get; set; }
}

/// <summary>The words stored in <see cref="CalendarEventPlace.State"/>.</summary>
public static class CalendarPlaceStates
{
    public const string Waiting = "waiting";
    public const string Published = "published";
    public const string Failed = "failed";
    public const string Removed = "removed";
}

/// <summary>
/// One place an event is published, and what was last written there. The table is
/// <c>calendar_event_place</c>.
/// </summary>
/// <remarks>
/// A place is written only when what it should say differs from <see cref="SentFingerprint"/>
/// (calendar design §3), which is how an edit, the instance opening and a cancel all reach it by
/// the same path.
/// </remarks>
public class CalendarEventPlace
{
    public Guid EventId { get; set; }

    /// <summary>One of <see cref="CalendarPlaces"/>.</summary>
    public string Place { get; set; } = string.Empty;

    /// <summary>One of <see cref="CalendarPlaceStates"/>.</summary>
    public string State { get; set; } = CalendarPlaceStates.Waiting;

    /// <summary>
    /// VRChat's calendar event id, Discord's event id, the channel message id, or the Google event
    /// id Modbot chose (kept after removal, for Google only).
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// For Google Calendar, the calendar the event was written to. When Settings names another
    /// calendar, the copy here is deleted and the event is made on the new one (Google Calendar
    /// design §3.2).
    /// </summary>
    public string? GoogleCalendarId { get; set; }

    /// <summary>For a channel post, the channel the message is actually in.</summary>
    public string? ChannelId { get; set; }

    /// <summary>For Discord, which occurrence the event or post is about.</summary>
    public DateTimeOffset? OccurrenceStartsAt { get; set; }

    /// <summary>A hash of what was last written successfully.</summary>
    public string? SentFingerprint { get; set; }

    /// <summary>
    /// For VRChat, the event's <c>updatedAt</c> as VRChat last gave it, in the answer to a write or
    /// in a read of the calendar. A later one on the next read means it was changed on VRChat's side
    /// (calendar design §12). Null until one has been seen.
    /// </summary>
    public DateTimeOffset? VRChatUpdatedAt { get; set; }

    /// <summary>A hash of what was last refused. Not sent again until it changes.</summary>
    public string? FailedFingerprint { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? ErrorAt { get; set; }

    /// <summary>
    /// The VRChat group permission Modbot's VRChat account lacked, when VRChat refused the last
    /// write with a 403 for that reason. Null otherwise.
    /// </summary>
    public string? MissingGroupPermission { get; set; }

    /// <summary>
    /// For VRChat, every problem Modbot found before sending, when it sent nothing because of them:
    /// one sentence each, in the order they are shown. A missing group permission is not among
    /// them; it is <see cref="MissingGroupPermission"/>, shown first. Null when the last failure
    /// was VRChat's own answer, or there is none (calendar design §17.2, added 2026-10-02).
    /// </summary>
    public List<string>? Problems { get; set; }

    /// <summary>
    /// For VRChat, what a create that got no answer sent: the title, times and repeat, as JSON.
    /// VRChat may have made the event anyway, and its copy says what was sent, not what the event
    /// says after an edit, so the copy is looked for by this (calendar design §3.1). Null otherwise.
    /// </summary>
    public string? CreateSent { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One attempt to open an occurrence's instance. The table is <c>calendar_opening</c>, keyed by
/// event and occurrence start, and written before the request is sent (calendar design §4).
/// </summary>
public class CalendarOpening
{
    public Guid EventId { get; set; }

    public DateTimeOffset OccurrenceStartsAt { get; set; }

    public DateTimeOffset AttemptedAt { get; set; }

    /// <summary>The instance's location, when VRChat created it.</summary>
    public string? Location { get; set; }

    /// <summary>The instance it was recorded as, in <c>vrchat_instance</c>.</summary>
    public Guid? InstanceId { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// True when the last attempt certainly made no instance -- the gate never sent it, Cloudflare
    /// stopped it, or VRChat answered 429 -- so a later pass may send it again while the time has not
    /// ended. False once VRChat really refused (a 4xx other than 408 and 429), which is final for that
    /// time, and false for an attempt whose outcome is unknown (<see cref="Checking"/>) (calendar
    /// design §4, added 2026-10-01).
    /// </summary>
    public bool TryAgain { get; set; }

    /// <summary>
    /// True while Modbot looks for an instance VRChat may have made after all: the request went out
    /// and VRChat failed on its side or gave no answer, so it may or may not have opened one. Never
    /// sent again on its own; the group instance poll is watched for one of this world in the group
    /// instead (calendar design §4, added 2026-10-01).
    /// </summary>
    public bool Checking { get; set; }

    /// <summary>
    /// When the unclear answer came back. Only a group instance poll that ran well after this can
    /// say no instance was made: one that started while VRChat was still making it proves nothing.
    /// </summary>
    public DateTimeOffset? CheckingSince { get; set; }

    /// <summary>The staff account that pressed Open now, or null when Modbot opened it on time.</summary>
    public Guid? OpenedByUserId { get; set; }

    /// <summary>
    /// When the invites for this occurrence were put in <c>calendar_invite</c>. Written once; null
    /// until then, and for an event that invites nobody.
    /// </summary>
    public DateTimeOffset? InvitesQueuedAt { get; set; }

    /// <summary>
    /// When the "first person is in" post went to the event's Discord channel, or was about to:
    /// written before the post, so it goes out once however many restarts.
    /// </summary>
    public DateTimeOffset? FirstJoinDiscordPostedAt { get; set; }

    /// <summary>What Discord said when that post was refused.</summary>
    public string? FirstJoinDiscordPostError { get; set; }

    /// <summary>The same for the VRChat group post, written before it is sent.</summary>
    public DateTimeOffset? FirstJoinVRChatPostedAt { get; set; }

    /// <summary>What VRChat said when the group post was refused.</summary>
    public string? FirstJoinVRChatPostError { get; set; }
}

/// <summary>The words stored in <see cref="CalendarInvite.State"/> (calendar auto-invite design §3).</summary>
public static class CalendarInviteStates
{
    /// <summary>A VRChat invite is next.</summary>
    public const string Waiting = "waiting";

    /// <summary>The VRChat invite is being sent. Left here by a crash; counted as sent, never sent again.</summary>
    public const string Sending = "sending";

    /// <summary>VRChat accepted the invite.</summary>
    public const string Invited = "invited";

    /// <summary>A Discord direct message is next.</summary>
    public const string ToMessage = "toMessage";

    /// <summary>The direct message is being sent. Like <see cref="Sending"/>, never sent again.</summary>
    public const string Messaging = "messaging";

    /// <summary>The direct message went out.</summary>
    public const string Messaged = "messaged";

    /// <summary>Tried, and neither VRChat nor Discord took it.</summary>
    public const string CouldNotReach = "couldNotReach";

    /// <summary>Not a friend of the group's VRChat account and no Discord account, or no account at all.</summary>
    public const string NoWay = "noWay";

    /// <summary>Banned, or already in the instance. Not counted.</summary>
    public const string Skipped = "skipped";

    /// <summary>Did not ask for event invites, or stopped them. Nothing is sent (calendar auto-invite design §2.1).</summary>
    public const string NotAsked = "notAsked";

    /// <summary>The instance closed or the time ended before it was sent.</summary>
    public const string Stopped = "stopped";

    /// <summary>States that still have something to send.</summary>
    public static readonly IReadOnlyList<string> Open = [Waiting, ToMessage];
}

/// <summary>The words stored in <see cref="CalendarInvite.Role"/>: why the person is on the queue.</summary>
public static class CalendarInviteRoles
{
    public const string Host = "host";
    public const string Staff = "staff";
    public const string List = "list";
}

/// <summary>
/// One person to invite to one occurrence's instance. The table is <c>calendar_invite</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The queue lives here, not in memory</strong>, so a restart carries on where it stopped
/// (calendar auto-invite design §5). <see cref="Position"/> is the order: host, staff, list.
/// </para>
/// <para>
/// One row per person per occurrence, by <see cref="PersonKey"/>. A row is marked sending or
/// messaging before the request goes out, so a crash between the two never sends twice.
/// </para>
/// <para>
/// The ids are opaque text, never parsed or validated (foundation §3.1.1). A purge deletes the
/// person's rows.
/// </para>
/// </remarks>
public class CalendarInvite
{
    public long Id { get; set; }

    public Guid EventId { get; set; }

    public DateTimeOffset OccurrenceStartsAt { get; set; }

    /// <summary>The order the person is invited in, from zero.</summary>
    public int Position { get; set; }

    /// <summary>One of <see cref="CalendarInviteRoles"/>.</summary>
    public string Role { get; set; } = CalendarInviteRoles.List;

    /// <summary>The staff account, for a host or staff row: whose "Get event invites" switch decides.</summary>
    public Guid? StaffUserId { get; set; }

    /// <summary><c>vrchat:usr_…</c> or <c>discord:…</c>, the way lists name people. Unique per occurrence.</summary>
    public string PersonKey { get; set; } = string.Empty;

    public string? VRChatUserId { get; set; }

    public string? DiscordUserId { get; set; }

    /// <summary>One of <see cref="CalendarInviteStates"/>.</summary>
    public string State { get; set; } = CalendarInviteStates.Waiting;

    /// <summary>Why it did not get through, or why it was skipped, in plain words.</summary>
    public string? Problem { get; set; }

    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When the VRChat invite was sent, or about to be. What the thirty seconds are counted from.</summary>
    public DateTimeOffset? TriedAt { get; set; }

    /// <summary>When the direct message was sent, or about to be.</summary>
    public DateTimeOffset? MessagedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The person's key for a VRChat id, else a Discord id.</summary>
    public static string KeyFor(string? vrchatUserId, string? discordUserId) =>
        vrchatUserId is { Length: > 0 } vrchat ? $"vrchat:{vrchat}" : $"discord:{discordUserId}";
}

/// <summary>
/// The calendar feed's secret link. One row; the table is <c>calendar_feed</c>.
/// </summary>
public class CalendarFeed
{
    public int Id { get; set; } = 1;

    /// <summary>SHA-256 of the token, hex. What a request is matched against.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The token itself, encrypted, so the page can show the link again.</summary>
    public string TokenEncrypted { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
