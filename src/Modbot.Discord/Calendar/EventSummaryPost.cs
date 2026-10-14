using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Posts, once a month in the chosen channel, how each event did the month before: its dates, the
/// average number interested on Discord and on VRChat when they started, and the average number who
/// came.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once per month.</strong> <see cref="Settings.EventSummaryLastMonth"/> is moved up to the
/// month before it is posted, so a restart never posts twice. A refusal that may pass puts it back,
/// so the next pass tries again; a final one keeps it, and the card shows what went wrong. A month in
/// which no event had a count or an opened instance gets no post.
/// </para>
/// <para>
/// Only the month before this one is posted, and only once the switch has been on since before that
/// month ended: turning it on sets the last month to the one before, so it never posts for months
/// already over. Months are UTC. Names are the events' titles, in an embed, which pings nobody.
/// </para>
/// </remarks>
public sealed class EventSummaryPost
{
    /// <summary>Discord allows 25 fields on an embed.</summary>
    internal const int MostEvents = 24;

    private const int ProblemAtMost = 300;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly EventMonthSummary _summary;
    private readonly ILogger _log;

    public EventSummaryPost(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _summary = new EventMonthSummary(db);
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <returns>1 when the summary went out, 0 otherwise.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!settings.EventSummaryOn
            || settings.EventSummaryChannelId is not { Length: > 0 } channelId
            || settings.EventSummaryLastMonth is not { } last)
        {
            return 0;
        }

        var month = EventMonthSummary.MonthOf(_clock.UtcNow).AddMonths(-1);

        if (last >= month)
            return 0;

        // Written first, and only where nobody has moved it yet: once, however many restarts or
        // copies of Modbot.
        var claimed = await _db.Settings
            .Where(s => s.Id == settings.Id && s.EventSummaryLastMonth == last)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.EventSummaryLastMonth, (DateOnly?)month), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
            return 0;

        var lines = await _summary.ForMonthAsync(month, ct).ConfigureAwait(false);

        if (lines.Count == 0)
            return 0;

        var outcome = await gateway.PostAsync(channelId.Trim(), text: null, [Embed(month, lines)], links: null, ct)
            .ConfigureAwait(false);

        if (!outcome.Sent)
        {
            // A refusal that may pass is tried again; a final one, or one that may have gone through,
            // is not, so a summary never doubles.
            if (!outcome.Permanent && !outcome.Unclear)
            {
                await _db.Settings
                    .Where(s => s.Id == settings.Id && s.EventSummaryLastMonth == month)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.EventSummaryLastMonth, (DateOnly?)last), ct)
                    .ConfigureAwait(false);
            }

            settings.EventSummaryProblem = CardText.Fit(outcome.Error ?? "Discord refused the summary.", ProblemAtMost);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _log.Warning("Could not post the event summary for {Month}: {Error}", month, settings.EventSummaryProblem);
            return 0;
        }

        settings.EventSummaryProblem = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return 1;
    }

    /// <summary>The summary card: a field for each event, the busiest first.</summary>
    public static DiscordEmbedContent Embed(DateOnly month, IReadOnlyList<EventMonthLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var fields = lines
            .Take(MostEvents)
            .Select(line => new DiscordEmbedField(CardText.Plain(line.Title, 250), Value(line)))
            .ToList();

        var left = lines.Count - fields.Count;

        return new DiscordEmbedContent(
            Title: "Events in " + month.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            Description: left > 0 ? $"and {left.ToString(CultureInfo.InvariantCulture)} more" : null,
            Color: CardColour.Violet,
            Fields: fields,
            Timestamp: null,
            Url: null,
            Footer: null);
    }

    private static string Value(EventMonthLine line)
    {
        var text = new List<string>
        {
            line.Dates == 1 ? "1 date" : $"{line.Dates.ToString(CultureInfo.InvariantCulture)} dates",
        };

        if (line.DiscordInterested is { } discord)
            text.Add("Average interested on Discord: " + Number(discord));

        if (line.VRChatInterested is { } vrchat)
            text.Add("Average interested on VRChat: " + Number(vrchat));

        if (line.Came is { } came)
            text.Add("Average came: " + Number(came));

        return string.Join('\n', text);
    }

    private static string Number(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
