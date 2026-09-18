using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Alerts;

/// <param name="Posted">Alerts sent this pass.</param>
/// <param name="Error">What went wrong with the first one that was not sent, if any.</param>
public sealed record AlertPostPass(int Posted, string? Error);

/// <summary>
/// Posts unusual-activity alerts to the Discord channel chosen for them (AI insights design §8.4).
/// </summary>
/// <remarks>
/// <para>
/// Reads the alert rows rather than being called by the checker, the same way insights are posted:
/// the checker lives in Modbot.AI and knows nothing of Discord, and an alert raised while the bot
/// is offline is posted when it comes back.
/// </para>
/// <para>
/// A channel that is gone or not allowed is recorded on the row and not tried again. Anything else
/// is tried again next pass for up to <see cref="GiveUpAfter"/>, after which an alert about an hour
/// last night is not worth posting.
/// </para>
/// </remarks>
public sealed class AlertPoster
{
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(6);

    public const int PerPass = 10;

    /// <summary>The same amber the health screens use for "worth a look".</summary>
    private const uint Amber = CardColour.Amber;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public AlertPoster(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<AlertPostPass> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var waiting = await _db.Alerts
            .Where(a => a.DiscordChannelId != null && a.DiscordPostedAt == null && a.DiscordError == null)
            .OrderBy(a => a.At)
            .Take(PerPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (waiting.Count == 0)
            return new AlertPostPass(0, null);

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.PublicAddress, s.ManagedGroupName })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var style = new CardStyle(
            settings?.PublicAddress,
            settings?.ManagedGroupName,
            BrandIcon.For(settings?.PublicAddress));

        var posted = 0;
        string? error = null;
        var now = _clock.UtcNow;

        foreach (var alert in waiting)
        {
            if (now - alert.At > GiveUpAfter)
            {
                alert.DiscordError = "Not posted within six hours.";
                continue;
            }

            var outcome = await gateway.PostAsync(alert.DiscordChannelId!, [Card(alert, style)], ct).ConfigureAwait(false);

            if (outcome.Sent)
            {
                alert.DiscordPostedAt = _clock.UtcNow;
                posted++;
                continue;
            }

            error = outcome.Error ?? "Discord refused the message.";
            _log.Warning("Could not post an alert to Discord: {Reason}", error);

            if (outcome.Permanent)
            {
                alert.DiscordError = error.Length <= 1000 ? error : error[..1000];
                continue;
            }

            // Discord is having a moment; the rest can wait for the next pass too.
            break;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AlertPostPass(posted, error);
    }

    public static DiscordEmbedContent Card(Alert alert, string? publicAddress)
        => Card(alert, new CardStyle(publicAddress, FooterIconUrl: BrandIcon.For(publicAddress)));

    /// <summary>The card: the figure, what normal is, the stretch of time, and where to look.</summary>
    /// <remarks>
    /// No picture and no author line. An alert is about a number, not about a person or a place,
    /// and the one thing a moderator does with it is open the page it points at.
    /// </remarks>
    public static DiscordEmbedContent Card(Alert alert, CardStyle style)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(style);

        var counts = AlertWatchers.Counts(alert.Watcher);
        var fields = new List<DiscordEmbedField>
        {
            new("Now", Number(alert.Now) + (counts.Length > 0 ? $" {counts}" : ""), Inline: true),
            new("Normally", Number(alert.Normal), Inline: true),
            new("Window", Window(alert), Inline: true),
        };

        return new DiscordEmbedContent(
            Title: CardText.Plain(AlertWatchers.Label(alert.Watcher), 256),
            Description: alert.Text,
            Color: Amber,
            Fields: fields,
            Timestamp: alert.At,
            Url: LinkTo(alert, style.PublicAddress),
            Footer: alert.Model is null ? "Unusual activity" : $"Unusual activity · {alert.Model}",
            FooterIconUrl: style.FooterIconUrl);
    }

    /// <summary>The alert's page in Modbot, when the deployment's public address is set.</summary>
    public static string? LinkTo(Alert alert, string? publicAddress)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (alert.Link is not { Length: > 0 } path || string.IsNullOrWhiteSpace(publicAddress))
            return null;

        return Uri.TryCreate(new Uri(publicAddress.TrimEnd('/') + "/"), path.TrimStart('/'), out var url)
            && url.Scheme is "https" or "http"
            ? url.ToString()
            : null;
    }

    private static string Window(Alert alert)
    {
        var span = alert.WindowEnd - alert.WindowStart;

        return span >= TimeSpan.FromDays(1)
            ? $"{Day(alert.WindowStart)} to {Day(alert.WindowEnd)}"
            : $"{alert.WindowStart.UtcDateTime:HH:mm} to {alert.WindowEnd.UtcDateTime:HH:mm} UTC";
    }

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM", CultureInfo.InvariantCulture);

    private static string Number(decimal value)
        => value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
}
