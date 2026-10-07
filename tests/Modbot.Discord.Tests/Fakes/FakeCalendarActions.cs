using System.Collections.Concurrent;
using Modbot.Core.Calendar;
using Modbot.Core.Discord;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>
/// Stands in for the API's calendar code: records what the bot asked for and answers as told.
/// A date is cancelled once, as the real service does: asking again answers "cancelled already".
/// </summary>
public sealed class FakeCalendarActions : ICalendarActions
{
    private readonly ConcurrentDictionary<(Guid, DateTimeOffset), bool> _cancelled = new();

    /// <summary>The events <see cref="UpcomingAsync"/> offers.</summary>
    public List<CalendarEventChoice> Events { get; } = [];

    /// <summary>The dates <see cref="DatesAsync"/> offers, by event.</summary>
    public Dictionary<Guid, List<CalendarDateChoice>> Dates { get; } = [];

    /// <summary>What Open now answers.</summary>
    public CalendarOpenAnswer OpenAnswer { get; set; } = new(true, string.Empty, "Movie night");

    /// <summary>What the checks before a cancel answer. Allowed by default.</summary>
    public CalendarCancelPlan Plan { get; set; } = new(true, null, "Movie night", new DateTimeOffset(2026, 9, 20, 20, 0, 0, TimeSpan.Zero), WholeEvent: false);

    /// <summary>Set to make the cancel itself refuse with this sentence.</summary>
    public string? CancelRefusal { get; set; }

    /// <summary>Set to make the cancel throw, as a database that went away does.</summary>
    public Exception? CancelThrows { get; set; }

    /// <summary>Every Open now asked for.</summary>
    public List<(Guid EventId, StaffMember By)> Opens { get; } = [];

    /// <summary>Every suggestion asked for.</summary>
    public List<(string Typed, int Most, StaffMember By)> UpcomingAsked { get; } = [];

    public List<(Guid EventId, int Most, StaffMember By)> DatesAsked { get; } = [];

    /// <summary>Every check before a cancel asked for.</summary>
    public List<(Guid EventId, DateTimeOffset Planned, bool SayIt, StaffMember By)> Plans { get; } = [];

    /// <summary>Every cancel asked for, repeats included.</summary>
    public List<(Guid EventId, DateTimeOffset Planned, bool SayIt, StaffMember By)> CancelCalls { get; } = [];

    /// <summary>The cancels that actually changed something: once per date.</summary>
    public List<(Guid EventId, DateTimeOffset Planned, bool SayIt, StaffMember By)> Cancelled { get; } = [];

    public Task<IReadOnlyList<CalendarEventChoice>> UpcomingAsync(string typed, int most, StaffMember by, CancellationToken ct = default)
    {
        UpcomingAsked.Add((typed, most, by));

        return Task.FromResult<IReadOnlyList<CalendarEventChoice>>(
            [.. Events.Where(e => e.Title.Contains(typed, StringComparison.OrdinalIgnoreCase)).Take(most)]);
    }

    public Task<IReadOnlyList<CalendarDateChoice>> DatesAsync(Guid eventId, int most, StaffMember by, CancellationToken ct = default)
    {
        DatesAsked.Add((eventId, most, by));

        return Task.FromResult<IReadOnlyList<CalendarDateChoice>>(
            Dates.TryGetValue(eventId, out var dates) ? [.. dates.Take(most)] : []);
    }

    public Task<CalendarOpenAnswer> OpenNowAsync(Guid eventId, StaffMember by, CancellationToken ct = default)
    {
        Opens.Add((eventId, by));
        return Task.FromResult(OpenAnswer);
    }

    public Task<CalendarCancelPlan> PlanCancelDateAsync(
        Guid eventId, DateTimeOffset plannedStartsAt, bool sayIt, StaffMember by, CancellationToken ct = default)
    {
        Plans.Add((eventId, plannedStartsAt, sayIt, by));
        return Task.FromResult(Plan);
    }

    public Task<CalendarCancelAnswer> CancelDateAsync(
        Guid eventId, DateTimeOffset plannedStartsAt, bool sayIt, StaffMember by, CancellationToken ct = default)
    {
        CancelCalls.Add((eventId, plannedStartsAt, sayIt, by));

        if (CancelThrows is { } thrown)
            throw thrown;

        if (CancelRefusal is { } refusal)
            return Task.FromResult(new CalendarCancelAnswer(false, refusal));

        if (!_cancelled.TryAdd((eventId, plannedStartsAt), true))
            return Task.FromResult(new CalendarCancelAnswer(true, string.Empty, Repeat: true));

        Cancelled.Add((eventId, plannedStartsAt, sayIt, by));
        return Task.FromResult(new CalendarCancelAnswer(true, string.Empty));
    }
}
