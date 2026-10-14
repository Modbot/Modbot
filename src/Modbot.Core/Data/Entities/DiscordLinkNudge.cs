namespace Modbot.Core.Data.Entities;

/// <summary>What became of the link reminder sent to one member, as stored in <see cref="DiscordLinkNudge.State"/>.</summary>
public static class DiscordLinkNudgeStates
{
    /// <summary>Claimed, and the message is on its way. Never tried again, whatever came of it.</summary>
    public const string Sending = "sending";

    public const string Sent = "sent";

    /// <summary>Discord refused: their direct messages are closed, or the account is gone.</summary>
    public const string NotReached = "not-reached";
}

/// <summary>
/// The one link reminder a Discord member is ever sent (account links). The table is
/// <c>discord_link_nudge</c>.
/// </summary>
/// <remarks>
/// A row is written before the message goes, and stays: it is what makes a reminder go out once per
/// person, ever, however many times staff start the job and whatever Discord answered. A purge
/// deletes the row with the rest of what Modbot keeps about the person.
/// </remarks>
public class DiscordLinkNudge
{
    /// <summary>The Discord account. Text, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>One of <see cref="DiscordLinkNudgeStates"/>.</summary>
    public string State { get; set; } = DiscordLinkNudgeStates.Sending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Why Discord refused, as a sentence. Null otherwise.</summary>
    public string? Error { get; set; }
}
