using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Calendar;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/events</c>: the next dates in the next two weeks, for any member of the server (Discord
/// commands design §3.6 and §4, step 5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only what the server already shows.</strong> An event is listed only when it is
/// published to this Discord -- a Discord event or a channel post, still switched on and still
/// there -- and is scheduled or open. Never a draft, a cancelled or finished event, or a deleted
/// one (decision 8). The answer is public by default, so it says nothing the channel could not
/// already read.
/// </para>
/// <para>
/// <strong>Dates come from the repeat</strong> (<see cref="CalendarRepeat.Between"/>), so a date
/// moved or cancelled on its own is right. Times are Discord's own timestamps, which each reader
/// sees in their own time zone.
/// </para>
/// <para>
/// <strong>A Join button</strong> appears only for an event anyone can join (Who can join =
/// Anyone) whose instance is open and not closed: the same rule as the Discord event and the
/// channel post (<see cref="CalendarJoinLink"/>, <see cref="CalendarFeedWriter.JoinLink"/>). It
/// opens VRChat's launch page for the instance directly, so it works without a public address.
/// Members-only events are listed with no button.
/// </para>
/// <para>
/// Who sees the answer is decided before the command is acknowledged, from the <c>private</c>
/// option and the channel rule (<see cref="PublicReplyWindow"/>); this class only writes it.
/// </para>
/// </remarks>
public sealed class EventsCommand
{
    /// <summary>The key of the per-person limit, apart from <c>/me</c>'s and <c>/verify</c>'s.</summary>
    public const string LimitsKey = "events";

    public const string TooFastMessage = "Slow down. Try again in a minute.";
    public const string NoEventsMessage = "No events in the next 14 days.";
    public const string OpenNow = "Open now";
    public const string JoinLabel = "Join";

    /// <summary>A message holds 2,000 characters; Discord refuses more.</summary>
    private const int MessageLength = 2000;

    /// <summary>A button label holds 80 characters, of which "Join " takes five.</summary>
    private const int TitleInLabel = 60;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public EventsCommand(
        ModbotContext db,
        IModbotClock clock,
        [FromKeyedServices(LimitsKey)] MemberCommandLimits limits)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);

        _db = db;
        _clock = clock;
        _limits = limits;
    }

    /// <summary>Counts one use for this person and says whether it is allowed. See <see cref="MemberCommandLimits"/>.</summary>
    public bool TryUse(string discordUserId) => _limits.TryUse(discordUserId, _clock.UtcNow);

    /// <summary>
    /// The events <c>/events</c> may show, and <c>/remindme</c> may suggest: scheduled or open, not
    /// deleted, and published in this Discord and still switched on there -- the place row says it
    /// went out, the event's own switch says it is still meant to be there. Never a draft, a
    /// cancelled or finished event, or one that was never posted.
    /// </summary>
    public static Task<List<CalendarEvent>> ListedAsync(ModbotContext db, CancellationToken ct)
        => Listed(db).ToListAsync(ct);

    /// <summary>
    /// The one rule for which events may be shown, as a query to narrow further. <c>/events</c> and
    /// <c>/remindme</c> sign-up read it through <see cref="ListedAsync"/>, and the reminder pass asks
    /// it again before it sends, so an event taken off Discord after a member asked is never named
    /// in a message.
    /// </summary>
    public static IQueryable<CalendarEvent> Listed(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null
                        && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)
                        && db.CalendarEventPlaces.Any(p => p.EventId == e.Id
                            && p.State == CalendarPlaceStates.Published
                            && ((p.Place == CalendarPlaces.DiscordEvent && e.PublishToDiscord)
                                || (p.Place == CalendarPlaces.ChannelPost && e.PostToChannel))));
    }

    /// <summary>The answer: the next dates, and a Join button under each one that can be joined now.</summary>
    public async Task<DiscordReply> AnswerAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var until = now.AddDays(DiscordCommands.EventsDays);

        var events = await ListedAsync(_db, ct).ConfigureAwait(false);

        var next = events
            .SelectMany(e => CalendarRepeat.Between(e, now, until).Select(date => (Event: e, Date: date)))
            .OrderBy(d => d.Date.StartsAt)
            .ThenBy(d => d.Event.Title, StringComparer.Ordinal)
            .Take(DiscordCommands.EventsMost)
            .ToList();

        if (next.Count == 0)
            return DiscordReply.Say(NoEventsMessage);

        var worldIds = next
            .Select(d => d.Event.WorldId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var worlds = worldIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _db.VRChatWorlds.AsNoTracking()
                .Where(w => worldIds.Contains(w.WorldId) && w.Name != null)
                .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

        var lines = new List<string>(next.Count);
        var joins = new List<DiscordLinkButton>();

        foreach (var (calendarEvent, date) in next)
        {
            var title = CalendarRepeat.TitleOf(calendarEvent, date);
            var open = calendarEvent.State == CalendarEventStates.Open && calendarEvent.OccurrenceStartsAt == date.StartsAt;

            lines.Add(Line(title, date, calendarEvent.WorldId is { } id ? worlds.GetValueOrDefault(id) : null, open));

            // Anyone-can-join, and the instance is open now: a link straight to it.
            if (open
                && await CalendarJoinLink.ForAnyoneAsync(_db, calendarEvent, date.StartsAt, ct).ConfigureAwait(false) is { } link)
            {
                joins.Add(new DiscordLinkButton($"{JoinLabel} {CardText.Plain(title, TitleInLabel)}", link));
            }
        }

        return new DiscordReply(
            DiscordCommandHandler.WholeLines(lines, MessageLength),
            [],
            joins.Count == 0 ? null : joins);
    }

    /// <summary>
    /// One date: <c>**Title** · &lt;t:…:f&gt; (&lt;t:…:R&gt;)</c>, then the world's name when Modbot
    /// knows it, then "Open now" while the instance is open. Public so the words can be tested
    /// without a database.
    /// </summary>
    public static string Line(string title, CalendarOccurrence date, string? worldName, bool open)
    {
        ArgumentNullException.ThrowIfNull(title);

        var line = new StringBuilder("**")
            .Append(CardText.EscapeName(CardText.Plain(title, CalendarEvent.MaxTitleLength)))
            .Append("** · ")
            .Append(DiscordTime.Absolute(date.StartsAt))
            .Append(" (")
            .Append(DiscordTime.Relative(date.StartsAt))
            .Append(')');

        if (!string.IsNullOrWhiteSpace(worldName))
            line.Append(" · ").Append(CardText.EscapeName(CardText.Plain(worldName, 80)));

        if (open)
            line.Append(" · ").Append(OpenNow);

        return line.ToString();
    }
}
