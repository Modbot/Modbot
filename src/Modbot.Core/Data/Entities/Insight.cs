namespace Modbot.Core.Data.Entities;

/// <summary>
/// A short piece of writing by the AI model about Modbot's own figures for a stretch of days
/// (AI insights design §1).
/// </summary>
/// <remarks>
/// Stored with the exact figures the model was given, so every sentence can be checked against
/// them. The figures are counts and world names only -- never a person -- so a row holds no personal
/// data and is outside retention and purge-user (design §5).
/// </remarks>
public class Insight
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary><c>group</c>, <c>team</c> or <c>instances</c>. See <c>InsightKinds</c> in Modbot.AI.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The first UTC day the figures cover.</summary>
    public DateOnly FirstDay { get; set; }

    /// <summary>The last UTC day the figures cover, inclusive.</summary>
    public DateOnly LastDay { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><c>schedule</c> or <c>button</c>.</summary>
    public string StartedBy { get; set; } = string.Empty;

    /// <summary>Who pressed Generate now, when somebody did.</summary>
    public Guid? RequestedByUserId { get; set; }

    public string? RequestedByUsername { get; set; }

    public string? Model { get; set; }

    public string? Provider { get; set; }

    /// <summary>The figures the model was given, as JSON.</summary>
    public string Figures { get; set; } = "{}";

    /// <summary>What the model wrote. Null when the attempt failed.</summary>
    public string? Text { get; set; }

    /// <summary>Why the attempt failed, in the provider's words where it gave any.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The Discord channel to post to, copied from the schedule when the insight was written, so
    /// changing the schedule later does not move an insight that is still waiting to be posted.
    /// </summary>
    public string? DiscordChannelId { get; set; }

    public DateTimeOffset? DiscordPostedAt { get; set; }

    /// <summary>Set when posting failed for good; the poster does not try again.</summary>
    public string? DiscordError { get; set; }
}

/// <summary>Settings → AI → Insights, the parts shared by every kind. One row.</summary>
public class InsightSettings
{
    /// <summary>Always 1. Enforced by a database check constraint.</summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// An IANA time zone name such as <c>Europe/London</c>. Only decides when a scheduled insight is
    /// written; the figures are always whole UTC days. Null means UTC.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>The model insights use. Null means the Base model.</summary>
    public string? Model { get; set; }
}

/// <summary>When one kind of insight is written, and where it goes. One row per kind.</summary>
public class InsightSchedule
{
    public string Kind { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary><c>day</c> or <c>week</c>.</summary>
    public string Every { get; set; } = "week";

    /// <summary>The hour of the day, 0 to 23, in <see cref="InsightSettings.TimeZone"/>.</summary>
    public int Hour { get; set; } = 9;

    /// <summary>For a weekly schedule: 0 is Sunday, as <see cref="DayOfWeek"/> counts.</summary>
    public int Weekday { get; set; } = 1;

    public string? DiscordChannelId { get; set; }

    /// <summary>
    /// The latest scheduled moment already dealt with -- written, failed, or passed over because
    /// the schedule was saved after it. A moment at or before this is never written again.
    /// </summary>
    public DateTimeOffset? HandledThrough { get; set; }
}

/// <summary>The kinds of insight, their labels, and the two schedule lengths (AI insights design §1).</summary>
/// <remarks>
/// In Core rather than Modbot.AI because the Discord poster names the kind in its card title and
/// Modbot.Discord does not reference Modbot.AI.
/// </remarks>
public static class InsightKinds
{
    public const string Group = "group";
    public const string Team = "team";
    public const string Instances = "instances";

    public const string EveryDay = "day";
    public const string EveryWeek = "week";

    public const string StartedBySchedule = "schedule";
    public const string StartedByButton = "button";

    /// <summary>In the order the settings page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Group, Team, Instances];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    public static string Label(string kind) => kind switch
    {
        Group => "Group",
        Team => "Moderation team",
        Instances => "Instances",
        _ => kind,
    };

    /// <summary>How many UTC days one insight covers.</summary>
    public static int Days(string every) => every == EveryDay ? 1 : 7;
}
