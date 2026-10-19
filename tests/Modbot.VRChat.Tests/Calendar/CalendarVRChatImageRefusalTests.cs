using System.Net;
using System.Text.Json;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// "18+ Hangout", 2026-10-19: VRChat answered a write with 404 "Couldn't find the image you wanted
/// to use for your calendar entry" for a picture another user uploaded. The 404 was taken for
/// "deleted on VRChat", the place let go of its id, and the next pass made the event a second time.
/// A refused picture is a refusal, and an event made on VRChat is looked for before it is made again.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarVRChatImageRefusalTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly TimeSpan Settle = CalendarVRChatPublisher.SettleFor + TimeSpan.FromSeconds(1);

    private const string Refusal = "Couldn't find the image you wanted to use for your calendar entry.";

    private const string Picture = "file_5c3db95e-0000-4000-8000-000000000001";

    [Fact]
    public void OnlyA404ThatSpeaksOfTheImageIsAnImageRefusal()
    {
        var body = $"{{\"error\":{{\"message\":\"{Refusal}\",\"status_code\":404}}}}";

        Assert.True(CalendarVRChatRequests.IsImageRefusal(404, body, null));
        Assert.True(CalendarVRChatRequests.IsImageRefusal(404, null, Refusal));
        Assert.False(CalendarVRChatRequests.IsImageRefusal(404, "{\"error\":{\"message\":\"Calendar event not found\"}}", null));
        Assert.False(CalendarVRChatRequests.IsImageRefusal(403, body, null));
        Assert.False(CalendarVRChatRequests.IsImageRefusal(0, null, "image"));
    }

    [Fact]
    public async Task AnUpdateRefusedForItsPictureKeepsTheEventsIdAndFailsLikeAnyRefusal()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        await EditAsync(e.Id, x => x.Description = "Bring more snacks");
        Clock.Advance(Settle);
        VRChat.Calendar.Refuse(HttpStatusCode.NotFound, Refusal);

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Failed, place?.State);
        Assert.Equal("cal_1", place?.ExternalId);
        Assert.NotNull(place?.FailedFingerprint);
        Assert.Contains("image", place?.Error, StringComparison.OrdinalIgnoreCase);

        // Not sent again until the event changes, and never made a second time.
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Single(VRChat.Calendar.Creates);
        Assert.Equal(2, VRChat.Calendar.Calls);
    }

    [Fact]
    public async Task AnUpdateThatGetsAnyOther404IsStillTakenForDeletedOnVRChat()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x => x.PublishToVRChat = true);
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        await EditAsync(e.Id, x => x.Description = "Bring more snacks");
        Clock.Advance(Settle);
        VRChat.Calendar.Refuse(HttpStatusCode.NotFound, "Calendar event not found");

        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal(CalendarPlaceStates.Waiting, place?.State);
        Assert.Null(place?.ExternalId);
    }

    [Fact]
    public async Task AnEventMadeOnVRChatTakesItsOwnCopyInsteadOfMakingAnother()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.Title = "18+ Hangout";
            x.PublishToVRChat = true;
            x.MadeOnVRChat = true;
            x.VRChatImageId = Picture;
            x.VRChatPictureId = Picture;
        });

        VRChat.Calendar.OnVRChat.Add(FakeCalendar.Made("cal_original", "18+ Hangout", e.StartsAt, e.EndsAt - e.StartsAt, Clock.UtcNow));

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);

        Assert.Empty(VRChat.Calendar.Creates);

        var place = await PlaceAsync(e.Id, CalendarPlaces.VRChat);
        Assert.Equal("cal_original", place?.ExternalId);
        Assert.Equal(CalendarPlaceStates.Published, place?.State);
        Assert.Null(place?.Error);

        // Taken without an edit having gone out, so the event is written to it as an update, and
        // that carries no picture: it is VRChat's own.
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var (id, sent) = Assert.Single(VRChat.Calendar.Updates);
        Assert.Equal("cal_original", id);
        using var body = JsonDocument.Parse(Assert.IsType<CalendarUpdateBody>(sent).ToJson());
        Assert.False(body.RootElement.TryGetProperty("imageId", out _));
        Assert.Empty(VRChat.Calendar.Creates);
    }

    [Fact]
    public async Task AnEventMadeOnVRChatWithNoCopyThereIsMade_WithoutVRChatsOwnPicture()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.Title = "18+ Hangout";
            x.PublishToVRChat = true;
            x.MadeOnVRChat = true;
            x.VRChatImageId = Picture;
            x.VRChatPictureId = Picture;
        });

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        Assert.Equal(1, VRChat.Calendar.Lists);
        Assert.Null(Assert.Single(VRChat.Calendar.Creates).ImageId);
        Assert.Equal("cal_1", (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.ExternalId);
    }

    [Fact]
    public async Task AnEventMadeOnVRChatIsNotMadeWhileItsCalendarCannotBeRead()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.Title = "18+ Hangout";
            x.PublishToVRChat = true;
            x.MadeOnVRChat = true;
        });

        VRChat.Calendar.ListStatus = HttpStatusCode.InternalServerError;
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);

        Assert.Empty(VRChat.Calendar.Creates);
        Assert.Equal(CalendarPlaceStates.Failed, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);

        // Not looked for again on every pass.
        var lists = VRChat.Calendar.Lists;
        Assert.Equal(CalendarPublishOutcome.NothingToDo, (await PublishAsync()).Outcome);
        Assert.Equal(lists, VRChat.Calendar.Lists);

        // Once the wait is over and VRChat answers, the look is made again.
        VRChat.Calendar.ListStatus = HttpStatusCode.OK;
        Clock.Advance(CalendarVRChatPublisher.RetryUnansweredAfter);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.Single(VRChat.Calendar.Creates);
    }
}
