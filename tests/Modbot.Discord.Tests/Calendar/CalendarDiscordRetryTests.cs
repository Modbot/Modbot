using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// Calendar design §17.4 (2026-10-02): a Discord place that failed is sent again by a moderator's
/// Try again, or by any save of the event, without changing anything Discord shows. Before, a
/// refusal such as a missing permission was held until the event changed, so fixing the permission
/// did nothing until someone edited the description.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarDiscordRetryTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task RunAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        await scope.ServiceProvider.GetRequiredService<CalendarDiscordPublisher>().RunOnceAsync(gateway, Ct);
    }

    private static async Task<CalendarEvent> AddEventAsync(TestServices services, Action<CalendarEvent>? shape = null)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + TimeSpan.FromDays(1),
            EndsAt = now + TimeSpan.FromDays(1) + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd",
            State = CalendarEventStates.Scheduled,
            PostToChannel = true,
            ChannelId = Channel,
            CreatedAt = now,
            UpdatedAt = now,
        };

        CalendarTimeline.Advance(e, now);
        shape?.Invoke(e);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static async Task<CalendarEventPlace> PostPlaceAsync(TestServices services, Guid id)
    {
        await using var context = services.Database.NewContext();
        return await context.CalendarEventPlaces.AsNoTracking()
            .SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.ChannelPost, Ct);
    }

    private static async Task<bool> ChangePostPlaceAsync(TestServices services, Guid id, Func<CalendarEventPlace, DateTimeOffset, bool> change)
    {
        await using var context = services.Database.NewContext();
        var place = await context.CalendarEventPlaces.SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.ChannelPost, Ct);
        var changed = change(place, services.Clock.UtcNow);
        await context.SaveChangesAsync(Ct);
        return changed;
    }

    [Fact]
    public async Task ARefusedPost_IsHeld_ThenTryAgainSendsItWithNoEdit_AndASecondPressFindsNothing()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services);
        gateway.FailNextPost("The bot may not post in that channel.", permanent: true);
        await RunAsync(services, gateway);

        Assert.Equal(CalendarPlaceStates.Failed, (await PostPlaceAsync(services, e.Id)).State);
        Assert.Empty(gateway.Messages);

        // Held: the same refusal is not asked for again.
        await RunAsync(services, gateway);
        Assert.Empty(gateway.Messages);

        // The channel permission is fixed in Discord; the moderator presses Try again, once.
        Assert.True(await ChangePostPlaceAsync(services, e.Id, CalendarDiscordRetry.TryAgain));
        Assert.False(await ChangePostPlaceAsync(services, e.Id, CalendarDiscordRetry.TryAgain));

        var waiting = await PostPlaceAsync(services, e.Id);
        Assert.Equal(CalendarPlaceStates.Waiting, waiting.State);
        Assert.Null(waiting.Error);
        Assert.Null(waiting.FailedFingerprint);

        await RunAsync(services, gateway);

        Assert.Single(gateway.Messages);
        Assert.Equal(CalendarPlaceStates.Published, (await PostPlaceAsync(services, e.Id)).State);
    }

    [Fact]
    public async Task ASaveOfTheEvent_ClearsARefusedPost_AndSendsItAgainUnchanged()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services);
        gateway.FailNextPost("The bot may not post in that channel.", permanent: true);
        await RunAsync(services, gateway);

        Assert.True(await ChangePostPlaceAsync(services, e.Id, CalendarDiscordRetry.ClearAfterEdit));
        await RunAsync(services, gateway);

        Assert.Single(gateway.Messages);
        Assert.Equal(CalendarPlaceStates.Published, (await PostPlaceAsync(services, e.Id)).State);
    }

    [Fact]
    public void OnlyAFailedDiscordPlace_HasAnythingToTryAgain_AndAnEditLeavesTheCancelPostAlone()
    {
        var now = DateTimeOffset.UnixEpoch;

        Assert.False(CalendarDiscordRetry.TryAgain(new CalendarEventPlace { Place = CalendarPlaces.ChannelPost, State = CalendarPlaceStates.Published }, now));
        Assert.False(CalendarDiscordRetry.TryAgain(new CalendarEventPlace { Place = CalendarPlaces.VRChat, State = CalendarPlaceStates.Failed }, now));
        Assert.True(CalendarDiscordRetry.TryAgain(new CalendarEventPlace { Place = CalendarPlaces.CancelPost, State = CalendarPlaceStates.Failed, FailedFingerprint = "cancelPost" }, now));

        var cancelPost = new CalendarEventPlace { Place = CalendarPlaces.CancelPost, State = CalendarPlaceStates.Failed, FailedFingerprint = "cancelPost" };
        Assert.False(CalendarDiscordRetry.ClearAfterEdit(cancelPost, now));
        Assert.Equal(CalendarPlaceStates.Failed, cancelPost.State);

        var serverEvent = new CalendarEventPlace { Place = CalendarPlaces.DiscordEvent, State = CalendarPlaceStates.Failed, FailedFingerprint = "f", Error = "No.", ErrorAt = now };
        Assert.True(CalendarDiscordRetry.ClearAfterEdit(serverEvent, now));
        Assert.Equal(CalendarPlaceStates.Waiting, serverEvent.State);
        Assert.Null(serverEvent.FailedFingerprint);
        Assert.Null(serverEvent.Error);
        Assert.Null(serverEvent.ErrorAt);
    }

    private static async Task<CalendarEventPlace> ServerEventPlaceAsync(TestServices services, Guid id)
    {
        await using var context = services.Database.NewContext();
        return await context.CalendarEventPlaces.AsNoTracking()
            .SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.DiscordEvent, Ct);
    }

    private static async Task TryServerEventAgainAsync(TestServices services, Guid id)
    {
        await using var context = services.Database.NewContext();
        var place = await context.CalendarEventPlaces.SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.DiscordEvent, Ct);
        Assert.True(CalendarDiscordRetry.TryAgain(place, services.Clock.UtcNow));
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// 2026-10-09: a tester pressed Try again on a Discord event that said the bot may not manage
    /// server events. The press has to put the event back to waiting and the next pass has to ask
    /// Discord again; only Discord's answer then decides whether it still complains.
    /// </summary>
    [Fact]
    public async Task ARefusedServerEvent_IsAskedOfDiscordAgainByTryAgain_AndPublishesOnceTheRoleIsFixed()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway { ServerEventError = CalendarDiscordRetry.NeedsManageEvents };

        var e = await AddEventAsync(services, x =>
        {
            x.PublishToDiscord = true;
            x.PostToChannel = false;
        });

        await RunAsync(services, gateway);
        var refused = await ServerEventPlaceAsync(services, e.Id);
        Assert.Equal(CalendarPlaceStates.Failed, refused.State);
        Assert.Equal(CalendarDiscordRetry.NeedsManageEvents, refused.Error);
        Assert.Empty(gateway.ServerEventCalls);

        // Still refused: Try again asks, is refused again, and says so.
        await TryServerEventAgainAsync(services, e.Id);
        Assert.Equal(CalendarPlaceStates.Waiting, (await ServerEventPlaceAsync(services, e.Id)).State);
        await RunAsync(services, gateway);
        Assert.Equal(CalendarPlaceStates.Failed, (await ServerEventPlaceAsync(services, e.Id)).State);

        // The role is fixed; Try again once more.
        gateway.ServerEventError = null;
        await TryServerEventAgainAsync(services, e.Id);
        await RunAsync(services, gateway);

        var published = await ServerEventPlaceAsync(services, e.Id);
        Assert.Equal(CalendarPlaceStates.Published, published.State);
        Assert.Null(published.Error);
        Assert.Contains(gateway.ServerEventCalls, c => c.Action == "create");
    }

    [Fact]
    public void OnlyAPlaceThatFailedForManageEvents_IsClearedWhenTheBotGainsIt()
    {
        var now = DateTimeOffset.UnixEpoch;

        var refused = new CalendarEventPlace
        {
            Place = CalendarPlaces.DiscordEvent,
            State = CalendarPlaceStates.Failed,
            FailedFingerprint = "f",
            // Discord's own words may follow the sentence.
            Error = CalendarDiscordRetry.NeedsManageEvents + " (Discord said: Missing Permissions, code 50013)",
            ErrorAt = now,
        };

        Assert.True(CalendarDiscordRetry.ClearAfterManageEventsGranted(refused, now));
        Assert.Equal(CalendarPlaceStates.Waiting, refused.State);
        Assert.Null(refused.FailedFingerprint);
        Assert.Null(refused.Error);
        Assert.Null(refused.ErrorAt);

        Assert.False(CalendarDiscordRetry.ClearAfterManageEventsGranted(refused, now));

        Assert.False(CalendarDiscordRetry.ClearAfterManageEventsGranted(
            new CalendarEventPlace { Place = CalendarPlaces.DiscordEvent, State = CalendarPlaceStates.Failed, Error = "Discord refused (403, code 160002): nope" }, now));
        Assert.False(CalendarDiscordRetry.ClearAfterManageEventsGranted(
            new CalendarEventPlace { Place = CalendarPlaces.VRChat, State = CalendarPlaceStates.Failed, Error = CalendarDiscordRetry.NeedsManageEvents }, now));
        Assert.False(CalendarDiscordRetry.ClearAfterManageEventsGranted(
            new CalendarEventPlace { Place = CalendarPlaces.DiscordEvent, State = CalendarPlaceStates.Published, Error = CalendarDiscordRetry.NeedsManageEvents }, now));
    }
}
