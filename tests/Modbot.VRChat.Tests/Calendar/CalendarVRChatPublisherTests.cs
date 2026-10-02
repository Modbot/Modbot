using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;

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

    /// <summary>
    /// Live updates (2026-10-01): the place getting onto VRChat's calendar writes one fact for the
    /// live stream to carry, so the calendar page shows it without a reload. An edit afterwards
    /// writes none, however many passes it takes to settle (the place waits between them);
    /// taking it off VRChat writes one of its own.
    /// </summary>
    [Fact]
    public async Task PublishingAndTakingDownEachWriteOneFact_AnUpdateWritesNone()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        await PublishAsync();

        var published = Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventPublished));
        Assert.Equal(e.Id.ToString(), published.SubjectId);
        Assert.Contains(CalendarPlaces.VRChat, published.Data, StringComparison.Ordinal);

        // The edit is still settling: the place waits (and that pass is saved), then goes out.
        await EditAsync(e.Id, x => x.Title = "Movie night: Alien");
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventPublished));

        await EditAsync(e.Id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = Clock.UtcNow;
        });
        Assert.Equal("delete", (await PublishAsync()).Action);

        var takenDown = Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventTakenDown));
        Assert.Contains("\"was\"", takenDown.Data, StringComparison.Ordinal);
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

    /// <summary>
    /// Seen three times on 2026-10-01: VRChat answered a create with a 500, made the event anyway,
    /// and changed its title (en dash dropped, "." turned into a look-alike dot). The copy is taken
    /// as the event's own: one create, no second one, and no second event taken in by the read.
    /// </summary>
    [Fact]
    public async Task ACreateAnsweredWith500ThatWasSavedAnywayIsTakenAsItsOwn_NotMadeTwice()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Title = "Movie night – Alien.";
        });
        VRChat.Calendar.SavesTitleAs = VRChatsTitle;
        Clock.Advance(Settle);
        VRChat.Calendar.SaveButAnswer(HttpStatusCode.InternalServerError);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Lists);

        // Not known to have failed: the place waits, keeps what was sent, and no failure is written.
        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Waiting, place?.State);
        Assert.Null(place?.Error);
        Assert.NotNull(place?.CreateSent);
        Assert.False(CalendarVRChatPublisher.NotAdded(place!));
        Assert.Empty(await FactsOfTypeAsync(FactType.PlannedEventPublishFailed));

        // Not looked for before the wait is over.
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(0, VRChat.Calendar.Lists);

        Clock.Advance(CalendarVRChatPublisher.LookAfter);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);

        Assert.Equal(1, VRChat.Calendar.Lists);
        Assert.Single(VRChat.Calendar.Creates);

        place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal("cal_1", place?.ExternalId);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);
        Assert.Null(place?.Error);
        Assert.Null(place?.CreateSent);
        Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventPublished));
        Assert.Empty(await FactsOfTypeAsync(FactType.PlannedEventPublishFailed));

        // Not edited since: VRChat has what was sent, so nothing more goes out.
        Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);

        // The calendar read finds it owned, and takes nothing in.
        await ReadAsync(refresh: true);
        Assert.Equal(e.Id, Assert.Single(await EventsAsync()).Id);
    }

    /// <summary>
    /// Review of 2026-10-01: VRChat's copy holds what the create sent, so a title and time fixed
    /// during the wait must not stop the copy being found. It is looked for as sent, adopted, and
    /// the fix goes out as an update.
    /// </summary>
    [Fact]
    public async Task ATitleAndTimeFixedWhileACreateHadNoAnswerGoOutAsAnUpdateOnceItsCopyIsFound()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Title = "Movie night – Alien.";
        });
        VRChat.Calendar.SavesTitleAs = VRChatsTitle;
        Clock.Advance(Settle);
        VRChat.Calendar.SaveButAnswer(HttpStatusCode.InternalServerError);
        await PublishAsync();

        Clock.Advance(TimeSpan.FromSeconds(30));
        var later = e.StartsAt.AddHours(1);
        await EditAsync(e.Id, x =>
        {
            x.Title = "Movie night – Aliens.";
            x.StartsAt = later;
            x.EndsAt = later.AddHours(2);
            x.OccurrenceStartsAt = later;
        });

        Clock.Advance(CalendarVRChatPublisher.LookAfter);
        await PublishAsync();

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal("cal_1", place?.ExternalId);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);
        Assert.Empty(await FactsOfTypeAsync(FactType.PlannedEventPublishFailed));

        var result = await PublishAsync();

        Assert.Equal("update", result.Action);
        var (id, body) = Assert.Single(VRChat.Calendar.Updates);
        Assert.Equal("cal_1", id);
        Assert.Equal("Movie night – Aliens.", body.Title);
        Assert.Equal(later.UtcDateTime, body.StartsAt);
        Assert.Single(VRChat.Calendar.Creates);

        // Nothing taken in as a second event.
        await ReadAsync(refresh: true);
        Assert.Equal(e.Id, Assert.Single(await EventsAsync()).Id);
    }

    [Fact]
    public async Task TryAgainLooksOnceMore_AndTakesACopyThatTurnedUpInsteadOfSendingAgain()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        var sentAt = Clock.UtcNow;
        VRChat.Calendar.Answer(HttpStatusCode.InternalServerError);
        await PublishAsync();

        Clock.Advance(CalendarVRChatPublisher.LookAfter);
        await PublishAsync();
        Assert.True(CalendarVRChatPublisher.NotAdded((await PlaceAsync(e.Id, CalendarPlaces.VRChat))!));

        // VRChat shows it late, made when the create was sent.
        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("cal_late", "Movie night", e.StartsAt, TimeSpan.FromHours(2), sentAt.AddSeconds(5)));

        Assert.True(await TryAgainAsync(e.Id));
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);

        Assert.Equal(2, VRChat.Calendar.Lists);
        Assert.Equal(1, VRChat.Calendar.Calls);
        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal("cal_late", place?.ExternalId);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);
    }

    [Fact]
    public async Task ACreateAnsweredWith500ThatWasNotSavedIsNeverSentAgainOnItsOwn_OnlyByTryAgain()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        VRChat.Calendar.Answer(HttpStatusCode.InternalServerError);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Empty(await FactsOfTypeAsync(FactType.PlannedEventPublishFailed));

        // Looked for once, after the wait, across the whole month: not there.
        Clock.Advance(CalendarVRChatPublisher.LookAfter);
        var result = await PublishAsync();

        Assert.Equal(CalendarPublishOutcome.Failed, result.Outcome);
        Assert.Equal(1, VRChat.Calendar.Lists);
        Assert.Equal(1, VRChat.Calendar.Calls);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Failed, place?.State);
        Assert.Equal(CalendarVRChatPublisher.NotAddedError, place?.Error);
        Assert.True(CalendarVRChatPublisher.NotAdded(place!));

        // The one failure fact, written when it is known not added.
        var fact = Assert.Single(await FactsOfTypeAsync(FactType.PlannedEventPublishFailed));
        Assert.Equal(CalendarVRChatPublisher.NotAddedError, System.Text.Json.Nodes.JsonNode.Parse(fact.Data)?["error"]?.ToString());

        // Not sent again however long it waits, nor after an edit, and not looked for again.
        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter * 4);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        await EditAsync(e.Id, x => x.Description = "Bring snacks and a blanket");
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);
        Assert.Equal(1, VRChat.Calendar.Lists);

        // A moderator's Try again looks once more, then sends it.
        Assert.True(await TryAgainAsync(e.Id));
        Assert.False(CalendarVRChatPublisher.NotAdded((await PlaceAsync(e.Id, CalendarPlaces.VRChat))!));
        result = await PublishAsync();

        Assert.Equal(CalendarPublishOutcome.Written, result.Outcome);
        Assert.Equal("create", result.Action);
        Assert.Equal(2, VRChat.Calendar.Lists);
        Assert.Equal("cal_1", (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.ExternalId);

        // Nothing to try again once it went through.
        Assert.False(await TryAgainAsync(e.Id));
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

        // Still looked for, not given up on. Once the calendar can be read and the event is not
        // there, the place fails and waits for Try again: the create is not sent on its own.
        VRChat.Calendar.ListStatus = HttpStatusCode.OK;
        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter + TimeSpan.FromSeconds(1));

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(2, VRChat.Calendar.Lists);
        Assert.Equal(1, VRChat.Calendar.Calls);
        Assert.Equal(CalendarVRChatPublisher.NotAddedError, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.Error);
    }

    /// <summary>What VRChat did to titles on 2026-10-01: dropped an en dash, and turned "." into "․".</summary>
    private static string VRChatsTitle(string title) => title.Replace(" – ", "  ", StringComparison.Ordinal).Replace('.', '․');

    private async Task<bool> TryAgainAsync(Guid id)
    {
        await using var context = Database.NewContext();
        var place = await context.CalendarEventPlaces.SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.VRChat, Ct);
        var tried = CalendarVRChatPublisher.TryAgain(place, Clock.UtcNow);
        await context.SaveChangesAsync(Ct);
        return tried;
    }

    /// <summary>
    /// VRChat's calendar turned off and on again: the place row left from the take-down is used
    /// again. A second row for the same event broke the key (event, place) and stopped every pass
    /// saving, as the Discord loop's did until staging's eea76aba (fixed here 2026-10-02).
    /// </summary>
    [Fact]
    public async Task TurningVRChatOffAndOnAgain_UsesThePlaceLeftBehind_AndSendsTheEventAgain()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        await PublishAsync();

        await EditAsync(e.Id, x => x.PublishToVRChat = false);
        Assert.Equal("delete", (await PublishAsync()).Action);
        Assert.Equal(CalendarPlaceStates.Removed, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);

        await EditAsync(e.Id, x => x.PublishToVRChat = true);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(CalendarPlaceStates.Waiting, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);

        Clock.Advance(Settle);
        var result = await PublishAsync();

        Assert.Equal(CalendarPublishOutcome.Written, result.Outcome);
        Assert.Equal("create", result.Action);
        Assert.Equal(2, VRChat.Calendar.Creates.Count);

        await using var context = Database.NewContext();
        var place = Assert.Single(await context.CalendarEventPlaces.AsNoTracking()
            .Where(p => p.EventId == e.Id && p.Place == CalendarPlaces.VRChat)
            .ToListAsync(Ct));
        Assert.Equal(CalendarPlaceStates.Published, place.State);
        Assert.Equal("cal_2", place.ExternalId);
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
