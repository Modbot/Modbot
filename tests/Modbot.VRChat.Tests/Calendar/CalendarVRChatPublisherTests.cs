using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>Calendar design §3.1: VRChat's calendar, written gently.</summary>
[Collection(nameof(PostgresCollection))]
public class CalendarVRChatPublisherTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly TimeSpan Settle = CalendarVRChatPublisher.SettleFor + TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ANewEventIsCreatedOnceItHasSettled_AndVRChatsIdIsKept()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);

        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Empty(VRChat.Calendar.Creates);
        Assert.Equal(CalendarPlaceStates.Waiting, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);

        Clock.Advance(Settle);
        var result = await PublishAsync();

        Assert.Equal(CalendarPublishOutcome.Written, result.Outcome);
        Assert.Equal("create", result.Action);
        Assert.Equal("Movie night", Assert.Single(VRChat.Calendar.Creates).Title);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);
        Assert.Equal("cal_1", place?.ExternalId);

        // Nothing changed, nothing sent.
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);
    }

    [Fact]
    public async Task QuickEditsBecomeOneUpdate()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        await PublishAsync();

        foreach (var title in new[] { "Movie nite", "Movie night!", "Movie night: Alien" })
        {
            await EditAsync(e.Id, x => x.Title = title);
            Clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        }

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var (id, body) = Assert.Single(VRChat.Calendar.Updates);
        Assert.Equal("cal_1", id);
        Assert.Equal("Movie night: Alien", body.Title);
    }

    [Fact]
    public async Task A429ColdStopsTheCalendarAndIsNotRetried()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.TooManyRequests);

        Assert.Equal(CalendarPublishOutcome.RateLimited, (await PublishAsync()).Outcome);
        Assert.Equal(CalendarPlaceStates.Waiting, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);

        // The next passes ask the gate again, and the gate refuses without sending anything.
        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(CalendarPublishOutcome.RateLimited, (await PublishAsync()).Outcome);
        }

        Assert.Equal(1, VRChat.Calendar.Calls);

        var bucket = (await Limiter.Limiter.DescribeAsync(Ct)).Single(b => b.EndpointClass == VRChatEndpointClass.CalendarWrite && b.IsColdStopped);
        Assert.Equal(GroupId, bucket.ResourceId);

        // Once the stop's own wait is over, the single probe is the queued write.
        Clock.Advance(new global::Modbot.VRChat.RateLimiting.RateLimitOptions().ColdStopBase + TimeSpan.FromSeconds(1));
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal(2, VRChat.Calendar.Calls);
    }

    [Fact]
    public async Task ARefusalIsNotSentAgainUntilTheEventChanges()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.BadRequest);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventPublishFailed));

        await EditAsync(e.Id, x => x.Description = "Fixed");
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
    }

    [Fact]
    public async Task CancellingDeletesTheEventOnVRChat()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        await PublishAsync();

        await EditAsync(e.Id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = Clock.UtcNow;
        });

        var result = await PublishAsync();

        Assert.Equal("delete", result.Action);
        Assert.Equal("cal_1", Assert.Single(VRChat.Calendar.Deletes));

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Removed, place?.State);
        Assert.Null(place?.ExternalId);
    }

    [Fact]
    public async Task AWeeklyEventIsOneSeriesWithVRChatsOwnRecurrence()
    {
        await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Repeat = CalendarRepeats.Weekly;
            x.TimeZone = "Europe/London";
            x.RepeatUntil = DateOnly.FromDateTime(x.StartsAt.UtcDateTime.AddDays(60));
        });

        Clock.Advance(Settle);
        await PublishAsync();

        var body = Assert.Single(VRChat.Calendar.Creates);
        Assert.Equal(global::VRChat.API.Model.CalendarEventFrequency.Weekly, body.Recurrence.Frequency);
        Assert.Equal("Europe/London", body.Recurrence.Timezone);
        Assert.Single(body.Recurrence.DaysOfWeek);
        Assert.Equal(global::VRChat.API.Model.CalendarEventRecurrenceEndType.AfterDate, body.Recurrence.End.Type);
    }
}
