namespace Modbot.Core.Data.Entities;

/// <summary>
/// How serious a notification is (foundation §4.5.1). Stored as text, never as a number.
/// </summary>
/// <remarks>
/// Severity is the first routing input, ahead of what happened and who it is for, because the
/// failure mode this pipeline exists to avoid is a moderator muting Modbot. A tool that says
/// everything at the same volume gets turned off, and then the one thing that mattered is missed.
/// </remarks>
public static class NotificationSeverities
{
    /// <summary>Modbot cannot do its job. Every channel a person has, straight away.</summary>
    public const string Critical = "critical";

    /// <summary>Something needs a person. Goes out straight away on the channels set to take it.</summary>
    public const string Warning = "warning";

    /// <summary>Worth knowing, not worth interrupting for. Only ever in the daily summary.</summary>
    public const string Information = "information";

    /// <summary>Most serious first. The order the settings screen lists them in.</summary>
    public static IReadOnlyList<string> All { get; } = [Critical, Warning, Information];

    public static bool IsKnown(string? severity) =>
        severity is not null && All.Contains(severity, StringComparer.Ordinal);

    public static string Label(string severity) => severity switch
    {
        Critical => "Critical",
        Warning => "Warning",
        Information => "Information",
        _ => severity,
    };

    /// <summary>Bigger is more serious. Only ever compared, never stored.</summary>
    public static int Rank(string severity) => severity switch
    {
        Critical => 3,
        Warning => 2,
        Information => 1,
        _ => 0,
    };
}

/// <summary>
/// One thing Modbot decided somebody should be told about (foundation §4.5).
/// </summary>
/// <remarks>
/// <para>
/// A row is written once and then only counted up. The same thing going wrong again inside the
/// quiet time does not write a second row: it raises <see cref="Repeats"/> and moves
/// <see cref="LastAt"/>, so the record says "this happened forty times" rather than filling the
/// table with forty copies and somebody's inbox with forty messages.
/// </para>
/// <para>
/// <strong>What makes two notifications the same is <see cref="SameAs"/></strong>, which the
/// caller decides. It is the thing that is wrong, not the moment it was noticed: the sync being
/// stopped is one <c>SameAs</c> however many times the check runs.
/// </para>
/// </remarks>
public class NotificationRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What happened, as a dotted name like a fact type. See <c>NotificationKinds</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary><see cref="NotificationSeverities"/>.</summary>
    public string Severity { get; set; } = NotificationSeverities.Information;

    /// <summary>What two notifications have to share to count as the same one.</summary>
    public string SameAs { get; set; } = string.Empty;

    /// <summary>One line. The email subject, the first line of a direct message.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>A sentence or two a person can act on.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Where in Modbot to look, as a path such as <c>/health</c>. Null when there is nowhere.</summary>
    public string? Link { get; set; }

    /// <summary>When this first happened.</summary>
    public DateTimeOffset FirstAt { get; set; }

    /// <summary>When it last happened. The same as <see cref="FirstAt"/> until it repeats.</summary>
    public DateTimeOffset LastAt { get; set; }

    /// <summary>How many times it happened again inside the quiet time. 0 the first time.</summary>
    public int Repeats { get; set; }

    /// <summary>
    /// True when this went out on no channel at all, for anybody. Only meaningful for a critical
    /// one, and the reason the sign-in banner exists: an alert nobody can receive is no alerting.
    /// </summary>
    public bool NobodyCouldReceive { get; set; }

    public ICollection<NotificationForPerson> People { get; set; } = [];
}

/// <summary>One person this notification was addressed to.</summary>
/// <remarks>
/// Written when the notification is raised, before anything is sent, so the record of who should
/// have been told survives every channel failing. This is also the in-app list.
/// </remarks>
public class NotificationForPerson
{
    public Guid NotificationId { get; set; }

    public NotificationRecord Notification { get; set; } = null!;

    public Guid UserId { get; set; }

    public ModbotUser User { get; set; } = null!;

    /// <summary>
    /// True when this is critical and reached this person on no channel. Cleared by nothing: it
    /// is what the banner at next sign-in reads.
    /// </summary>
    public bool Waiting { get; set; }

    /// <summary>When this person saw it. Null while they have not.</summary>
    public DateTimeOffset? SeenAt { get; set; }
}

/// <summary>What a channel did with one notification for one person.</summary>
public class NotificationSend
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid NotificationId { get; set; }

    public NotificationRecord Notification { get; set; } = null!;

    public Guid UserId { get; set; }

    /// <summary><c>email</c> or <c>discord</c>. See <c>NotificationChannels</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary><see cref="NotificationSendStates"/>.</summary>
    public string State { get; set; } = NotificationSendStates.Waiting;

    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When it went out. Null while it has not.</summary>
    public DateTimeOffset? SentAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>One sentence for the channel health on the settings screen.</summary>
    public string? LastError { get; set; }
}

/// <summary>Where one send has got to.</summary>
public static class NotificationSendStates
{
    /// <summary>Not tried yet.</summary>
    public const string Waiting = "waiting";

    /// <summary>Held for the daily summary rather than sent on its own.</summary>
    public const string ForSummary = "for-summary";

    /// <summary>Handed to the channel. Not proof anybody read it.</summary>
    public const string Sent = "sent";

    /// <summary>Tried enough times and given up on.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One person's setting for one channel (foundation §4.5.1's "per-user, per-channel").
/// </summary>
/// <remarks>
/// A row exists only where somebody changed something. A channel with no row uses that channel's
/// default, which is what makes the defaults deliberately quiet: nobody has to opt out of
/// anything, and an operator who wants everything by email can say so.
/// </remarks>
public class NotificationChoice
{
    public Guid UserId { get; set; }

    public ModbotUser User { get; set; } = null!;

    /// <summary>See <c>NotificationChannels</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>The least serious thing that goes out on this channel. See <c>NotificationLevels</c>.</summary>
    public string Level { get; set; } = NotificationLevels.Critical;

    /// <summary>Whether the daily summary goes out on this channel.</summary>
    public bool DailySummary { get; set; }

    /// <summary>When the last daily summary went out on this channel. Null means none has.</summary>
    public DateTimeOffset? SummarySentAt { get; set; }
}

/// <summary>How much of what happens goes out on a channel.</summary>
public static class NotificationLevels
{
    /// <summary>Nothing at all. The daily summary can still be on.</summary>
    public const string Off = "off";

    public const string Critical = "critical";

    /// <summary>Critical and warnings.</summary>
    public const string Warning = "warning";

    /// <summary>Everything, as it happens, including what would otherwise wait for the summary.</summary>
    public const string Everything = "everything";

    /// <summary>In the order the account screen lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Off, Critical, Warning, Everything];

    public static bool IsKnown(string? level) =>
        level is not null && All.Contains(level, StringComparer.Ordinal);

    public static string Label(string level) => level switch
    {
        Off => "Off",
        Critical => "Critical only",
        Warning => "Critical and warnings",
        Everything => "Everything",
        _ => level,
    };
}

/// <summary>The parts of the notification pipeline that are the same for everybody. One row.</summary>
public class NotificationSettings
{
    public const int DefaultQuietHours = 6;

    /// <summary>A week. Past this a quiet time is really "off", and turning it off is the honest control.</summary>
    public const int MaxQuietHours = 168;

    /// <summary>Always 1. Enforced by a database check constraint.</summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// How long the same thing stays quiet after it has been sent. Something that gets more
    /// serious inside the quiet time goes out anyway.
    /// </summary>
    public int QuietHours { get; set; } = DefaultQuietHours;

    /// <summary>Whether Modbot sends anything at all. Off leaves the record and sends nothing.</summary>
    public bool On { get; set; } = true;
}
