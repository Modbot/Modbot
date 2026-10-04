using System.Text;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Announcements;

/// <summary>
/// The rules a message to everyone in an instance follows: what its words may be, and how late is
/// too late. Pure; the clock is passed in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The words are someone's typing and go out in the group's name</strong>, so they are
/// made plain before anything else: a line break or a tab becomes a space, any other control
/// character is dropped, and the ends are trimmed. VRChat's own limits are not known (the title is
/// under 300 characters and the message under 3000), so Modbot keeps to far less:
/// <see cref="VRChatAnnouncement.MaxTitleLength"/> and <see cref="VRChatAnnouncement.MaxMessageLength"/>.
/// </para>
/// <para>
/// The late rule is the scheduled posts' own (<see cref="PostRules.LateLimit"/>): one due more than
/// an hour ago is not sent.
/// </para>
/// </remarks>
public static class AnnouncementRules
{
    /// <summary>A scheduled announcement due longer ago than this is not sent: it turns Failed.</summary>
    public static readonly TimeSpan LateLimit = PostRules.LateLimit;

    /// <summary>A time picked a moment ago is still "now" by the time it is saved.</summary>
    public static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A row left Sending longer than this was cut off mid-send (the process stopped). It may have
    /// gone, so it turns Failed and is never sent again.
    /// </summary>
    public static readonly TimeSpan StuckSendingAfter = TimeSpan.FromMinutes(2);

    public const string NotSentOnTime = "Not sent on time.";
    public const string InstanceClosed = "The instance has closed.";
    public const string NotOurGroup = "That instance is not in Modbot's VRChat group.";
    public const string CutOff = "Modbot stopped while sending it. It may have gone out.";

    /// <summary>The words as they go out: one line, no control characters, trimmed.</summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var plain = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t')
            {
                // One space for a line break, however it was written.
                if (plain.Length > 0 && plain[^1] != ' ')
                    plain.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                plain.Append(c);
            }
        }

        return plain.ToString().Trim();
    }

    /// <summary>Every problem with a title and a message, already tidied, one sentence each.</summary>
    public static List<string> Problems(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        var problems = new List<string>(2);

        if (title.Length == 0)
            problems.Add("Write a title.");
        else if (title.Length > VRChatAnnouncement.MaxTitleLength)
            problems.Add($"The title is longer than {VRChatAnnouncement.MaxTitleLength} characters.");

        if (message.Length == 0)
            problems.Add("Write a message.");
        else if (message.Length > VRChatAnnouncement.MaxMessageLength)
            problems.Add($"The message is longer than {VRChatAnnouncement.MaxMessageLength} characters.");

        return problems;
    }

    /// <summary>A scheduled announcement whose time has come.</summary>
    public static bool IsDue(VRChatAnnouncement announcement, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        return announcement.State == VRChatAnnouncementStates.Scheduled && announcement.SendAt <= now;
    }

    /// <summary>Due, and more than <see cref="LateLimit"/> ago: it is not sent, and turns Failed.</summary>
    public static bool IsLate(VRChatAnnouncement announcement, DateTimeOffset now) =>
        IsDue(announcement, now) && now - announcement.SendAt > LateLimit;

    /// <summary>Why it cannot be cancelled now, or null when it can.</summary>
    public static string? CannotCancel(VRChatAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);

        return announcement.State switch
        {
            VRChatAnnouncementStates.Scheduled => null,
            VRChatAnnouncementStates.Sending => "It is being sent.",
            VRChatAnnouncementStates.Cancelled => "It was cancelled already.",
            _ => "It has already gone, or failed.",
        };
    }
}
