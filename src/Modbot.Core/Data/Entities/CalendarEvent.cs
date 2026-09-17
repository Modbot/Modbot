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

    public string? WorldId { get; set; }

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
}

/// <summary>The words stored in <see cref="CalendarEventPlace.Place"/>.</summary>
public static class CalendarPlaces
{
    public const string VRChat = "vrchat";
    public const string DiscordEvent = "discordEvent";
    public const string ChannelPost = "channelPost";
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

    /// <summary>A hash of what was last refused. Not sent again until it changes.</summary>
    public string? FailedFingerprint { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? ErrorAt { get; set; }

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
