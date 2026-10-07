using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.VRChat.Calendar;
using NodaTime;
using NodaTime.Text;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The bot's way into the calendar: Open now and cancelling a date, through the same code the
/// endpoints call, with the staff account the bot resolved as the caller (Discord commands design
/// §3.7 and §4, step 7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The endpoints' own code.</strong> Open now is <see cref="CalendarOpener.OpenNowAsync"/>,
/// with its refusals worded by <see cref="CalendarOpenWords"/>; a cancel is
/// <see cref="CalendarCancellations"/>. The facts, the rows and the refusals are therefore the ones
/// the web app's buttons give.
/// </para>
/// <para>
/// <strong>A one-off event's only date is the whole event.</strong> The endpoint refuses a one-off
/// event's date ("Only a repeating event has dates of its own."), because the web app has a Cancel
/// event button for it. Here there is only the one command, so cancelling the only date cancels the
/// event, with the same rules as the Cancel event endpoint.
/// </para>
/// <para>
/// <strong>The permission is checked here too</strong> (Manage calendar), with the web app's rule.
/// Suggesting events and dates also needs See calendar, because the web app lists events only with
/// it, and the bot shows no more than the web app does.
/// </para>
/// </remarks>
public sealed class CalendarActionsForDiscord : ICalendarActions
{
    public const string NoPermission = "You do not have permission to do that.";
    public const string NotSetUp = "This deployment is not set up to act in VRChat.";
    public const string AlreadyCancelledDate = "That date is cancelled already.";
    public const string AlreadyCancelledEvent = "That event is cancelled already.";
    public const string NoDateThen = "That event has no date then.";
    public const string TryAgainSoon = "VRChat did not take it yet. Modbot will try again in a minute.";

    private static readonly LocalDateTimePattern WhenPattern = LocalDateTimePattern.CreateWithInvariantCulture("ddd d MMM, HH:mm");

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarCancellations _cancellations;
    private readonly IServiceProvider _services;

    public CalendarActionsForDiscord(
        ModbotContext db, IModbotClock clock, CalendarCancellations cancellations, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cancellations);
        ArgumentNullException.ThrowIfNull(services);

        _db = db;
        _clock = clock;
        _cancellations = cancellations;
        _services = services;
    }

    private static bool Manages(StaffMember by) => ModbotAuth.Allows(by.Held, ModbotPermissions.ManageCalendar);

    /// <summary>The web app lists events only with See calendar; a suggestion lists them too.</summary>
    private static bool Sees(StaffMember by) => Manages(by) && ModbotAuth.Allows(by.Held, ModbotPermissions.ViewCalendar);

    public async Task<IReadOnlyList<CalendarEventChoice>> UpcomingAsync(
        string typed, int most, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (!Sees(by))
            return [];

        var now = _clock.UtcNow;
        var words = (typed ?? string.Empty).Trim();

        var live = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .ToListAsync(ct);

        return
        [
            .. live
                .Where(e => words.Length == 0 || e.Title.Contains(words, StringComparison.OrdinalIgnoreCase))
                .Select(e => (Event: e, Next: CalendarRepeat.Next(e, now)))
                .Where(x => x.Next is not null)
                .OrderBy(x => x.Next!.Value.StartsAt)
                .ThenBy(x => x.Event.Title, StringComparer.Ordinal)
                .Take(Math.Max(0, most))
                .Select(x => new CalendarEventChoice(
                    x.Event.Id, x.Event.Title, x.Next!.Value.StartsAt, When(x.Event, x.Next!.Value.StartsAt))),
        ];
    }

    public async Task<IReadOnlyList<CalendarDateChoice>> DatesAsync(
        Guid eventId, int most, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (!Sees(by))
            return [];

        var calendarEvent = await _db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DeletedAt == null, ct);

        if (calendarEvent is null || !CalendarEventStates.IsLive(calendarEvent.State))
            return [];

        // Dates the repeat gives and a date's own change has moved or cancelled, as the web app's
        // page has them. A date that has ended is not offered.
        return
        [
            .. CalendarRepeat.Between(calendarEvent, _clock.UtcNow)
                .Take(Math.Max(0, most))
                .Select(o => new CalendarDateChoice(o.PlannedStartsAt, o.StartsAt, When(calendarEvent, o.StartsAt))),
        ];
    }

    public async Task<CalendarOpenAnswer> OpenNowAsync(Guid eventId, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (!Manages(by))
            return new CalendarOpenAnswer(false, NoPermission);

        if (_services.GetService<CalendarOpener>() is not { } opener)
            return new CalendarOpenAnswer(false, NotSetUp);

        var result = await opener.OpenNowAsync(eventId, by.UserId, ct);
        var title = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.Id == eventId && e.DeletedAt == null)
            .Select(e => e.Title)
            .FirstOrDefaultAsync(ct);

        if (CalendarOpenWords.Refusal(result.Outcome) is { } words)
            return new CalendarOpenAnswer(false, words, title);

        // An attempt was made. How it went is on the attempt, as the endpoint's answer shows it.
        switch (result.Opened)
        {
            case CalendarOpenOutcome.Opened:
                return new CalendarOpenAnswer(true, string.Empty, title);

            case CalendarOpenOutcome.TryAgain:
                return new CalendarOpenAnswer(false, TryAgainSoon, title);

            case CalendarOpenOutcome.Checking:
                return new CalendarOpenAnswer(false, CalendarOpenWords.Checking, title);

            default:
                var error = await _db.CalendarOpenings.AsNoTracking()
                    .Where(o => o.EventId == eventId)
                    .OrderByDescending(o => o.AttemptedAt)
                    .Select(o => o.Error)
                    .FirstOrDefaultAsync(ct);

                return new CalendarOpenAnswer(false, $"VRChat refused: {error ?? "it did not say why."}", title);
        }
    }

    public async Task<CalendarCancelPlan> PlanCancelDateAsync(
        Guid eventId, DateTimeOffset plannedStartsAt, bool sayIt, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (!Manages(by))
            return CalendarCancelPlan.Refused(NoPermission);

        var calendarEvent = await _db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DeletedAt == null, ct);

        if (calendarEvent is null)
            return CalendarCancelPlan.Refused(CalendarOpenWords.NoSuchEvent);

        if (calendarEvent.Repeat == CalendarRepeats.None)
        {
            var problem = OneOffProblem(calendarEvent, plannedStartsAt, sayIt);
            return problem is null
                ? new CalendarCancelPlan(true, null, calendarEvent.Title, calendarEvent.StartsAt, WholeEvent: true)
                : CalendarCancelPlan.Refused(problem);
        }

        if (await _cancellations.CheckDateAsync(eventId, plannedStartsAt, sayIt, ct) is { } refusal)
            return CalendarCancelPlan.Refused(Words(refusal, AlreadyCancelledDate));

        var date = CalendarRepeat.ForDate(calendarEvent, plannedStartsAt)!.Value;
        return new CalendarCancelPlan(true, null, CalendarRepeat.TitleOf(calendarEvent, date), date.StartsAt, WholeEvent: false);
    }

    public async Task<CalendarCancelAnswer> CancelDateAsync(
        Guid eventId, DateTimeOffset plannedStartsAt, bool sayIt, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (!Manages(by))
            return new CalendarCancelAnswer(false, NoPermission);

        var calendarEvent = await _db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DeletedAt == null, ct);

        if (calendarEvent is null)
            return new CalendarCancelAnswer(false, CalendarOpenWords.NoSuchEvent);

        var actor = new Actor(by.UserId, by.Username);
        CalendarCancelResult result;

        if (calendarEvent.Repeat == CalendarRepeats.None)
        {
            // The same rules as Cancel event, once the date is known to be this event's only one.
            if (calendarEvent.State != CalendarEventStates.Cancelled && OneOffProblem(calendarEvent, plannedStartsAt, sayIt) is { } problem)
                return new CalendarCancelAnswer(false, problem);

            result = await _cancellations.CancelEventAsync(eventId, sayIt, actor, ct);
        }
        else
        {
            result = await _cancellations.CancelDateAsync(eventId, plannedStartsAt, sayIt, actor, ct);
        }

        return result.Status switch
        {
            CalendarCancelStatus.Done => new CalendarCancelAnswer(true, string.Empty),
            CalendarCancelStatus.AlreadyCancelled => new CalendarCancelAnswer(true, string.Empty, Repeat: true),
            _ => new CalendarCancelAnswer(false, Words(result, AlreadyCancelledDate)),
        };
    }

    /// <summary>Why a one-off event cannot be cancelled now by its only date, or null when it can.</summary>
    private string? OneOffProblem(CalendarEvent calendarEvent, DateTimeOffset planned, bool sayIt)
    {
        if (calendarEvent.State == CalendarEventStates.Cancelled)
            return AlreadyCancelledEvent;

        if (calendarEvent.State == CalendarEventStates.Finished)
            return CalendarOpenWords.Over;

        if (!CalendarRepeat.IsPlannedDate(calendarEvent, planned))
            return NoDateThen;

        if (CalendarRepeat.ForDate(calendarEvent, planned) is { } date && date.EndsAt <= _clock.UtcNow)
            return CalendarOpenWords.Over;

        if (sayIt && string.IsNullOrEmpty(calendarEvent.ChannelId?.Trim()))
            return CalendarCancellations.NoChannel;

        return null;
    }

    private static string Words(CalendarCancelResult result, string alreadyCancelled) => result.Status switch
    {
        CalendarCancelStatus.AlreadyCancelled => alreadyCancelled,
        CalendarCancelStatus.NotFound => CalendarOpenWords.NoSuchEvent,
        _ => result.Error ?? "That did not work.",
    };

    /// <summary>A start as plain words in the event's own time zone: "Sat 10 Oct, 20:00 UTC".</summary>
    private static string When(CalendarEvent calendarEvent, DateTimeOffset startsAt)
    {
        var zone = CalendarRepeat.ZoneOf(calendarEvent);
        return WhenPattern.Format(Instant.FromDateTimeOffset(startsAt).InZone(zone).LocalDateTime) + " " + zone.Id;
    }
}
