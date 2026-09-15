using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Insights;

/// <param name="Posted">Insights sent this pass.</param>
/// <param name="Error">What went wrong with the first one that was not sent, if any.</param>
public sealed record InsightPostPass(int Posted, string? Error);

/// <summary>
/// Posts scheduled AI insights to the Discord channel their schedule named (AI insights design §4).
/// </summary>
/// <remarks>
/// <para>
/// Reads the insight rows rather than being called by the writer, the same way the moderation log
/// reads the fact log: the writer lives in Modbot.AI and knows nothing of Discord, and an insight
/// written while the bot is offline is posted when it comes back.
/// </para>
/// <para>
/// A channel that is gone or not allowed is recorded on the row and not tried again. Anything else
/// is tried again on the next pass for up to <see cref="GiveUpAfter"/>; after that a summary of
/// last week is not news.
/// </para>
/// </remarks>
public sealed class InsightPoster
{
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);

    public const int PerPass = 10;

    private const uint Purple = 0x8E44AD;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public InsightPoster(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<InsightPostPass> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var waiting = await _db.Insights
            .Where(i => i.DiscordChannelId != null && i.DiscordPostedAt == null && i.DiscordError == null && i.Text != null)
            .OrderBy(i => i.CreatedAt)
            .Take(PerPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var posted = 0;
        string? error = null;
        var now = _clock.UtcNow;

        foreach (var insight in waiting)
        {
            if (now - insight.CreatedAt > GiveUpAfter)
            {
                insight.DiscordError = "Not posted within a day.";
                continue;
            }

            var outcome = await gateway.PostAsync(insight.DiscordChannelId!, [Card(insight)], ct).ConfigureAwait(false);

            if (outcome.Sent)
            {
                insight.DiscordPostedAt = _clock.UtcNow;
                posted++;
                continue;
            }

            error = outcome.Error ?? "Discord refused the message.";
            _log.Warning("Could not post an insight to Discord: {Reason}", error);

            if (outcome.Permanent)
            {
                insight.DiscordError = error.Length <= 1000 ? error : error[..1000];
                continue;
            }

            // Discord is having a moment; the rest can wait for the next pass too.
            break;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new InsightPostPass(posted, error);
    }

    public static DiscordEmbedContent Card(Insight insight)
    {
        ArgumentNullException.ThrowIfNull(insight);

        var days = insight.FirstDay == insight.LastDay
            ? insight.LastDay.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : $"{insight.FirstDay.ToString("d MMMM", CultureInfo.InvariantCulture)} to {insight.LastDay.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}";

        var text = insight.Text ?? "";

        return new DiscordEmbedContent(
            Title: $"{InsightKinds.Label(insight.Kind)}: {days}",
            Description: text.Length <= 4096 ? text : string.Concat(text.AsSpan(0, 4095), "…"),
            Color: Purple,
            Fields: [],
            Timestamp: insight.CreatedAt,
            Url: null,
            Footer: insight.Model is null ? "AI insight" : $"AI insight · {insight.Model}");
    }
}
