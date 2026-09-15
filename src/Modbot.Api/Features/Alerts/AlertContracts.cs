using Modbot.AI.Alerts;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Alerts;

/// <summary>One alert, with the figures behind it.</summary>
/// <param name="Counts">What the figure counts, in plain words.</param>
/// <param name="Now">The figure for the window.</param>
/// <param name="Normal">The middle of the matching earlier windows.</param>
/// <param name="Score">How many spreads from normal the window sits.</param>
/// <param name="Where">The world or room, for the two room watchers.</param>
/// <param name="Link">Where in Modbot to look, as a path.</param>
/// <param name="Text">One AI-written sentence, or null when none was written.</param>
public sealed record AlertView(
    Guid Id,
    string Watcher,
    string Label,
    string Counts,
    DateTimeOffset At,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    decimal Now,
    decimal Normal,
    decimal Spread,
    decimal Score,
    string Sensitivity,
    string? Where,
    string? Link,
    string? Text,
    string? Model,
    DateTimeOffset? DismissedAt,
    string? DismissedBy,
    DateTimeOffset? DiscordPostedAt,
    string? DiscordError)
{
    public static AlertView From(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var figures = AlertFigures.FromJson(alert.Figures);

        return new AlertView(
            alert.Id,
            alert.Watcher,
            AlertWatchers.Label(alert.Watcher),
            AlertWatchers.Counts(alert.Watcher),
            alert.At,
            alert.WindowStart,
            alert.WindowEnd,
            alert.Now,
            alert.Normal,
            alert.Spread,
            alert.Score,
            alert.Sensitivity,
            figures?.Where,
            alert.Link,
            alert.Text,
            alert.Model,
            alert.DismissedAt,
            alert.DismissedByUsername,
            alert.DiscordPostedAt,
            alert.DiscordError);
    }
}

public sealed record AlertPage(IReadOnlyList<AlertView> Alerts);

/// <param name="Sensitivity"><c>off</c>, <c>low</c>, <c>normal</c> or <c>high</c>.</param>
/// <param name="Last">The most recent alert this watcher raised, or null.</param>
public sealed record AlertWatchSettings(
    string Watcher,
    string Label,
    string Sensitivity,
    AlertView? Last);

/// <param name="QuietHours">How long the same watcher stays quiet after an alert, unless it gets much worse.</param>
/// <param name="AiOn">Whether AI is switched on. Alerts go out with their figures either way.</param>
public sealed record AlertSettingsResponse(
    string? DiscordChannelId,
    int QuietHours,
    bool WriteSentence,
    bool AiOn,
    IReadOnlyList<AlertWatchSettings> Watchers);

public sealed record AlertWatchUpdate(string Watcher, string Sensitivity);

/// <param name="Watchers">Watchers left out keep what they had.</param>
public sealed record AlertSettingsUpdate(
    string? DiscordChannelId,
    int QuietHours,
    bool WriteSentence,
    IReadOnlyList<AlertWatchUpdate>? Watchers);
