using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>Calendar design §12: VRChat's calendar read back into Modbot, only when someone asks.</summary>
[Collection(nameof(PostgresCollection))]
public class CalendarVRChatReaderTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly TimeSpan Settle = CalendarVRChatPublisher.SettleFor + TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private global::VRChat.API.Model.CalendarEvent WatchParty(DateTimeOffset? updatedAt = null, bool featured = false) =>
        FakeCalendar.Made("cal_party", "Watch Party", Clock.UtcNow.AddDays(2), TwoHours, updatedAt ?? Clock.UtcNow.AddDays(-1), featured: featured);

    [Fact]
    public async Task AnEventMadeOnVRChatIsTakenIn_AndNotSentBack()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty(featured: true));

        Assert.Equal(CalendarReadOutcome.Read, (await ReadAsync()).Outcome);

        var e = Assert.Single(await EventsAsync());
        Assert.True(e.MadeOnVRChat);
        Assert.Equal("Watch Party", e.Title);
        Assert.Equal(CalendarEventStates.Scheduled, e.State);
        Assert.True(e.PublishToVRChat);
        Assert.False(e.PublishToDiscord);
        Assert.False(e.AutoOpen);
        Assert.Equal("public", e.Visibility);
        Assert.Equal("film_media", e.Category);
        Assert.True(e.VRChatFeatured);
        Assert.Equal(60, e.VRChatHostEarlyJoinMinutes);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal("cal_party", place?.ExternalId);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Calls);

        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventCreated));
    }

    [Fact]
    public async Task ARepeatingEventIsTakenInFromItsSeries()
    {
        var first = Clock.UtcNow.AddDays(-14);
        var rule = new CalendarEventRecurrence(frequency: CalendarEventFrequency.Weekly, interval: 1, timezone: "America/New_York");
        VRChat.Calendar.Series.Add(FakeCalendar.Made(
            "cal_series", "Hangout", first, TimeSpan.FromHours(4), first, CalendarEventOccurrenceKind.Series, recurrence: rule));

        foreach (var week in new[] { 1, 2 })
        {
            VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made(
                $"cal_date_{week}", "Hangout", first.AddDays(7 * (2 + week)), TimeSpan.FromHours(4), first,
                CalendarEventOccurrenceKind.Occurrence, seriesId: "cal_series"));
        }

        await ReadAsync();

        var e = Assert.Single(await EventsAsync());
        Assert.Equal(CalendarRepeats.Weekly, e.Repeat);
        Assert.Equal("America/New_York", e.TimeZone);
        Assert.Equal(first, e.StartsAt);
        Assert.Equal("cal_series", (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.ExternalId);
        Assert.Equal(1, VRChat.Calendar.Gets);
    }

    [Fact]
    public async Task AMonthReadInTheLastFiveMinutesIsNotAskedAgain_ButRefreshAsks()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty());

        await ReadAsync();
        Assert.Equal(CalendarReadOutcome.Remembered, (await ReadAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Lists);

        Assert.Equal(CalendarReadOutcome.Read, (await ReadAsync(refresh: true)).Outcome);
        Assert.Equal(2, VRChat.Calendar.Lists);

        Clock.Advance(CalendarVRChatReadMemory.KeepFor);
        await ReadAsync();
        Assert.Equal(3, VRChat.Calendar.Lists);

        // Read three times, taken in once.
        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task AChangeMadeOnVRChatIsCopiedIn_AndNotSentBack()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty());
        await ReadAsync();

        Clock.Advance(TimeSpan.FromMinutes(1));
        VRChat.Calendar.OnVRChat.Clear();
        var changed = WatchParty(updatedAt: Clock.UtcNow);
        changed.Title = "Watch Party: The Weird";
        VRChat.Calendar.OnVRChat.Add(changed);

        await ReadAsync(refresh: true);

        var e = Assert.Single(await EventsAsync());
        Assert.Equal("Watch Party: The Weird", e.Title);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventChanged));

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Empty(VRChat.Calendar.Updates);
    }

    [Fact]
    public async Task AnEditInModbotNewerThanVRChatsChangeWins()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty());
        await ReadAsync();

        // Changed on VRChat first, then in Modbot before that change was read.
        Clock.Advance(TimeSpan.FromMinutes(1));
        VRChat.Calendar.OnVRChat.Clear();
        var changed = WatchParty(updatedAt: Clock.UtcNow);
        changed.Title = "From VRChat";
        VRChat.Calendar.OnVRChat.Add(changed);

        Clock.Advance(TimeSpan.FromSeconds(5));
        var id = Assert.Single(await EventsAsync()).Id;
        await EditAsync(id, x => x.Title = "From Modbot");

        await ReadAsync(refresh: true);
        Assert.Equal("From Modbot", Assert.Single(await EventsAsync()).Title);

        Clock.Advance(Settle);
        await PublishAsync();
        var (sentTo, body) = Assert.Single(VRChat.Calendar.Updates);
        Assert.Equal("cal_party", sentTo);
        Assert.Equal("From Modbot", body.Title);
    }

    [Fact]
    public async Task AnUpdateFromModbotSendsVRChatsOwnSettingsBack()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty(featured: true));
        await ReadAsync();

        var id = Assert.Single(await EventsAsync()).Id;
        await EditAsync(id, x => x.Title = "Renamed");
        Clock.Advance(Settle);
        await PublishAsync();

        var (_, body) = Assert.Single(VRChat.Calendar.Updates);
        Assert.True(body.Featured);
        Assert.Equal(60, body.HostEarlyJoinMinutes);
        Assert.Equal(5, body.GuestEarlyJoinMinutes);
    }

    [Fact]
    public async Task DeletedOnVRChat_IsDeletedInModbotOnceVRChatSaysSo()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty());
        await ReadAsync();

        VRChat.Calendar.OnVRChat.Clear();
        await ReadAsync(refresh: true);

        // Looked up on its own before anything happened.
        Assert.Equal(1, VRChat.Calendar.Gets);

        var e = Assert.Single(await EventsAsync());
        Assert.NotNull(e.DeletedAt);
        Assert.Equal(CalendarEventStates.Cancelled, e.State);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Removed, place?.State);
        Assert.Null(place?.ExternalId);

        // Already gone from VRChat: nothing is sent to take it off.
        Clock.Advance(Settle);
        await PublishAsync();
        Assert.Empty(VRChat.Calendar.Deletes);

        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventDeleted));
    }

    [Fact]
    public async Task MovedToAnotherMonthOnVRChat_IsNotDeleted()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty());
        await ReadAsync();

        // Gone from this month's list, but still there when looked up on its own.
        Clock.Advance(TimeSpan.FromMinutes(2));
        var moved = WatchParty(updatedAt: Clock.UtcNow);
        moved.StartsAt = Clock.UtcNow.AddDays(60).UtcDateTime;
        moved.EndsAt = Clock.UtcNow.AddDays(60).Add(TwoHours).UtcDateTime;
        VRChat.Calendar.OnVRChat.Clear();
        VRChat.Calendar.Series.Add(moved);

        await ReadAsync(refresh: true);

        var e = Assert.Single(await EventsAsync());
        Assert.Null(e.DeletedAt);
        Assert.Equal(moved.StartsAt, e.StartsAt.UtcDateTime);
    }

    [Fact]
    public async Task AFailedReadDeletesNothing()
    {
        VRChat.Calendar.OnVRChat.Add(WatchParty());
        await ReadAsync();

        VRChat.Calendar.OnVRChat.Clear();
        VRChat.Calendar.ListStatus = HttpStatusCode.InternalServerError;

        Assert.Equal(CalendarReadOutcome.Failed, (await ReadAsync(refresh: true)).Outcome);
        Assert.Null(Assert.Single(await EventsAsync()).DeletedAt);
        Assert.Equal(0, VRChat.Calendar.Gets);
    }

    [Fact]
    public async Task A429IsNotRetried()
    {
        VRChat.Calendar.ListStatus = HttpStatusCode.TooManyRequests;

        Assert.Equal(CalendarReadOutcome.Waiting, (await ReadAsync()).Outcome);

        // Asked again straight away: the gate refuses without sending.
        Assert.Equal(CalendarReadOutcome.Waiting, (await ReadAsync(refresh: true)).Outcome);
        Assert.Equal(1, VRChat.Calendar.Lists);
    }

    [Fact]
    public async Task AnEventModbotPublishedIsNotTakenInAgain()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        await PublishAsync();
        Assert.Single(VRChat.Calendar.OnVRChat);

        await ReadAsync();

        Assert.Equal(e.Id, Assert.Single(await EventsAsync()).Id);
    }

    [Fact]
    public async Task AnEventModbotIsStillCreatingIsNotTakenIn()
    {
        // VRChat saved the create but answered with a 500: the event is there, and Modbot has no id.
        await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.SaveButAnswer(HttpStatusCode.InternalServerError);
        await PublishAsync();

        await ReadAsync();

        Assert.Single(await EventsAsync());
    }
}
