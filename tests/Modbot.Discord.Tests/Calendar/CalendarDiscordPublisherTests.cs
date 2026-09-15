using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// Calendar design §3.2 and §3.3: one server event and one post per occurrence, the join link on
/// both once the instance is open, and a cancel reaching both.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarDiscordPublisherTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Group = "grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CalendarDiscordPass> RunAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<CalendarDiscordPublisher>().RunOnceAsync(gateway, Ct);
    }

    private static async Task<CalendarEvent> AddEventAsync(TestServices services, TimeSpan startsIn, Action<CalendarEvent>? shape = null)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + startsIn,
            EndsAt = now + startsIn + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            WorldId = World,
            State = CalendarEventStates.Scheduled,
            PublishToDiscord = true,
            PostToChannel = true,
            ChannelId = Channel,
            CreatedAt = now,
            UpdatedAt = now,
        };

        shape?.Invoke(e);
        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static async Task ChangeAsync(TestServices services, Guid id, Action<CalendarEvent> change)
    {
        await using var context = services.Database.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        e.UpdatedAt = services.Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>What the VRChat side leaves behind when it opens the instance: the room and the opening row.</summary>
    private static async Task<string> OpenInstanceAsync(TestServices services, CalendarEvent e)
    {
        var location = $"{World}:12345~group({Group})~groupAccessType(members)~region(us)";
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();

        var room = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = location,
            WorldId = World,
            VRChatInstanceId = "12345",
            GroupId = Group,
            OpenedAt = now,
            LastSeenAt = now,
        };

        context.VRChatInstances.Add(room);
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = e.StartsAt,
            AttemptedAt = now,
            Location = location,
            RoomId = room.Id,
        });

        var saved = await context.CalendarEvents.SingleAsync(x => x.Id == e.Id, Ct);
        CalendarTimeline.Advance(saved, now);

        await context.SaveChangesAsync(Ct);

        return Core.Data.InstanceJoinLink.For(location)!;
    }

    [Fact]
    public async Task AScheduledEventGetsAServerEventAndAPost_WithTheWorldAsTheLocation()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        await AddEventAsync(services, TimeSpan.FromDays(1));
        await RunAsync(services, gateway);

        var serverEvent = Assert.Single(gateway.ServerEvents.Values);
        Assert.Equal("Movie night", serverEvent.Details.Name);
        Assert.Equal(World, serverEvent.Details.Location);
        Assert.False(serverEvent.Started);

        var post = Assert.Single(gateway.Messages);
        Assert.Equal(Channel, post.ChannelId);
        Assert.Empty(post.Links);
        Assert.Contains(post.Embeds[0].Fields, f => f.Name == "When" && f.Value.StartsWith("<t:", StringComparison.Ordinal));

        // A quiet pass costs Discord nothing.
        var calls = gateway.ServerEventCalls.Count;
        await RunAsync(services, gateway);
        Assert.Equal(calls, gateway.ServerEventCalls.Count);
        Assert.Empty(gateway.Edits);
    }

    [Fact]
    public async Task OnceTheInstanceIsOpen_TheJoinLinkGoesOnTheServerEventAndThePost()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.PublicAddress = "https://modbot.example";
        }, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromMinutes(20), x =>
        {
            x.AutoOpen = true;
            x.OpenMinutesBefore = 10;
        });

        await RunAsync(services, gateway);

        services.Clock.Advance(TimeSpan.FromMinutes(10));
        var link = await OpenInstanceAsync(services, e);
        await RunAsync(services, gateway);

        var serverEvent = Assert.Single(gateway.ServerEvents.Values);
        Assert.True(serverEvent.Started);
        Assert.Equal($"https://modbot.example/api/calendar/join/{e.Id:D}", serverEvent.Details.Location);

        var edit = Assert.Single(gateway.Edits);
        var join = Assert.Single(edit.Links);
        Assert.Equal("Join", join.Label);
        Assert.Equal(link, join.Url);
        Assert.Equal(link, edit.Embeds[0].Url);
    }

    [Fact]
    public async Task WithoutAPublicAddress_TheJoinLinkGoesIntoTheServerEventsDescription()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromMinutes(5), x => x.AutoOpen = true);
        var link = await OpenInstanceAsync(services, e);

        await RunAsync(services, gateway);

        var serverEvent = Assert.Single(gateway.ServerEvents.Values);
        Assert.Equal(World, serverEvent.Details.Location);
        Assert.StartsWith("Join: " + link, serverEvent.Details.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingEndsTheServerEventAndMarksThePostCancelled()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1));
        await RunAsync(services, gateway);

        await ChangeAsync(services, e.Id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = services.Clock.UtcNow;
        });

        await RunAsync(services, gateway);

        Assert.True(Assert.Single(gateway.ServerEvents.Values).Ended);

        var edit = Assert.Single(gateway.Edits);
        Assert.Equal("Cancelled", edit.Embeds[0].Footer);
        Assert.Empty(edit.Links);

        await using var context = services.Database.NewContext();
        var places = await context.CalendarEventPlaces.AsNoTracking().Where(p => p.EventId == e.Id).ToListAsync(Ct);
        Assert.All(places, p => Assert.Equal(CalendarPlaceStates.Removed, p.State));

        // Nothing more to say afterwards.
        await RunAsync(services, gateway);
        Assert.Single(gateway.Edits);
    }

    [Fact]
    public async Task EachOccurrenceOfARepeatingEventGetsItsOwnServerEventAndPost()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromHours(1), x => x.Repeat = CalendarRepeats.Daily);
        await RunAsync(services, gateway);

        // The first occurrence ends; the scheduler moves the event on.
        services.Clock.Advance(TimeSpan.FromHours(3));
        await ChangeAsync(services, e.Id, x => CalendarTimeline.Advance(x, services.Clock.UtcNow));

        await RunAsync(services, gateway);

        Assert.Equal(2, gateway.ServerEvents.Count);
        Assert.Single(gateway.ServerEvents.Values, s => s.Ended);
        Assert.Equal(2, gateway.Messages.Count);
        Assert.Equal("Finished", Assert.Single(gateway.Edits).Embeds[0].Footer);
    }

    [Fact]
    public async Task WithoutManageEvents_TheFailureIsKeptAndNotRepeatedEveryPass()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway { ServerEventError = "The bot may not manage server events; it needs Manage Events." };

        var e = await AddEventAsync(services, TimeSpan.FromDays(1), x => x.PostToChannel = false);

        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        await using var context = services.Database.NewContext();
        var place = await context.CalendarEventPlaces.AsNoTracking()
            .SingleAsync(p => p.EventId == e.Id && p.Place == CalendarPlaces.DiscordEvent, Ct);

        Assert.Equal(CalendarPlaceStates.Failed, place.State);
        Assert.Contains("Manage Events", place.Error, StringComparison.Ordinal);
        Assert.Equal(1, await context.Events.CountAsync(f => f.Type == FactType.PlannedEventPublishFailed, Ct));
    }
}
