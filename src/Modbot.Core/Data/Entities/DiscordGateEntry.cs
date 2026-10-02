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

    /// <summary>Discord refused the removal (the person outranks the bot, or Kick Members is missing).</summary>
    public const string CannotRemove = "cannot-remove";

    /// <summary>Not somebody the gate holds: the server's owner, a bot, or staff.</summary>
    public const string NotGated = "not-gated";
}

/// <summary>
/// The join gate's timing (join gate design §6), in one place for the pass that removes people and
/// the list that says when it will.
/// </summary>
public static class DiscordGateTimes
{
    public const int ShortestWarningMinutes = 5;

    public const int LongestWarningMinutes = 10;

    /// <summary>
    /// The least time between a delivered warning and the removal: half the removal time, never
    /// less than <see cref="ShortestWarningMinutes"/> nor more than <see cref="LongestWarningMinutes"/>.
    /// </summary>
    public static TimeSpan WarningWindow(int removeAfterMinutes)
        => TimeSpan.FromMinutes(Math.Clamp(removeAfterMinutes / 2, ShortestWarningMinutes, LongestWarningMinutes));

    /// <summary>Whether the warning is due: half the removal time has counted.</summary>
    public static bool WarningDue(int minutesCounted, int removeAfterMinutes) => minutesCounted * 2 >= removeAfterMinutes;

    /// <summary>
    /// The earliest somebody can be removed if nothing changes and their time keeps counting: when
    /// their time runs out, and never before the warning window has passed since the warning reached
    /// them. A warning not delivered yet is taken as going out when it falls due, or now when it is
    /// overdue, so the answer is never sooner than a full window away. Null when they are never
    /// removed: Watch only, or Remove after set to Never.
    /// </summary>
    public static DateTimeOffset? EarliestRemoval(DiscordGateEntry entry, int? removeAfterMinutes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.WatchOnly || removeAfterMinutes is not { } removeAfter)
            return null;

        var timeRunsOut = now.AddMinutes(Math.Max(0, removeAfter - entry.MinutesCounted));

        var warning = entry.WarnedAt
                      ?? now.AddMinutes(Math.Max(0, (removeAfter + 1) / 2 - entry.MinutesCounted));

        var afterWarning = warning + WarningWindow(removeAfter);

        return timeRunsOut > afterWarning ? timeRunsOut : afterWarning;
    }
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

    /// <summary>
    /// Whether Discord said they had not accepted the server's rules yet, as the join showed it. Used
    /// only while the stored member list has no row for them.
    /// </summary>
    public bool Pending { get; set; }

    /// <summary>When they pressed I agree.</summary>
    public DateTimeOffset? AgreedAt { get; set; }

    /// <summary>
    /// The minutes counted against them: only time while the hold-up was theirs (join gate design
    /// §6). Removal is when this reaches the removal time.
    /// </summary>
    public int MinutesCounted { get; set; }

    /// <summary>When the last pass looked at this row, so the next one knows how much time went by.</summary>
    public DateTimeOffset? LastCountedAt { get; set; }

    /// <summary>
    /// When the warning reached them: a direct message that was sent, or, with their DMs closed, a
    /// mention in the gate channel that was posted. In Watch only, when it would have been sent.
    /// Null until then: a warning that could not be delivered is tried again, and nobody is removed
    /// before it is.
    /// </summary>
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
