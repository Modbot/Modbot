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

    /// <summary>The last date an occurrence may start on, in the event's own time zone. Null repeats forever.</summary>
    public DateOnly? RepeatUntil { get; set; }

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

    // ── VRChat's settings Modbot's form does not have ────────────────────────────────────
    //
    // Kept as VRChat said them, for an event read from VRChat's calendar, and sent back unchanged
    // with every update: VRChat's update body sends "featured" and "uses instance overflow" as false
    // when they are left out, so an edit from Modbot would otherwise switch them off. Null for an
    // event made in Modbot, which sends what it always has.

    /// <summary>True when the event was made on VRChat (on vrchat.com or in the game) and read in by Modbot.</summary>
    public bool MadeOnVRChat { get; set; }

    public bool? VRChatFeatured { get; set; }

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

    public string? ChannelId { get; set; }

    public bool AutoOpen { get; set; }

    /// <summary>How many minutes before the start the instance is opened.</summary>
    public int OpenMinutesBefore { get; set; } = 10;

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
}

/// <summary>The words stored in <see cref="CalendarEventPlace.Place"/>.</summary>
public static class CalendarPlaces
{
    public const string VRChat = "vrchat";
    public const string DiscordEvent = "discordEvent";
    public const string ChannelPost = "channelPost";

    /// <summary>
    /// The short message in the event's channel that says it is cancelled. Made by the cancel, only
    /// when the moderator ticked it, and posted once (added 2026-10-01).
    /// </summary>
    public const string CancelPost = "cancelPost";
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

    /// <summary>VRChat's calendar event id, Discord's event id, or the channel message id.</summary>
    public string? ExternalId { get; set; }

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
