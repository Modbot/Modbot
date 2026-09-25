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
    public async Task ARefusalKeepsVRChatsOwnWords()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Refuse(HttpStatusCode.BadRequest, "description is required");

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal("description is required", (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.Error);
    }

    [Fact]
    public async Task ACreateAnsweredWith500ThatWasSavedAnywayIsFound_NotMadeTwice()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.SaveButAnswer(HttpStatusCode.InternalServerError);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Lists);

        // Not asked again before the wait for an unanswered write is over.
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Lists);

        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter + TimeSpan.FromSeconds(1));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);

        Assert.Equal(1, VRChat.Calendar.Lists);
        Assert.Single(VRChat.Calendar.Creates);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal("cal_1", place?.ExternalId);
        Assert.Null(place?.Error);

        // What VRChat holds is not known, so it is brought up to date with an update, not a create.
        Clock.Advance(TimeSpan.FromMinutes(1));
        var result = await PublishAsync();

        Assert.Equal("update", result.Action);
        Assert.Equal("cal_1", Assert.Single(VRChat.Calendar.Updates).Id);
        Assert.Single(VRChat.Calendar.OnVRChat);
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);
    }

    [Fact]
    public async Task ACreateAnsweredWith500ThatWasNotSavedIsSentAgain()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.InternalServerError);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);

        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter + TimeSpan.FromSeconds(1));
        var result = await PublishAsync();

        Assert.Equal(CalendarPublishOutcome.Written, result.Outcome);
        Assert.Equal("create", result.Action);
        Assert.Equal(1, VRChat.Calendar.Lists);
        Assert.Single(VRChat.Calendar.OnVRChat);
        Assert.Equal("cal_1", (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.ExternalId);
    }

    [Fact]
    public async Task AnEventLetGoAfterA500IsTakenOffVRChatIfTheCreateWentThrough()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.SaveButAnswer(HttpStatusCode.InternalServerError);
        await PublishAsync();

        await EditAsync(e.Id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = Clock.UtcNow;
            x.DeletedAt = Clock.UtcNow;
        });

        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter + TimeSpan.FromSeconds(1));
        var result = await PublishAsync();

        Assert.Equal("delete", result.Action);
        Assert.Equal("cal_1", Assert.Single(VRChat.Calendar.Deletes));
        Assert.Empty(VRChat.Calendar.OnVRChat);
        Assert.Equal(CalendarPlaceStates.Removed, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);
    }

    [Fact]
    public async Task NothingIsSentAgainWhileTheCalendarCannotBeChecked()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.InternalServerError);
        await PublishAsync();

        VRChat.Calendar.ListStatus = HttpStatusCode.Forbidden;
        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter + TimeSpan.FromSeconds(1));

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);
        Assert.StartsWith("Could not check VRChat's calendar", (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.Error);

        // The fact says it was the check that failed, not a write: nothing was written.
        var held = (await FactsOfTypeAsync(FactType.PlannedEventPublishFailed)).Last();
        Assert.Equal("check", System.Text.Json.Nodes.JsonNode.Parse(held.Data)?["action"]?.ToString());

        // Still looked for, not given up on: once the calendar can be read, the create goes ahead.
        VRChat.Calendar.ListStatus = HttpStatusCode.OK;
        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter + TimeSpan.FromSeconds(1));

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Equal(2, VRChat.Calendar.Lists);
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
