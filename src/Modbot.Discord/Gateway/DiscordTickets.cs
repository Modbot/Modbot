namespace Modbot.Discord.Gateway;

/// <summary>A member's private thread with staff, to be made.</summary>
/// <param name="ChannelId">The channel the thread sits under: the one the panel is in.</param>
/// <param name="ThreadName">Discord ends a thread's name at 100 characters.</param>
/// <param name="MemberId">The member who is added.</param>
/// <param name="StaffRoleId">The role that is asked in, or null for none.</param>
/// <param name="Text">The first message, under the mentions of the member and the role.</param>
/// <param name="Actions">The buttons under the first message.</param>
public sealed record DiscordTicketOpening(
    string ChannelId,
    string ThreadName,
    string MemberId,
    string? StaffRoleId,
    string Text,
    IReadOnlyList<DiscordActionButton> Actions);

/// <summary>Whether a ticket's thread was made, and if not, why.</summary>
/// <param name="ThreadId">The new thread's id when it was made.</param>
/// <param name="Error">What went wrong, as a sentence, naming the permission when that is why.</param>
public sealed record DiscordTicketOutcome(bool Opened, string? ThreadId, string? Error)
{
    public static DiscordTicketOutcome Made(string threadId) => new(true, threadId, null);

    public static DiscordTicketOutcome Failed(string error) => new(false, null, error);
}
