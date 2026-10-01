using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;
using VRChatKind = VRChat.API.Model.CalendarEventOccurrenceKind;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar design §2.2 and §3.1: one date of a repeating event, cancelled or moved in Modbot, is
/// sent to that date's own id on VRChat -- never to the series' id.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarVRChatDatesTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly TimeSpan Settle = CalendarVRChatPublisher.SettleFor + TimeSpan.FromSeconds(1);

    /// <summary>A weekly event, published to VRChat as the series <c>cal_1</c>, whose first two dates VRChat lists apart.</summary>
    private async Task<CalendarEvent> PublishedWeeklyAsync()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Repeat = CalendarRepeats.Weekly;
        });

        await ScheduleAsync();
        Clock.Advance(Settle);
        Assert.Equal("create", (await PublishAsync()).Action);

        // VRChat's month list: the series' dates, each with an id of its own.
        VRChat.Calendar.OnVRChat.Clear();
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("occ_1", e.Title, e.StartsAt, TimeSpan.FromHours(2), Clock.UtcNow, VRChatKind.Occurrence, "cal_1"));
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("occ_2", e.Title, e.StartsAt + TimeSpan.FromDays(7), TimeSpan.FromHours(2), Clock.UtcNow, VRChatKind.Occurrence, "cal_1"));

        Clock.Advance(TimeSpan.FromMinutes(2));
        return e;
    }

    private async Task ChangeDateAsync(Guid id, DateTimeOffset planned, Action<CalendarDateChange> change)
    {
        await using var context = Database.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);

        var row = new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = id,
            PlannedStartsAt = planned,
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow,
        };

        change(row);
        e.DateChanges.Add(row);
        e.Version++;
        e.UpdatedAt = Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<CalendarDateChange> DateAsync(Guid id)
    {
        await using var context = Database.NewContext();
        return await context.CalendarDateChanges.AsNoTracking().SingleAsync(c => c.EventId == id, Ct);
    }

    [Fact]
    public async Task ACancelledDateIsDeletedByItsOwnId_NotTheSeries()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);

        await ChangeDateAsync(e.Id, second, c => c.Cancelled = true);

        // Waits for the change to settle, as an edit does.
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Empty(VRChat.Calendar.Deletes);

        Clock.Advance(Settle);
        var result = await PublishAsync();

        Assert.Equal(CalendarPublishOutcome.Written, result.Outcome);
        Assert.Equal("date", result.Action);
        Assert.Equal(["occ_2"], VRChat.Calendar.Deletes);

        var date = await DateAsync(e.Id);
        Assert.Equal("occ_2", date.VRChatId);
        Assert.NotNull(date.VRChatSentFingerprint);
        Assert.Null(date.VRChatError);

        // Sent once.
        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Single(VRChat.Calendar.Deletes);
    }

    [Fact]
    public async Task AMovedDateIsUpdatedByItsOwnId_WithItsNewTimesAndNoRepeat()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);
        var moved = second + TimeSpan.FromHours(1);

        await ChangeDateAsync(e.Id, second, c =>
        {
            c.StartsAt = moved;
            c.EndsAt = moved + TimeSpan.FromHours(3);
            c.Title = "Double feature";
        });

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var (id, body) = Assert.Single(VRChat.Calendar.Updates);
        Assert.Equal("occ_2", id);
        Assert.Equal(moved.UtcDateTime, body.StartsAt);
        Assert.Equal((moved + TimeSpan.FromHours(3)).UtcDateTime, body.EndsAt);
        Assert.Equal("Double feature", body.Title);
        Assert.Null(body.Recurrence);

        // The series was not touched.
        Assert.DoesNotContain(VRChat.Calendar.Updates, u => u.Id == "cal_1");
    }

    [Fact]
    public async Task ADatePutBackAsPlannedIsSentBackToVRChat_ThenForgotten()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);
        var moved = second + TimeSpan.FromHours(1);

        await ChangeDateAsync(e.Id, second, c =>
        {
            c.StartsAt = moved;
            c.EndsAt = moved + TimeSpan.FromHours(2);
        });

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        // Undo: the date is put back as planned. The row stays, because VRChat still has the move.
        await using (var context = Database.NewContext())
        {
            var row = await context.CalendarDateChanges.SingleAsync(c => c.EventId == e.Id, Ct);
            row.StartsAt = null;
            row.EndsAt = null;
            row.UpdatedAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var (id, body) = VRChat.Calendar.Updates[^1];
        Assert.Equal("occ_2", id);
        Assert.Equal(second.UtcDateTime, body.StartsAt);
        Assert.Equal("Movie night", body.Title);

        // Sent back: nothing of its own is left.
        await using var after = Database.NewContext();
        Assert.False(await after.CalendarDateChanges.AnyAsync(c => c.EventId == e.Id, Ct));
    }

    [Fact]
    public async Task ADateVRChatDoesNotListOnItsOwnIsShownAsFailed_AndTheSeriesIsLeftAlone()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);

        // Only the series is listed: no id for that one date.
        VRChat.Calendar.OnVRChat.Clear();
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("cal_1", e.Title, e.StartsAt, TimeSpan.FromHours(2), Clock.UtcNow, VRChatKind.Series));

        await ChangeDateAsync(e.Id, second, c =>
        {
            c.StartsAt = second + TimeSpan.FromHours(1);
            c.EndsAt = second + TimeSpan.FromHours(3);
        });

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);

        Assert.Empty(VRChat.Calendar.Updates);
        Assert.Empty(VRChat.Calendar.Deletes);
        Assert.NotNull((await DateAsync(e.Id)).VRChatError);

        // Not looked for again every pass while the change is the same.
        var lists = VRChat.Calendar.Lists;
        Clock.Advance(TimeSpan.FromMinutes(20));
        await PublishAsync();
        Assert.Equal(lists, VRChat.Calendar.Lists);
    }

    [Fact]
    public async Task ACancelledDateNotFoundOnVRChatIsShownAsFailed_NotTakenAsSent()
    {
        var e = await PublishedWeeklyAsync();
        VRChat.Calendar.OnVRChat.RemoveAll(r => r.Id == "occ_2");

        await ChangeDateAsync(e.Id, e.StartsAt + TimeSpan.FromDays(7), c => c.Cancelled = true);

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);

        Assert.Empty(VRChat.Calendar.Deletes);
        var date = await DateAsync(e.Id);
        Assert.Null(date.VRChatSentFingerprint);
        Assert.NotNull(date.VRChatError);
    }

    [Fact]
    public async Task ADateOnALaterPageOfTheMonthIsFound()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);

        // Other events fill the first page; the date is on the second.
        VRChat.Calendar.OnVRChat.InsertRange(0, Enumerable.Range(1, 4)
            .Select(i => FakeCalendar.Made($"other_{i}", "Something else", e.StartsAt + TimeSpan.FromHours(i), TimeSpan.FromHours(1), Clock.UtcNow)));
        VRChat.Calendar.PageSize = 3;

        await ChangeDateAsync(e.Id, second, c => c.Cancelled = true);

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal("occ_2", Assert.Single(VRChat.Calendar.Deletes));
    }

    [Fact]
    public async Task CancellingTheLastDateStillTakesItOffVRChat_ThoughTheEventIsFinished()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Repeat = CalendarRepeats.Daily;
            x.RepeatUntil = DateOnly.FromDateTime((x.StartsAt + TimeSpan.FromDays(1)).UtcDateTime);
        });

        await ScheduleAsync();
        Clock.Advance(Settle);
        Assert.Equal("create", (await PublishAsync()).Action);

        var last = e.StartsAt + TimeSpan.FromDays(1);
        VRChat.Calendar.OnVRChat.Clear();
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("occ_last", e.Title, last, TimeSpan.FromHours(2), Clock.UtcNow, VRChatKind.Occurrence, "cal_1"));

        // The first date has run; the last is cancelled, so nothing is left and the event finishes.
        Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(3));
        await ChangeDateAsync(e.Id, last, c => c.Cancelled = true);
        await ScheduleAsync();
        Assert.Equal(CalendarEventStates.Finished, (await EventsAsync()).Single(x => x.Id == e.Id).State);

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal("occ_last", Assert.Single(VRChat.Calendar.Deletes));
    }
}
