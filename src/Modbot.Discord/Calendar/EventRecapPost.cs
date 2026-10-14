using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using NodaTime;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Posts a recap in the chosen channel once the instance a date of an event opened has closed: who
/// came, the most people there at once, the next date of the event, and a thread for photos started
/// on the post.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Closed</strong> is the group instance poll finding the instance gone from the group's
/// list. An instance the poll never saw close counts as closed <see cref="StillOpenAfter"/> after
/// the date was due to end, so one missed close never holds a recap back for good.
/// </para>
/// <para>
/// <strong>Once per date.</strong> <c>calendar_opening.recap_posted_at</c> is written before the
/// post, so a restart never posts twice. A refusal Discord calls final keeps it set; one that may
/// pass clears it, so the next pass tries again. What went wrong is kept on the settings, where the
/// card shows it. A date nobody was seen at, and no head count above zero, gets no recap.
/// </para>
/// <para>
/// Only recaps for instances that closed after the switch was last turned on (<see cref="Settings.EventRecapsFrom"/>),
/// so turning it on never posts a pile for events long over. Names are posted as VRChat shows them,
/// in an embed, which pings nobody.
/// </para>
/// </remarks>
public sealed class EventRecapPost
{
    /// <summary>The longest one date may take; openings older than this are not looked at.</summary>
    private static readonly TimeSpan LongestOccurrence = TimeSpan.FromDays(7);

    /// <summary>How long after a date was due to end an instance that never showed as closed counts as closed.</summary>
    public static readonly TimeSpan StillOpenAfter = TimeSpan.FromHours(12);

    /// <summary>Discord's longest embed description is 4096; the names stop short of it.</summary>
    private const int NamesAtMost = 3000;

    private const int ProblemAtMost = 300;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly EventAttendance _attendance;
    private readonly ILogger _log;

    public EventRecapPost(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _attendance = new EventAttendance(db);
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <returns>How many recaps went out.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!settings.EventRecapsOn
            || settings.EventRecapChannelId is not { Length: > 0 } channelId
            || settings.EventRecapsFrom is not { } from)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var since = now - LongestOccurrence;

        var openings = await _db.CalendarOpenings
            .Where(o => o.InstanceId != null && o.RecapPostedAt == null && o.OccurrenceStartsAt > since)
            .ToListAsync(ct).ConfigureAwait(false);

        var posted = 0;

        foreach (var opening in openings)
        {
            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == opening.EventId, ct).ConfigureAwait(false);

            if (calendarEvent is null || calendarEvent.DeletedAt is not null)
                continue;

            var instance = await _db.VRChatInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == opening.InstanceId, ct).ConfigureAwait(false);

            if (instance is null)
                continue;

            var occurrence = CalendarRepeat.StartingAt(calendarEvent, opening.OccurrenceStartsAt)
                ?? new CalendarOccurrence(
                    opening.OccurrenceStartsAt, opening.OccurrenceStartsAt + CalendarRepeat.LengthOf(calendarEvent));

            var closedAt = instance.ClosedAt ?? occurrence.EndsAt + StillOpenAfter;

            if (closedAt > now)
                continue;

            // Written first, and only where nobody has written it yet: once, however many restarts or
            // copies of Modbot. A date that closed before the switch was turned on is marked the same
            // way, without a post, so it is not read again every pass.
            if (!await MarkAsync(opening, now, ct).ConfigureAwait(false))
                continue;

            if (closedAt < from)
                continue;

            var turnout = (await _attendance.ForOpeningsAsync([opening], ct).ConfigureAwait(false))[0];

            if (turnout.People.Count == 0 && (turnout.PeakHeadCount ?? 0) <= 0)
                continue;

            var names = await NamesAsync(turnout.People, ct).ConfigureAwait(false);
            var next = NextDate(calendarEvent, occurrence);

            var outcome = await gateway.PostAsync(
                channelId.Trim(),
                text: null,
                [Embed(calendarEvent, occurrence, turnout, names, next)],
                links: null,
                ct).ConfigureAwait(false);

            if (!outcome.Sent)
            {
                // A refusal that may pass is tried again; a final one, or one that may have gone
                // through, is not, so a recap never doubles.
                if (!outcome.Permanent && !outcome.Unclear)
                    await UnmarkAsync(opening, now, ct).ConfigureAwait(false);

                settings.EventRecapProblem = CardText.Fit(outcome.Error ?? "Discord refused the recap.", ProblemAtMost);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                _log.Warning(
                    "Could not post the recap of the event {EventId}: {Error}", calendarEvent.Id, settings.EventRecapProblem);
                continue;
            }

            posted++;
            settings.EventRecapProblem = null;

            if (outcome.MessageId is { } messageId)
            {
                var thread = await gateway.StartThreadOnMessageAsync(
                    channelId.Trim(), messageId, ThreadName(calendarEvent, occurrence), ct).ConfigureAwait(false);

                if (!thread.Sent)
                {
                    settings.EventRecapProblem = CardText.Fit(
                        "The recap went up without its photo thread. " + (thread.Error ?? "Discord refused the thread."),
                        ProblemAtMost);

                    _log.Warning(
                        "Could not start the photo thread of the event {EventId}: {Error}", calendarEvent.Id, thread.Error);
                }
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return posted;
    }

    /// <summary>Sets the posted time where it is still empty. False when another copy of Modbot got there first.</summary>
    private async Task<bool> MarkAsync(CalendarOpening opening, DateTimeOffset now, CancellationToken ct)
        => await _db.CalendarOpenings
            .Where(o => o.EventId == opening.EventId
                && o.OccurrenceStartsAt == opening.OccurrenceStartsAt
                && o.RecapPostedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.RecapPostedAt, now), ct)
            .ConfigureAwait(false) > 0;

    /// <summary>Clears the time this pass set, so the next pass tries the post again.</summary>
    private Task<int> UnmarkAsync(CalendarOpening opening, DateTimeOffset marked, CancellationToken ct)
        => _db.CalendarOpenings
            .Where(o => o.EventId == opening.EventId
                && o.OccurrenceStartsAt == opening.OccurrenceStartsAt
                && o.RecapPostedAt == marked)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.RecapPostedAt, (DateTimeOffset?)null), ct);

    /// <summary>
    /// The next date of the same event after <paramref name="done"/>: the repeat's next date, with
    /// a date cancelled or moved on its own counted as it now is. Null when the event does not
    /// repeat again, or is cancelled, deleted or not live any more.
    /// </summary>
    public static CalendarOccurrence? NextDate(CalendarEvent calendarEvent, CalendarOccurrence done)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (calendarEvent.DeletedAt is not null
            || calendarEvent.CancelledAt is not null
            || !CalendarEventStates.IsLive(calendarEvent.State))
        {
            return null;
        }

        foreach (var occurrence in CalendarRepeat.Between(calendarEvent, done.StartsAt))
        {
            if (occurrence.StartsAt > done.StartsAt)
                return occurrence;
        }

        return null;
    }

    /// <summary>The thread's name: the event's title and the date, in the event's own time zone.</summary>
    public static string ThreadName(CalendarEvent calendarEvent, CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var date = Instant.FromDateTimeOffset(occurrence.StartsAt)
            .InZone(CalendarRepeat.ZoneOf(calendarEvent))
            .Date
            .ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        var title = CardText.Plain(CalendarRepeat.TitleOf(calendarEvent, occurrence, CalendarSite.Discord), 100 - date.Length - 1);
        return $"{title} {date}";
    }

    /// <summary>The recap card. Names are shown as VRChat shows them and never pinged.</summary>
    /// <param name="names">Display names of the people who came that Modbot knows; may be fewer than <see cref="EventTurnout.People"/>.</param>
    public static DiscordEmbedContent Embed(
        CalendarEvent calendarEvent,
        CalendarOccurrence occurrence,
        EventTurnout turnout,
        IReadOnlyList<string> names,
        CalendarOccurrence? next)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(turnout);
        ArgumentNullException.ThrowIfNull(names);

        var fields = new List<DiscordEmbedField>();

        if (turnout.People.Count > 0)
        {
            fields.Add(new DiscordEmbedField(
                "Came",
                turnout.People.Count == 1 ? "1 person" : $"{turnout.People.Count.ToString(CultureInfo.InvariantCulture)} people",
                Inline: true));
        }

        if (turnout.PeakHeadCount is { } peak and > 0)
            fields.Add(new DiscordEmbedField("Most at once", peak.ToString(CultureInfo.InvariantCulture), Inline: true));

        if (next is { } following)
            fields.Add(new DiscordEmbedField("Next date", Stamp(following.StartsAt), Inline: true));

        return new DiscordEmbedContent(
            Title: "Recap: " + CardText.Plain(CalendarRepeat.TitleOf(calendarEvent, occurrence, CalendarSite.Discord), 240),
            Description: NameList(names, turnout.People.Count),
            Color: CardColour.Dark,
            Fields: fields,
            Timestamp: occurrence.StartsAt,
            Url: null,
            Footer: null);
    }

    /// <summary>The names in one line, as many as fit, then how many more came.</summary>
    private static string? NameList(IReadOnlyList<string> names, int came)
    {
        if (names.Count == 0)
            return null;

        var line = new System.Text.StringBuilder();
        var shown = 0;

        foreach (var name in names)
        {
            var piece = CardText.EscapeName(name);

            if (piece.Length == 0)
                continue;

            if (line.Length + piece.Length + 2 > NamesAtMost)
                break;

            if (shown > 0)
                line.Append(", ");

            line.Append(piece);
            shown++;
        }

        if (shown == 0)
            return null;

        var more = came - shown;

        if (more > 0)
            line.Append(CultureInfo.InvariantCulture, $" and {more} more");

        return line.ToString();
    }

    private async Task<IReadOnlyList<string>> NamesAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var names = await _db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null && u.DisplayName != "")
            .Select(u => u.DisplayName!)
            .ToListAsync(ct).ConfigureAwait(false);

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private static string Stamp(DateTimeOffset at)
    {
        var seconds = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"<t:{seconds}:F> (<t:{seconds}:R>)";
    }
}
