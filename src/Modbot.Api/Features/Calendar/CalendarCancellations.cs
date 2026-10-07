using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat.Calendar;

namespace Modbot.Api.Features.Calendar;

/// <summary>How a cancel ended.</summary>
public enum CalendarCancelStatus
{
    /// <summary>It is cancelled now, and the fact is written.</summary>
    Done,

    /// <summary>It was cancelled already. Nothing changed, and nothing was written.</summary>
    AlreadyCancelled,

    /// <summary>No such event.</summary>
    NotFound,

    /// <summary>Refused: <see cref="CalendarCancelResult.Error"/> says why, and <see cref="CalendarCancelResult.HttpStatus"/> is the status the endpoint gives it.</summary>
    Refused,
}

/// <param name="HttpStatus">For a refusal: 400 or 409, as the endpoints have always answered it.</param>
/// <param name="Title">The event's title, when it was found.</param>
public sealed record CalendarCancelResult(CalendarCancelStatus Status, string? Error = null, int HttpStatus = 0, string? Title = null)
{
    public bool Cancelled => Status is CalendarCancelStatus.Done or CalendarCancelStatus.AlreadyCancelled;
}

/// <summary>
/// Cancelling a whole event, and cancelling one date of a repeating one: the rules and the writes
/// that used to sit inside the calendar endpoints' lambdas, moved here so the Discord bot can reach
/// them too (Discord commands design §3.7, step 7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Moved, not changed.</strong> The endpoints call this and turn the result back into the
/// answers they have always given (204, 400, 404 or 409 with the same words); the bot's
/// <see cref="CalendarActionsForDiscord"/> calls it too. A test runs the same cancel through both
/// and holds them to the same result, the same rows and the same fact.
/// </para>
/// <para>
/// The row, the version and the fact are saved in one transaction, as they always were. The
/// calendar's Discord loop posts "cancelled" in the event's channel, once, when asked to.
/// </para>
/// </remarks>
public sealed class CalendarCancellations
{
    public const string NoChannel = "The event has no channel to post in.";

    private readonly ModbotContext _db;
    private readonly AccountFacts _facts;
    private readonly IModbotClock _clock;

    public CalendarCancellations(ModbotContext db, AccountFacts facts, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _clock = clock;
    }

    /// <summary>Cancels a whole event. Cancelled already changes nothing, and a second cancel post is never made.</summary>
    /// <param name="postInChannel">Also post, once, that it is cancelled, in the event's channel.</param>
    public async Task<CalendarCancelResult> CancelEventAsync(Guid id, bool postInChannel, Actor? actor, CancellationToken ct)
    {
        var calendarEvent = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
        if (calendarEvent is null)
            return new CalendarCancelResult(CalendarCancelStatus.NotFound);

        if (calendarEvent.State == CalendarEventStates.Cancelled)
            return new CalendarCancelResult(CalendarCancelStatus.AlreadyCancelled, Title: calendarEvent.Title);

        var channelId = calendarEvent.ChannelId?.Trim();

        if (postInChannel && string.IsNullOrEmpty(channelId))
            return Refused(StatusCodes.Status400BadRequest, NoChannel, calendarEvent.Title);

        var now = _clock.UtcNow;
        var occurrence = calendarEvent.OccurrenceStartsAt ?? calendarEvent.StartsAt;
        calendarEvent.State = CalendarEventStates.Cancelled;
        calendarEvent.CancelledAt = now;
        calendarEvent.UpdatedAt = now;
        calendarEvent.Version++;

        // The calendar's Discord loop posts it, once, and marks the row published.
        if (postInChannel && await _db.CalendarEventPlaces.AllAsync(
                p => p.EventId != calendarEvent.Id || p.Place != CalendarPlaces.CancelPost, ct))
        {
            _db.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = calendarEvent.Id,
                Place = CalendarPlaces.CancelPost,
                State = CalendarPlaceStates.Waiting,
                ChannelId = channelId,
                OccurrenceStartsAt = occurrence,
                UpdatedAt = now,
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.SaveChangesAsync(ct);
        await _facts.RecordAsync(
            FactType.PlannedEventCancelled, calendarEvent.Id.ToString(), actor,
            new JsonObject { ["title"] = calendarEvent.Title, ["postInChannel"] = postInChannel }, ct);
        await transaction.CommitAsync(ct);

        return new CalendarCancelResult(CalendarCancelStatus.Done, Title: calendarEvent.Title);
    }

    /// <summary>Cancels one date of a repeating event and leaves the others. Cancelled already changes nothing.</summary>
    /// <param name="planned">The date as the repeat plans it: what a date is known by.</param>
    /// <param name="postInChannel">Also post, once, that the date is cancelled, in the event's channel.</param>
    public async Task<CalendarCancelResult> CancelDateAsync(
        Guid id, DateTimeOffset planned, bool postInChannel, Actor? actor, CancellationToken ct)
    {
        var calendarEvent = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
        if (calendarEvent is null)
            return new CalendarCancelResult(CalendarCancelStatus.NotFound);

        var now = _clock.UtcNow;

        if (CheckDate(calendarEvent, planned, postInChannel, now) is { } early)
            return early;

        var change = calendarEvent.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == planned);
        var was = CalendarRepeat.ForDate(calendarEvent, planned)!.Value;
        var channelId = calendarEvent.ChannelId?.Trim();

        if (change is null)
        {
            change = new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = calendarEvent.Id,
                PlannedStartsAt = planned,
                CreatedAt = now,
            };

            calendarEvent.DateChanges.Add(change);
        }

        // The calendar's Discord loop posts it, once (calendar design §14.4).
        if (postInChannel)
            change.CancelPostChannelId = channelId;

        // The times it was moved to are kept: VRChat may have the date there, and taking it
        // off VRChat's calendar has to find it.
        change.Cancelled = true;
        change.UpdatedAt = now;

        MovedOn(calendarEvent, now);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.SaveChangesAsync(ct);

        await _facts.RecordAsync(
            FactType.PlannedDateCancelled,
            calendarEvent.Id.ToString(),
            actor,
            new JsonObject
            {
                ["title"] = calendarEvent.Title,
                ["date"] = planned.ToString("O", CultureInfo.InvariantCulture),
                ["startsAt"] = was.StartsAt.ToString("O", CultureInfo.InvariantCulture),
                ["endsAt"] = was.EndsAt.ToString("O", CultureInfo.InvariantCulture),
                ["postInChannel"] = postInChannel,
            },
            ct);

        await transaction.CommitAsync(ct);

        return new CalendarCancelResult(CalendarCancelStatus.Done, Title: calendarEvent.Title);
    }

    /// <summary>
    /// Every check <see cref="CancelDateAsync"/> makes before it changes anything, in its order,
    /// without changing anything. Null means the date would be cancelled now.
    /// </summary>
    public async Task<CalendarCancelResult?> CheckDateAsync(
        Guid id, DateTimeOffset planned, bool postInChannel, CancellationToken ct)
    {
        // The event's date changes load with it (auto-included), as in the endpoint.
        var calendarEvent = await _db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);

        return calendarEvent is null
            ? new CalendarCancelResult(CalendarCancelStatus.NotFound)
            : CheckDate(calendarEvent, planned, postInChannel, _clock.UtcNow);
    }

    /// <summary>The checks of a date cancel, in the order the endpoint has always made them.</summary>
    private static CalendarCancelResult? CheckDate(
        CalendarEvent calendarEvent, DateTimeOffset planned, bool postInChannel, DateTimeOffset now)
    {
        var change = calendarEvent.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == planned);

        if (change is { Cancelled: true })
            return new CalendarCancelResult(CalendarCancelStatus.AlreadyCancelled, Title: calendarEvent.Title);

        if (DateProblem(calendarEvent, planned, now) is { } problem)
            return Refused(problem.Status, problem.Error, calendarEvent.Title);

        if (postInChannel && string.IsNullOrEmpty(calendarEvent.ChannelId?.Trim()))
            return Refused(StatusCodes.Status400BadRequest, NoChannel, calendarEvent.Title);

        return null;
    }

    private static CalendarCancelResult Refused(int status, string error, string title)
        => new(CalendarCancelStatus.Refused, error, status, title);

    /// <summary>Why one date of this event cannot be cancelled or changed now, or null when it can.</summary>
    public static (int Status, string Error)? DateProblem(CalendarEvent calendarEvent, DateTimeOffset planned, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (calendarEvent.State == CalendarEventStates.Cancelled)
            return (StatusCodes.Status409Conflict, "A cancelled event cannot be changed.");

        if (calendarEvent.State == CalendarEventStates.Finished)
            return (StatusCodes.Status409Conflict, "That event has already ended.");

        if (calendarEvent.Repeat == CalendarRepeats.None)
            return (StatusCodes.Status400BadRequest, "Only a repeating event has dates of its own.");

        if (!CalendarRepeat.IsPlannedDate(calendarEvent, planned))
            return (StatusCodes.Status400BadRequest, "That event has no date then.");

        if (CalendarRepeat.ForDate(calendarEvent, planned) is { } date && date.EndsAt <= now)
            return (StatusCodes.Status409Conflict, "That date has already ended.");

        return null;
    }

    /// <summary>
    /// After one date changed: the event is a newer version, and the date it is dealing with is
    /// worked out again -- a cancelled one is skipped, a moved one opens at its new time.
    /// </summary>
    public static void MovedOn(CalendarEvent calendarEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        calendarEvent.Version++;
        calendarEvent.UpdatedAt = now;

        if (!CalendarEventStates.IsLive(calendarEvent.State))
            return;

        calendarEvent.OccurrenceStartsAt = null;
        CalendarTimeline.Advance(calendarEvent, now);
    }
}
