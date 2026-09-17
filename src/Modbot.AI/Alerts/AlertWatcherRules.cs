using Modbot.Core.Data.Entities;

namespace Modbot.AI.Alerts;

/// <summary>The stretch of time a pass is judging, and the days behind it.</summary>
/// <param name="End">
/// The end of the window: now, rounded down to the quarter-hour so that this window and the same
/// window on earlier days line up exactly.
/// </param>
/// <param name="Start">An hour before <paramref name="End"/>.</param>
/// <param name="LastWholeDay">Yesterday, in UTC days -- the last day the daily totals can be complete for.</param>
public sealed record AlertWindows(DateTimeOffset End, DateTimeOffset Start, DateOnly LastWholeDay)
{
    /// <summary>How far apart the checks line up. Every window ends on one of these.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    public static AlertWindows At(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var end = new DateTimeOffset(utc.Ticks - utc.Ticks % Step.Ticks, TimeSpan.Zero);

        return new AlertWindows(
            end,
            end - AlertFigureReader.WindowLength,
            DateOnly.FromDateTime(end.UtcDateTime).AddDays(-1));
    }
}

/// <summary>
/// What each watcher counts, and how big the figure has to be before it is worth saying anything
/// (AI insights design §8.1).
/// </summary>
/// <param name="Minimum">
/// The smallest figure that can ever raise an alert. Set from what a volunteer would shrug at: two
/// extra joins in an hour is not news in any group, however quiet.
/// </param>
/// <param name="Types">The fact types the figure counts. Empty for the watchers that read elsewhere.</param>
public sealed record AlertWatcherRule(
    string Watcher,
    int Minimum,
    IReadOnlyList<string> Types,
    AlertDirection Direction = AlertDirection.Above,
    int LeastEarlier = UnusualRule.LeastEarlierWindows);

/// <summary>Every watcher's rule, and the fact types a pass has to count between them.</summary>
public static class AlertWatcherRules
{
    /// <summary>The moderation actions counted together: what a moderator did, whatever the platform.</summary>
    public static readonly string[] ActionTypes =
    [
        FactType.MemberBanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberKicked,
        FactType.DiscordMemberTimedOut,
    ];

    public static IReadOnlyList<AlertWatcherRule> All { get; } =
    [
        new(AlertWatchers.VRChatJoins, 5, [FactType.MemberJoined]),
        new(AlertWatchers.DiscordJoins, 5, [FactType.DiscordMemberJoined]),
        new(AlertWatchers.NewAccounts, 3, []),
        new(AlertWatchers.Flags, 3, [FactType.AiModerationFlag]),
        new(AlertWatchers.Actions, 3, ActionTypes),
        new(AlertWatchers.Leaves, 5, [FactType.MemberLeft, FactType.DiscordMemberLeft]),
        new(AlertWatchers.InstancesOpened, 3, [FactType.GroupInstanceCreated]),
        new(AlertWatchers.InstanceFilling, 8, []),
        new(AlertWatchers.InstanceUnwatched, 8, []),
        // Four earlier weeks is all there ever is, so the weekly watcher wants three of them rather
        // than the five an hourly watcher wants.
        new(AlertWatchers.ActiveDrop, 20, [], AlertDirection.Below, LeastEarlier: 3),
    ];

    public static AlertWatcherRule For(string watcher)
        => All.FirstOrDefault(r => r.Watcher == watcher)
           ?? throw new ArgumentOutOfRangeException(nameof(watcher), watcher, "Not a watcher.");

    /// <summary>The watchers that read the open instances.</summary>
    public static IReadOnlySet<string> InstanceWatchers { get; } =
        new HashSet<string>(StringComparer.Ordinal) { AlertWatchers.InstanceFilling, AlertWatchers.InstanceUnwatched };

    /// <summary>Where in Modbot to look, as a path the web app knows.</summary>
    public static string? LinkFor(string watcher, AlertWindows windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        // The members list takes the window as a filter, so "those joiners" is one click rather
        // than a page of everybody sorted by join date.
        var joined = $"/?joinedFrom={Uri.EscapeDataString(windows.Start.ToUniversalTime().ToString("o"))}"
            + $"&joinedTo={Uri.EscapeDataString(windows.End.ToUniversalTime().ToString("o"))}";

        return watcher switch
        {
            AlertWatchers.VRChatJoins or AlertWatchers.NewAccounts => joined,
            AlertWatchers.DiscordJoins => "/discord/members",
            AlertWatchers.Flags => "/flags",
            AlertWatchers.Actions => "/audit",
            AlertWatchers.Leaves => "/analytics/group",
            AlertWatchers.InstancesOpened => "/analytics/instances",
            AlertWatchers.InstanceFilling or AlertWatchers.InstanceUnwatched => "/live",
            AlertWatchers.ActiveDrop => "/analytics/server",
            _ => null,
        };
    }
}
