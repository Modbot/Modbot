using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Chat;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>The calendar, read the way the Calendar page reads it.</summary>
internal static class CalendarRows
{
    public static object Row(CalendarEventView e) => new
    {
        eventId = e.Id,
        e.Title,
        e.State,
        startsAt = e.Occurrences.Count > 0 ? e.Occurrences[0].StartsAt : e.StartsAt,
        endsAt = e.Occurrences.Count > 0 ? e.Occurrences[0].EndsAt : e.EndsAt,
        e.TimeZone,
        e.Repeat,
        e.RepeatDays,
        e.RepeatUntil,
        e.WorldId,
        e.WorldName,
        e.AccessType,
        e.Region,
        e.Category,
        occurrences = e.Occurrences.Count,
    };

    public static IEnumerable<ChatReference> References(IEnumerable<CalendarEventView> events)
    {
        foreach (var e in events)
        {
            yield return new ChatReference(ChatReference.Event, e.Id.ToString(), e.Title);

            if (e.WorldId is { } world)
                yield return new ChatReference(ChatReference.World, world, e.WorldName);
        }
    }
}

/// <summary>What is coming up, or what ran, over a window of days.</summary>
internal sealed class CalendarEventsTool : ReadTool
{
    /// <summary>The most days one call may ask for, which is what the page's own read allows.</summary>
    private const int MostDays = 62;

    public override string Name => "list_calendar_events";

    public override string Label => "What's on";

    public override string Description =>
        "The group's calendar events over a window of days: when each runs, its world, whether it "
        + "repeats, and whether it is a draft, scheduled, open, finished or cancelled. Looks "
        + "forward by default; pass days back to include what already ran.";

    protected override string Schema => """
        {"type":"object","properties":{"days":{"type":"integer","minimum":1,"maximum":62,"description":"How many days forward. Default 30."},"daysBack":{"type":"integer","minimum":0,"maximum":61,"description":"How many days back to include. Default 0."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 20."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewCalendar;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var limit = ChatArguments.Number(arguments, "limit", 20, 1, 50);
        var back = ChatArguments.Number(arguments, "daysBack", 0, 0, MostDays - 1);
        var forward = ChatArguments.Number(arguments, "days", 30, 1, MostDays - back);

        var db = Get<ModbotContext>(context);
        var now = Get<IModbotClock>(context).UtcNow;
        var from = now.AddDays(-back);
        var to = now.AddDays(forward);

        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(ct);

        // The page's own read: an event's occurrences are worked out for the window, so a repeating
        // event is listed by when it actually next runs.
        var views = await CalendarEndpoints.ViewsAsync(db, events, from, to, ct);

        var (rows, more) = FirstOf(
            [.. views
                .Where(v => v.Occurrences.Count > 0)
                .OrderBy(v => v.Occurrences[0].StartsAt)
                .Take(limit + 1)],
            limit);

        return ChatToolResult.Json(
            new
            {
                from,
                to,
                count = rows.Count,
                more,
                events = rows.Select(CalendarRows.Row),
            },
            CalendarRows.References(rows));
    }
}

/// <summary>One event, past or planned, with how publishing it went.</summary>
internal sealed class GetCalendarEventTool : ReadTool
{
    public override string Name => "get_calendar_event";

    public override string Label => "Open an event";

    public override string Description =>
        "One calendar event by its id: its description, when it runs, its world and instance settings, "
        + "where it was published and whether that worked, and whether Modbot opened an instance for it.";

    protected override string Schema => """
        {"type":"object","properties":{"eventId":{"type":"string","description":"The event id another tool returned."}},"required":["eventId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewCalendar;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (!Guid.TryParse(ChatArguments.Text(arguments, "eventId"), out var id))
            return ChatToolResult.Problem("eventId must be the id another tool returned.");

        var db = Get<ModbotContext>(context);
        var now = Get<IModbotClock>(context).UtcNow;

        var row = await db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);

        if (row is null)
            return ChatToolResult.Problem("No event has that id.");

        var view = (await CalendarEndpoints.ViewsAsync(db, [row], now.AddDays(-365), now.AddDays(365), ct))[0];

        return ChatToolResult.Json(
            new
            {
                eventId = view.Id,
                view.Title,
                view.Description,
                view.State,
                view.StartsAt,
                view.EndsAt,
                view.TimeZone,
                view.Repeat,
                view.RepeatDays,
                view.RepeatUntil,
                view.WorldId,
                view.WorldName,
                view.AccessType,
                view.Region,
                view.Category,
                view.Languages,
                view.Platforms,
                view.Tags,
                view.Visibility,
                view.AutoOpen,
                view.OpenMinutesBefore,
                publishedTo = view.Places.Select(p => new { p.Place, p.State, p.Error }),
                opening = view.Opening is null
                    ? null
                    : new { view.Opening.OccurrenceStartsAt, view.Opening.AttemptedAt, instanceId = view.Opening.InstanceId, view.Opening.Closed, view.Opening.Error },
                nextOccurrences = view.Occurrences.Take(5),
                view.CreatedAt,
                view.UpdatedAt,
            },
            [
                .. CalendarRows.References([view]),
                .. view.Opening?.InstanceId is { } instance
                    ? new[] { new ChatReference(ChatReference.Instance, instance.ToString(), view.Title) }
                    : [],
            ]);
    }
}
