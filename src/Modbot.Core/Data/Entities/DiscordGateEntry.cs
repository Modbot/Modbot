namespace Modbot.Core.Data.Entities;

/// <summary>What the join gate is doing (join gate design §3).</summary>
public static class DiscordGateModes
{
    public const string Off = "off";

    /// <summary>Records who would be warned, removed or let in, and does nothing in Discord.</summary>
    public const string Watch = "watch";

    public const string On = "on";

    public static bool IsKnown(string? value) => value is Off or Watch or On;
}

/// <summary>How somebody's time at the gate ended.</summary>
public static class DiscordGateOutcomes
{
    /// <summary>They did the steps and Modbot gave them the member role.</summary>
    public const string Passed = "passed";

    /// <summary>A moderator let them in from Modbot.</summary>
    public const string LetIn = "let-in";

    /// <summary>They got the member role from somebody else: a moderator in Discord, or another bot.</summary>
    public const string LetInInDiscord = "let-in-in-discord";

    /// <summary>Removed for not finishing, or by a moderator from Modbot.</summary>
    public const string Removed = "removed";

    /// <summary>They left the server by themselves.</summary>
    public const string Left = "left";

    /// <summary>The gate was turned off or changed mode while they were waiting.</summary>
    public const string GateChanged = "gate-changed";
}

/// <summary>
/// One person's time at the join gate, from joining to getting in, leaving or being removed (join
/// gate design §11). A person who comes back gets a new row; at most one row per person is open.
/// </summary>
public class DiscordGateEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string GuildId { get; set; } = string.Empty;

    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>Their Discord username when they joined. Untrusted text.</summary>
    public string DiscordUsername { get; set; } = string.Empty;

    /// <summary>When they joined, or when the gate first saw them.</summary>
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>Made while the gate was Watch only: nothing is done in Discord for this row.</summary>
    public bool WatchOnly { get; set; }

    /// <summary>When they pressed I agree.</summary>
    public DateTimeOffset? AgreedAt { get; set; }

    /// <summary>
    /// The minutes counted against them: only time while the hold-up was theirs (join gate design
    /// §6). Removal is when this reaches the removal time.
    /// </summary>
    public int MinutesCounted { get; set; }

    /// <summary>When the last pass looked at this row, so the next one knows how much time went by.</summary>
    public DateTimeOffset? LastCountedAt { get; set; }

    /// <summary>When they were warned (or, in Watch only, would have been).</summary>
    public DateTimeOffset? WarnedAt { get; set; }

    /// <summary>When they would have been removed, in Watch only. The row stays open.</summary>
    public DateTimeOffset? WouldRemoveAt { get; set; }

    /// <summary>When it ended. Null while they are still at the gate.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>One of <see cref="DiscordGateOutcomes"/>, once closed.</summary>
    public string? Outcome { get; set; }

    /// <summary>The Modbot account that let them in or removed them, when a person did.</summary>
    public Guid? ClosedByUserId { get; set; }

    /// <summary>The last thing that went wrong for this row, in a sentence.</summary>
    public string? Problem { get; set; }
}
