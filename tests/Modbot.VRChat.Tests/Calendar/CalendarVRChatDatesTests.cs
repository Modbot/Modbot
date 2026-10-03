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

    /// <summary>
    /// Google Calendar design §3.5, the one shared-code trap: VRChat dropping a put-back date's row
    /// as soon as it had the plain date would leave Google's moved copy there for good.
    /// </summary>
    [Fact]
    public async Task ADatePutBackAsPlanned_KeepsItsRowWhileGoogleStillHoldsTheMove()
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

        // Google was sent the move too; then the date is put back as planned.
        await using (var context = Database.NewContext())
        {
            var row = await context.CalendarDateChanges.SingleAsync(c => c.EventId == e.Id, Ct);
            row.GoogleSentFingerprint = "sent-to-google";
            row.StartsAt = null;
            row.EndsAt = null;
            row.UpdatedAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        // VRChat has the planned date back and holds it as planned; the row stays for Google.
        var date = await DateAsync(e.Id);
        Assert.Equal(Modbot.Core.Calendar.CalendarDates.VRChatHasPlannedDate, date.VRChatSentFingerprint);
        Assert.Null(date.VRChatId);
        Assert.False(Modbot.Core.Calendar.CalendarDates.MayBeOnVRChat(date));
        Assert.Equal("sent-to-google", date.GoogleSentFingerprint);

        // And VRChat is not sent the plain date again and again.
        var updates = VRChat.Calendar.Updates.Count;
        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(updates, VRChat.Calendar.Updates.Count);

        // A series write: the plain date is sent to VRChat again, as it always was, rather than
        // assuming VRChat put it back on its own.
        await EditAsync(e.Id, x => x.Description = "Bring snacks and a blanket");
        Clock.Advance(Settle + TimeSpan.FromMinutes(1));
        Assert.Equal("update", (await PublishAsync()).Action);
        Assert.Equal("cal_1", VRChat.Calendar.Updates[^1].Id);

        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var (id, body) = VRChat.Calendar.Updates[^1];
        Assert.Equal("occ_2", id);
        Assert.Equal(second.UtcDateTime, body.StartsAt);
    }

    /// <summary>
    /// With Google not involved, VRChat behaves as it did before Google Calendar: after a series
    /// write a put-back date is sent again, and its row goes once VRChat has it.
    /// </summary>
    [Fact]
    public async Task APutBackDateIsSentAgainAfterASeriesWrite_ThenForgotten()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);

        // Put back as planned, VRChat still holding the move: kept for VRChat.
        await ChangeDateAsync(e.Id, second, c =>
        {
            c.VRChatSentStartsAt = second + TimeSpan.FromHours(1);
            c.VRChatSentFingerprint = "sent before";
        });

        // The series is written first; the date is then looked for and sent again.
        await EditAsync(e.Id, x => x.Description = "Bring snacks and a blanket");
        Clock.Advance(Settle + TimeSpan.FromMinutes(1));
        Assert.Equal("update", (await PublishAsync()).Action);

        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal("occ_2", VRChat.Calendar.Updates[^1].Id);

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
    public async Task AKeptIdIsNeverWrittenTo_TheDateIsLookedUpFirst()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);

        // An id kept from an earlier pass, which VRChat no longer lists; the date is there under a new one.
        await ChangeDateAsync(e.Id, second, c =>
        {
            c.Cancelled = true;
            c.VRChatId = "occ_stale";
        });

        var lists = VRChat.Calendar.Lists;
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        Assert.True(VRChat.Calendar.Lists > lists);
        Assert.Equal("occ_2", Assert.Single(VRChat.Calendar.Deletes));
        Assert.Equal("occ_2", (await DateAsync(e.Id)).VRChatId);
    }

    [Fact]
    public async Task AnAllDatesEditAfterAMove_FindsTheMovedDateByItsNewIdAndSendsTheMoveAgain()
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
        Assert.Equal("occ_2", VRChat.Calendar.Updates[^1].Id);

        // "All dates": the description of the whole series changes.
        await EditAsync(e.Id, x => x.Description = "Bring snacks and a blanket");
        Clock.Advance(Settle + TimeSpan.FromMinutes(1));
        Assert.Equal("update", (await PublishAsync()).Action);
        Assert.Equal("cal_1", VRChat.Calendar.Updates[^1].Id);

        // VRChat makes the series' dates afresh: new ids, and the moved date back at its planned time.
        VRChat.Calendar.OnVRChat.Clear();
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("occ_1b", e.Title, e.StartsAt, TimeSpan.FromHours(2), Clock.UtcNow, VRChatKind.Occurrence, "cal_1"));
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("occ_2b", e.Title, second, TimeSpan.FromHours(2), Clock.UtcNow, VRChatKind.Occurrence, "cal_1"));

        var date = await DateAsync(e.Id);
        Assert.Null(date.VRChatId);
        Assert.Null(date.VRChatSentFingerprint);

        // The move goes out again, to the new id.
        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var (id, body) = VRChat.Calendar.Updates[^1];
        Assert.Equal("occ_2b", id);
        Assert.Equal(moved.UtcDateTime, body.StartsAt);
        Assert.DoesNotContain(VRChat.Calendar.Updates.Skip(2), u => u.Id == "occ_2");
    }

    [Fact]
    public async Task APutBackDateIsFoundWhereVRChatLastHadIt()
    {
        var e = await PublishedWeeklyAsync();
        var second = e.StartsAt + TimeSpan.FromDays(7);
        var movedTo = second + TimeSpan.FromHours(1);

        // VRChat lists the date where Modbot last sent it; the row is put back and its id forgotten.
        VRChat.Calendar.OnVRChat.RemoveAll(r => r.Id == "occ_2");
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("occ_2", e.Title, movedTo, TimeSpan.FromHours(2), Clock.UtcNow, VRChatKind.Occurrence, "cal_1"));

        await ChangeDateAsync(e.Id, second, c =>
        {
            c.VRChatSentStartsAt = movedTo;
            c.VRChatSentFingerprint = "sent before";
        });

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var (id, body) = Assert.Single(VRChat.Calendar.Updates);
        Assert.Equal("occ_2", id);
        Assert.Equal(second.UtcDateTime, body.StartsAt);
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
