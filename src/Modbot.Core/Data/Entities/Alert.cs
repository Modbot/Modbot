namespace Modbot.Core.Data.Entities;

/// <summary>
/// One time something Modbot watches ran far outside that deployment's own normal
/// (AI insights design §8).
/// </summary>
/// <remarks>
/// An alert names counts, a stretch of time, and at most a world or instance. It never names a person
/// and never says what to do about it (M8 §2, §6), so a row holds no personal data and is outside
/// retention and purge-user, like an insight.
/// </remarks>
public class Alert
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Which watcher noticed it. See <see cref="AlertWatchers"/>.</summary>
    public string Watcher { get; set; } = string.Empty;

    /// <summary>When Modbot noticed.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The stretch of time the figure covers.</summary>
    public DateTimeOffset WindowStart { get; set; }

    public DateTimeOffset WindowEnd { get; set; }

    /// <summary>The figure for the window.</summary>
    public decimal Now { get; set; }

    /// <summary>What normal looks like: the middle of the matching earlier windows.</summary>
    public decimal Normal { get; set; }

    /// <summary>How far the earlier windows usually sat from normal.</summary>
    public decimal Spread { get; set; }

    /// <summary>How many spreads this window sits from normal. What "much worse" is measured in.</summary>
    public decimal Score { get; set; }

    /// <summary>The sensitivity the watcher was set to when it fired.</summary>
    public string Sensitivity { get; set; } = AlertSensitivities.Normal;

    /// <summary>The figures behind the alert, as JSON. Counts, worlds and instances only.</summary>
    public string Figures { get; set; } = "{}";

    /// <summary>Where in Modbot to look, as a path such as <c>/flags</c>. Null when there is nowhere.</summary>
    public string? Link { get; set; }

    /// <summary>One AI-written sentence about the figures. Null when AI is off or at its spend limit.</summary>
    public string? Text { get; set; }

    public string? Model { get; set; }

    public string? Provider { get; set; }

    /// <summary>When somebody hid the card. The alert itself is kept.</summary>
    public DateTimeOffset? DismissedAt { get; set; }

    public Guid? DismissedByUserId { get; set; }

    public string? DismissedByUsername { get; set; }

    /// <summary>The Discord channel to post to, copied from the settings when the alert was written.</summary>
    public string? DiscordChannelId { get; set; }

    public DateTimeOffset? DiscordPostedAt { get; set; }

    /// <summary>Set when posting failed for good; the poster does not try again.</summary>
    public string? DiscordError { get; set; }
}

/// <summary>One watcher's settings. One row per watcher.</summary>
public class AlertWatch
{
    public string Watcher { get; set; } = string.Empty;

    /// <summary><c>off</c>, <c>low</c>, <c>normal</c> or <c>high</c>. See <see cref="AlertSensitivities"/>.</summary>
    public string Sensitivity { get; set; } = AlertSensitivities.Off;

    /// <summary>
    /// The last window this watcher was checked for, so the same window is not alerted on twice
    /// while the checks run more often than a window is long.
    /// </summary>
    public DateTimeOffset? CheckedThrough { get; set; }
}

/// <summary>Settings → AI → Alerts, the parts shared by every watcher. One row.</summary>
public class AlertSettings
{
    /// <summary>Always 1. Enforced by a database check constraint.</summary>
    public int Id { get; set; } = 1;

    /// <summary>Where alerts are posted. Null posts nowhere.</summary>
    public string? DiscordChannelId { get; set; }

    /// <summary>
    /// How long the same watcher stays quiet after an alert, in hours, unless it gets much worse.
    /// </summary>
    public int QuietHours { get; set; } = AlertWatchers.DefaultQuietHours;

    /// <summary>
    /// Whether an AI sentence is added to each alert. The figures go out either way.
    /// </summary>
    public bool WriteSentence { get; set; } = true;

    /// <summary>
    /// When the checks last ran. Kept in the database so a restart does not start the 15 minutes
    /// over and a second copy of Modbot does not double the work.
    /// </summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>The four settings a watcher can be on.</summary>
public static class AlertSensitivities
{
    public const string Off = "off";
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";

    /// <summary>In the order the settings page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Off, Low, Normal, High];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    public static string Label(string value) => value switch
    {
        Off => "Off",
        Low => "Low",
        Normal => "Normal",
        High => "High",
        _ => value,
    };
}

/// <summary>
/// The things Modbot watches for unusual activity, and their labels (AI insights design §8.1).
/// </summary>
/// <remarks>
/// In Core rather than Modbot.AI because the Discord poster names the watcher in its card title
/// and Modbot.Discord does not reference Modbot.AI.
/// </remarks>
public static class AlertWatchers
{
    /// <summary>People joining the VRChat group.</summary>
    public const string VRChatJoins = "vrchat-joins";

    /// <summary>People joining the Discord server.</summary>
    public const string DiscordJoins = "discord-joins";

    /// <summary>Of the people joining the group, those whose VRChat account is less than a month old.</summary>
    public const string NewAccounts = "new-accounts";

    /// <summary>Flags the AI moderation rules raised.</summary>
    public const string Flags = "flags";

    /// <summary>Bans, removals, kicks, warnings, rejections and Discord timeouts together.</summary>
    public const string Actions = "actions";

    /// <summary>People leaving the group or the Discord server.</summary>
    public const string Leaves = "leaves";

    /// <summary>Group instances opening.</summary>
    public const string InstancesOpened = "instances-opened";

    /// <summary>One open instance holding far more people than instances here usually hold.</summary>
    public const string InstanceFilling = "instance-filling";

    /// <summary>A busy open instance with no moderator's client in it.</summary>
    public const string InstanceUnwatched = "instance-unwatched";

    /// <summary>Fewer active members this week than in the four weeks before.</summary>
    public const string ActiveDrop = "active-drop";

    /// <summary>Hours the same watcher stays quiet after an alert, unless it gets much worse.</summary>
    public const int DefaultQuietHours = 6;

    public const int MaxQuietHours = 168;

    /// <summary>In the order the settings page lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        VRChatJoins,
        DiscordJoins,
        NewAccounts,
        Flags,
        Actions,
        Leaves,
        InstancesOpened,
        InstanceFilling,
        InstanceUnwatched,
        ActiveDrop,
    ];

    public static bool IsKnown(string? watcher) => watcher is not null && All.Contains(watcher, StringComparer.Ordinal);

    public static string Label(string watcher) => watcher switch
    {
        VRChatJoins => "People joining the group",
        DiscordJoins => "People joining Discord",
        NewAccounts => "New accounts joining",
        Flags => "AI flags",
        Actions => "Moderation actions",
        Leaves => "People leaving",
        InstancesOpened => "Instances opening",
        InstanceFilling => "An instance filling up",
        InstanceUnwatched => "Nobody watching a busy instance",
        ActiveDrop => "Fewer active members",
        _ => watcher,
    };

    /// <summary>What the figure counts, for the card and the Discord post.</summary>
    public static string Counts(string watcher) => watcher switch
    {
        VRChatJoins => "joins",
        DiscordJoins => "Discord joins",
        NewAccounts => "accounts less than a month old",
        Flags => "flags",
        Actions => "actions",
        Leaves => "leaves",
        InstancesOpened => "instances opened",
        InstanceFilling => "people in the instance",
        InstanceUnwatched => "people in the instance",
        ActiveDrop => "active members",
        _ => "",
    };
}
