namespace Modbot.Core.Data.Entities;

/// <summary>The words stored in <see cref="MemberReport.State"/> (Discord commands design §3.4).</summary>
public static class MemberReportStates
{
    /// <summary>Waiting for a moderator to look. The only state that can be closed.</summary>
    public const string Open = "open";

    /// <summary>A moderator closed it, with a note.</summary>
    public const string Closed = "closed";

    public static bool IsKnown(string? state) => state is Open or Closed;
}

/// <summary>
/// What a member told the mods with <c>/report</c> or the "Report to mods" message menu in Discord.
/// The table is <c>member_report</c> (Discord commands design §3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A member's question to the mods, not a note by staff.</strong> It has open and closed
/// states and a page of its own (Reports). Everyone who may see reports sees who reported
/// (decision 2); Discord, notifications and the audit log never do, so the reporter's id and name
/// exist in this row and nowhere else.
/// </para>
/// <para>
/// <strong>Kept until the retention setting, then reduced.</strong> A closed report keeps what was
/// written and the copy of the message for <see cref="Settings.MemberReportRetentionDays"/> days
/// from the close (decision 10; 0 keeps them forever). After that <see cref="Text"/> and every
/// <c>Message…</c> column are removed, <see cref="TextRemovedAt"/> is set, and the bare record stays:
/// who, about whom, when, and how it was closed. A purge of a person deletes the row whether they
/// reported or were reported.
/// </para>
/// <para>
/// The ids are opaque text, never parsed or validated (foundation §3.1.1). Names are untrusted text
/// as the reporter's or the member's client showed them at the time.
/// </para>
/// </remarks>
public class MemberReport
{
    /// <summary>Fewest and most characters a member may write.</summary>
    public const int MinTextLength = 1;

    public const int MaxTextLength = 1000;

    /// <summary>The most characters of the quoted message kept. Discord's own limit for a message is 4,000.</summary>
    public const int MaxMessageLength = 4000;

    /// <summary>How long a close note may be, the same as a review's.</summary>
    public const int MaxCloseNoteLength = 2000;

    /// <summary>How long a closed report keeps its words when nothing else is set: a year.</summary>
    public const int DefaultRetentionDays = 365;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The Discord account that reported, and their name then. Seen only on the Reports page.</summary>
    public string ReporterDiscordId { get; set; } = string.Empty;

    public string ReporterName { get; set; } = string.Empty;

    /// <summary>The Discord account reported: the member named, or the author of the message.</summary>
    public string ReportedDiscordId { get; set; } = string.Empty;

    /// <summary>Their name when Modbot last saw the member, or null when it never had.</summary>
    public string? ReportedName { get; set; }

    /// <summary>The VRChat account linked to the reported Discord account at the time, if any.</summary>
    public string? ReportedVRChatUserId { get; set; }

    /// <summary>What the reporter wrote, 1 to 1,000 characters. Null once removed.</summary>
    public string? Text { get; set; }

    // ── The message copy: only for a report made from the message menu. Removed with the text. ──

    public string? MessageId { get; set; }

    public string? MessageChannelId { get; set; }

    public string? MessageChannelName { get; set; }

    public DateTimeOffset? MessageSentAt { get; set; }

    /// <summary>The message's words as Discord handed them over.</summary>
    public string? MessageText { get; set; }

    /// <summary>
    /// The names of the files attached, and nothing else about them: Discord's file links expire,
    /// and Modbot does not keep the files.
    /// </summary>
    public List<string>? MessageAttachments { get; set; }

    /// <summary>The message's own link, which opens it in Discord.</summary>
    public string? MessageUrl { get; set; }

    /// <summary>One of <see cref="MemberReportStates"/>.</summary>
    public string State { get; set; } = MemberReportStates.Open;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedByUserId { get; set; }

    public string? ClosedByUsername { get; set; }

    /// <summary>What the person who closed it concluded, in their words. Required to close.</summary>
    public string? CloseNote { get; set; }

    /// <summary>When the text and the message copy were removed by retention, or null while they are kept.</summary>
    public DateTimeOffset? TextRemovedAt { get; set; }
}
