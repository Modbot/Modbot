using System.Net;
using System.Text.Json;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Featured on VRChat's calendar, 2026-10-09: VRChat refused "You do not have permission to make a
/// featured event" for an event with the box ticked, and again for "18+ Hangout", which was made
/// on VRChat and written back as VRChat had it. An account that may not feature is not refused
/// for it again and again, and an event nobody changed Featured on is written without it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarVRChatFeaturedTests(PostgresFixture fixture) : CalendarTestBase(fixture)
{
    private static readonly TimeSpan Settle = CalendarVRChatPublisher.SettleFor + TimeSpan.FromSeconds(1);

    private const string Refusal = "You do not have permission to make a featured event";

    private static CalendarEvent Imported(bool vrchatSays, bool featured) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "18+ Hangout",
        Description = "Chat",
        StartsAt = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
        EndsAt = new DateTimeOffset(2026, 10, 9, 21, 0, 0, TimeSpan.Zero),
        TimeZone = "UTC",
        State = CalendarEventStates.Scheduled,
        MadeOnVRChat = true,
        VRChatFeatured = vrchatSays,
        Featured = featured,
    };

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(false, false, null)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void AnEventMadeOnVRChatSendsFeaturedOnlyWhenAModeratorChangedIt(bool vrchatSays, bool featured, bool? sent)
    {
        Assert.Equal(sent, CalendarVRChatRequests.FeaturedToSend(Imported(vrchatSays, featured)));
    }

    [Fact]
    public void AnImportedEventsBodiesCarryNoFeaturedUntilItIsChanged()
    {
        var unchanged = Imported(vrchatSays: true, featured: true);
        var change = new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = unchanged.Id,
            PlannedStartsAt = unchanged.StartsAt.AddDays(7),
            Title = "Week two",
        };

        foreach (var json in new[]
                 {
                     CalendarVRChatRequests.Create(unchanged).ToJson(),
                     CalendarVRChatRequests.Update(unchanged).ToJson(),
                     CalendarVRChatRequests.UpdateDate(unchanged, change).ToJson(),
                 })
        {
            using var body = JsonDocument.Parse(json);

            // A create has to say something, and says "not featured"; the updates leave it out.
            if (body.RootElement.TryGetProperty("featured", out var featured))
                Assert.Equal(JsonValueKind.False, featured.ValueKind);
        }

        using var update = JsonDocument.Parse(CalendarVRChatRequests.Update(unchanged).ToJson());
        Assert.False(update.RootElement.TryGetProperty("featured", out _));

        using var date = JsonDocument.Parse(CalendarVRChatRequests.UpdateDate(unchanged, change).ToJson());
        Assert.False(date.RootElement.TryGetProperty("featured", out _));

        // A moderator unticking it is a change, and is sent.
        var unticked = Imported(vrchatSays: true, featured: false);
        using var sent = JsonDocument.Parse(CalendarVRChatRequests.Update(unticked).ToJson());
        Assert.Equal(JsonValueKind.False, sent.RootElement.GetProperty("featured").ValueKind);
    }

    [Fact]
    public void AnEventMadeInModbotKeepsSendingFeaturedAsTheFormHasIt_UnlessTheAccountWasRefused()
    {
        var e = new CalendarEvent { Id = Guid.CreateVersion7(), Title = "Movie night", Featured = true };

        Assert.True(CalendarVRChatRequests.FeaturedToSend(e));
        Assert.Null(CalendarVRChatRequests.FeaturedToSend(e, canFeature: false));

        e.Featured = false;
        Assert.False(CalendarVRChatRequests.FeaturedToSend(e, canFeature: false));

        using var update = JsonDocument.Parse(CalendarVRChatRequests.Update(new CalendarEvent { Id = Guid.CreateVersion7(), Title = "x", Featured = true }, canFeature: false).ToJson());
        Assert.False(update.RootElement.TryGetProperty("featured", out _));
    }

    [Fact]
    public void OnlyAClientErrorThatSpeaksOfFeaturedIsAFeaturedRefusal()
    {
        var body = $"{{\"error\":{{\"message\":\"{Refusal}\",\"status_code\":403}}}}";

        Assert.True(CalendarVRChatRequests.IsFeaturedRefusal(403, body, null));
        Assert.True(CalendarVRChatRequests.IsFeaturedRefusal(403, null, Refusal));
        Assert.False(CalendarVRChatRequests.IsFeaturedRefusal(403, "{\"error\":{\"message\":\"You do not have permission to manage the calendar\"}}", null));
        Assert.False(CalendarVRChatRequests.IsFeaturedRefusal(500, body, null));
        Assert.False(CalendarVRChatRequests.IsFeaturedRefusal(0, null, "featured"));
    }

    [Fact]
    public void ARefusedAccountAsksAgainAfterAWhile()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.True(CalendarVRChatRequests.CanFeature(new Settings(), now));
        Assert.False(CalendarVRChatRequests.CanFeature(new Settings { VRChatFeaturedRefusedAt = now - TimeSpan.FromDays(1) }, now));
        Assert.True(CalendarVRChatRequests.CanFeature(new Settings { VRChatFeaturedRefusedAt = now - CalendarVRChatRequests.FeaturedRefusalMemory }, now));
    }

    [Fact]
    public async Task AFeaturedRefusalIsSentAgainWithoutFeatured_AndTheNextEventIsNotRefusedAgain()
    {
        var first = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Featured = true;
        });

        Clock.Advance(Settle);
        VRChat.Calendar.Refuse(HttpStatusCode.Forbidden, Refusal);

        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        // Refused once, then sent again without Featured: one event on VRChat, published.
        Assert.Equal(2, VRChat.Calendar.Calls);
        Assert.False(Assert.Single(VRChat.Calendar.Creates).Featured);
        Assert.Equal(CalendarPlaceStates.Published, (await PlaceAsync(first.Id, CalendarPlaces.VRChat))?.State);
        Assert.Null((await PlaceAsync(first.Id, CalendarPlaces.VRChat))?.Error);

        await using (var context = Database.NewContext())
        {
            var refusedAt = (await context.GetSettingsAsync(Ct)).VRChatFeaturedRefusedAt;
            Assert.NotNull(refusedAt);
            Assert.True(Clock.UtcNow - refusedAt.Value < TimeSpan.FromSeconds(1));
        }

        // Remembered: the next event with the box ticked goes straight out without it.
        await AddEventAsync(TimeSpan.FromDays(3), x =>
        {
            x.Title = "Game night";
            x.PublishToVRChat = true;
            x.Featured = true;
        });

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        Assert.Equal(3, VRChat.Calendar.Calls);
        Assert.Equal(2, VRChat.Calendar.Creates.Count);
        Assert.All(VRChat.Calendar.Creates, c => Assert.False(c.Featured));
    }

    [Fact]
    public async Task ARefusalThatIsNotAboutFeaturedIsNotSentAgain()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.Featured = true;
        });

        Clock.Advance(Settle);
        VRChat.Calendar.Refuse(HttpStatusCode.BadRequest, "description is required");

        Assert.Equal(CalendarPublishOutcome.Failed, (await PublishAsync()).Outcome);
        Assert.Equal(1, VRChat.Calendar.Calls);
        Assert.Equal(CalendarPlaceStates.Failed, (await PlaceAsync(e.Id, CalendarPlaces.VRChat))?.State);

        await using var context = Database.NewContext();
        Assert.Null((await context.GetSettingsAsync(Ct)).VRChatFeaturedRefusedAt);
    }

    [Fact]
    public async Task AnEventMadeOnVRChatIsWrittenBackWithoutFeatured_UntilAModeratorChangesIt()
    {
        var e = await AddEventAsync(TimeSpan.FromDays(2), x =>
        {
            x.PublishToVRChat = true;
            x.MadeOnVRChat = true;
            x.Featured = true;
            x.VRChatFeatured = true;
        });

        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);
        Assert.False(Assert.Single(VRChat.Calendar.Creates).Featured);

        // An edit that is not about Featured.
        await EditAsync(e.Id, x => x.Description = "Chat about anything");
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var update = Assert.IsType<CalendarUpdateBody>(Assert.Single(VRChat.Calendar.Updates).Body);
        using (var body = JsonDocument.Parse(update.ToJson()))
            Assert.False(body.RootElement.TryGetProperty("featured", out _));

        // The moderator unticks it: that one is sent.
        await EditAsync(e.Id, x => x.Featured = false);
        Clock.Advance(Settle);
        Assert.Equal(CalendarPublishOutcome.Written, (await PublishAsync()).Outcome);

        var unticked = Assert.IsType<CalendarUpdateBody>(VRChat.Calendar.Updates[^1].Body);
        using var sent = JsonDocument.Parse(unticked.ToJson());
        Assert.Equal(JsonValueKind.False, sent.RootElement.GetProperty("featured").ValueKind);
    }
}
