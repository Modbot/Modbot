namespace Modbot.Core.Data.Entities;

/// <summary>How often Modbot saves a snapshot of the Discord server by itself.</summary>
public static class DiscordSnapshotSchedules
{
    public const string Off = "off";
    public const string Weekly = "weekly";
    public const string EveryTwoWeeks = "every-two-weeks";
    public const string Monthly = "monthly";

    public static bool IsKnown(string? schedule)
        => schedule is Off or Weekly or EveryTwoWeeks or Monthly;

    /// <summary>When the next snapshot is due after one taken at <paramref name="last"/>; null while the schedule is off.</summary>
    public static DateTimeOffset? NextAfter(string schedule, DateTimeOffset last) => schedule switch
    {
        Weekly => last.AddDays(7),
        EveryTwoWeeks => last.AddDays(14),
        Monthly => last.AddMonths(1),
        _ => null,
    };
}

/// <summary>What snapshots are limited to, in one place for the page, the save and the schedule.</summary>
public static class DiscordSnapshotLimits
{
    /// <summary>The most snapshots kept. A new one by hand is refused past this; staff delete one first.</summary>
    public const int MostKept = 100;

    /// <summary>The most snapshots the schedule keeps. Once it has taken more, the oldest scheduled ones go.</summary>
    public const int MostScheduledKept = 24;
}

/// <summary>
/// The Discord server's roles and channels with their permissions, as they were at one moment. The
/// table is <c>discord_snapshot</c>.
/// </summary>
/// <remarks>
/// <see cref="Content"/> is a <c>ServerSnapshotContent</c> as JSON. Nothing in it is about a person:
/// channel settings for single members are not kept.
/// </remarks>
public class DiscordSnapshot
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset TakenAt { get; set; }

    /// <summary>Taken by the schedule. False when somebody pressed the button.</summary>
    public bool Scheduled { get; set; }

    /// <summary>The Modbot account that took it by hand, as it was called then; null for the schedule.</summary>
    public string? TakenBy { get; set; }

    public int RoleCount { get; set; }

    public int ChannelCount { get; set; }

    /// <summary>The snapshot itself, as JSON.</summary>
    public string Content { get; set; } = "{}";
}
