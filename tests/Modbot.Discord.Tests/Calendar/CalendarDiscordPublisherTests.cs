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

    /// <summary>What the VRChat side leaves behind when it opens the instance: the instance and the opening row.</summary>
    private static async Task<string> OpenInstanceAsync(TestServices services, CalendarEvent e)
    {
        var location = $"{World}:12345~group({Group})~groupAccessType(members)~region(us)";
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = location,
            WorldId = World,
            VRChatInstanceId = "12345",
            GroupId = Group,
            OpenedAt = now,
            LastSeenAt = now,
        };

        context.VRChatInstances.Add(instance);
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = e.StartsAt,
            AttemptedAt = now,
            Location = location,
            InstanceId = instance.Id,
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

    private static async Task AddWorldAsync(TestServices services, string name)
    {
        var now = services.Clock.UtcNow;

        await using var context = services.Database.NewContext();
        context.VRChatWorlds.Add(new VRChatWorld { WorldId = World, Name = name, FirstSeenAt = now, LastSeenAt = now });
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>What the VRChat side leaves behind when VRChat refused to open the instance.</summary>
    private static async Task RefusedOpeningAsync(TestServices services, CalendarEvent e)
    {
        await using var context = services.Database.NewContext();
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = e.StartsAt,
            AttemptedAt = services.Clock.UtcNow,
            Error = "instancePersistenceEnabled must be a boolean: 'null'",
        });

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Open with no instance Modbot opened -- auto-open off, or VRChat refused it -- there is no join
    /// link, so the location is the world's name and not Modbot's join address, which would answer
    /// 404 (found in review, 2026-10-01).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenWithNoInstanceOpened_TheLocationIsTheWorldsName_NotAJoinAddress(bool openingRefused)
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.PublicAddress = "https://modbot.example";
        }, Ct);
        await AddWorldAsync(services, "The Black Cat");
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromMinutes(-5), x => x.AutoOpen = openingRefused);
        Assert.Equal(CalendarEventStates.Open, e.State);

        if (openingRefused)
            await RefusedOpeningAsync(services, e);

        await RunAsync(services, gateway);

        var serverEvent = Assert.Single(gateway.ServerEvents.Values);
        Assert.True(serverEvent.Started);
        Assert.Equal("The Black Cat", serverEvent.Details.Location);
        Assert.Equal("Bring snacks", serverEvent.Details.Description);

        var post = Assert.Single(gateway.Messages);
        Assert.Empty(post.Links);
        Assert.Null(post.Embeds[0].Url);
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

    // ── Live updates (2026-10-01) ───────────────────────────────────────────────────────

    /// <summary>
    /// A place turning published writes one fact, which the live stream carries to the calendar
    /// page; an edit sent to a place already published writes none.
    /// </summary>
    [Fact]
    public async Task APlaceTurningPublishedWritesOneFact_AndAnEditAfterwardsWritesNone()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1));
        await RunAsync(services, gateway);

        await using (var context = services.Database.NewContext())
        {
            var facts = await context.Events.AsNoTracking()
                .Where(f => f.Type == FactType.PlannedEventPublished && f.SubjectId == e.Id.ToString())
                .ToListAsync(Ct);

            Assert.Equal(2, facts.Count);
            Assert.Contains(facts, f => f.Data!.Contains("discordEvent", StringComparison.Ordinal));
            Assert.Contains(facts, f => f.Data!.Contains("channelPost", StringComparison.Ordinal));
        }

        await ChangeAsync(services, e.Id, x => x.Title = "Movie night: Alien");
        await RunAsync(services, gateway);

        Assert.NotEmpty(gateway.Edits);

        await using (var context = services.Database.NewContext())
        {
            Assert.Equal(2, await context.Events.CountAsync(
                f => f.Type == FactType.PlannedEventPublished && f.SubjectId == e.Id.ToString(), Ct));
        }
    }

    [Fact]
    public async Task ACancelTakesBothPlacesDown_WithAFactForEach()
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
        await RunAsync(services, gateway);

        await using var context = services.Database.NewContext();
        Assert.Equal(2, await context.Events.CountAsync(
            f => f.Type == FactType.PlannedEventTakenDown && f.SubjectId == e.Id.ToString(), Ct));
    }

    // ── The cancel post (2026-10-01) ────────────────────────────────────────────────────

    private static async Task CancelAsync(TestServices services, CalendarEvent e, bool post)
    {
        await ChangeAsync(services, e.Id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = services.Clock.UtcNow;
        });

        if (!post)
            return;

        // What the cancel endpoint leaves when the moderator ticked it.
        await using var context = services.Database.NewContext();
        context.CalendarEventPlaces.Add(new CalendarEventPlace
        {
            EventId = e.Id,
            Place = CalendarPlaces.CancelPost,
            State = CalendarPlaceStates.Waiting,
            ChannelId = Channel,
            OccurrenceStartsAt = e.OccurrenceStartsAt ?? e.StartsAt,
            UpdatedAt = services.Clock.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ATickedCancelPostsOnceInTheChannel_WithTheTitleTimeAndWord()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1));
        await RunAsync(services, gateway);
        var before = gateway.Messages.Count;

        await CancelAsync(services, e, post: true);
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        var notice = Assert.Single(gateway.Messages.Skip(before));
        Assert.Equal(Channel, notice.ChannelId);
        Assert.Empty(notice.Embeds);
        Assert.Contains("Movie night", notice.Text, StringComparison.Ordinal);
        Assert.Contains($"<t:{(e.OccurrenceStartsAt ?? e.StartsAt).ToUnixTimeSeconds()}:F>", notice.Text, StringComparison.Ordinal);
        Assert.EndsWith("Cancelled", notice.Text, StringComparison.Ordinal);

        // The card still turns red, as before.
        Assert.Contains(gateway.Edits, edit => edit.Embeds.Count > 0 && edit.Embeds[0].Footer == "Cancelled");

        await using var context = services.Database.NewContext();
        var place = await context.CalendarEventPlaces.AsNoTracking()
            .SingleAsync(p => p.EventId == e.Id && p.Place == CalendarPlaces.CancelPost, Ct);

        Assert.Equal(CalendarPlaceStates.Published, place.State);
        Assert.Equal(notice.MessageId, place.ExternalId);
        var published = await context.Events.AsNoTracking()
            .Where(f => f.Type == FactType.PlannedEventPublished && f.SubjectId == e.Id.ToString())
            .ToListAsync(Ct);
        Assert.Single(published, f => f.Data!.Contains("cancelPost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUntickedCancelPostsNothing()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1));
        await RunAsync(services, gateway);
        var before = gateway.Messages.Count;

        await CancelAsync(services, e, post: false);
        await RunAsync(services, gateway);

        Assert.Equal(before, gateway.Messages.Count);
    }

    // ── The form's preview (2026-10-01) ─────────────────────────────────────────────────

    /// <summary>
    /// The preview is drawn by the publisher's own builders, so what the form shows is what
    /// Discord is sent: name, description, times, location and cover; the card's title, fields and
    /// footer.
    /// </summary>
    [Fact]
    public async Task ThePreviewIsWhatThePublisherSends()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.PublicAddress = "https://modbot.example";
            s.ManagedGroupName = "Night Owls";
        }, Ct);
        await AddWorldAsync(services, "The Black Cat");
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1), x =>
        {
            x.Title = new string('t', 100);
            x.Description = new string('d', 1000);
            x.ImageUrl = "https://pictures.example/movie.png";
        });

        await RunAsync(services, gateway);

        VRChatWorld world;
        await using (var context = services.Database.NewContext())
            world = await context.VRChatWorlds.AsNoTracking().SingleAsync(w => w.WorldId == World, Ct);

        var preview = new CalendarDiscordPreviewer().Preview(
            e, world, new Core.Calendar.CalendarPreviewContext("https://modbot.example", "Night Owls", null, services.Clock.UtcNow));

        var sent = Assert.Single(gateway.ServerEvents.Values).Details;
        Assert.Equal(sent.Name, preview.DiscordEvent.Name);
        Assert.Equal(sent.Description, preview.DiscordEvent.Description);
        Assert.Equal(sent.StartsAt, preview.DiscordEvent.StartsAt);
        Assert.Equal(sent.EndsAt, preview.DiscordEvent.EndsAt);
        Assert.Equal(sent.Location, preview.DiscordEvent.Location);
        Assert.Equal("The Black Cat", preview.DiscordEvent.Location);
        Assert.Equal(sent.CoverImageUrl, preview.DiscordEvent.CoverUrl);

        var card = Assert.Single(gateway.Messages).Embeds[0];
        Assert.Equal(card.Title, preview.ChannelPost.Title);
        Assert.Equal(card.Description, preview.ChannelPost.Description);
        Assert.Equal(card.Color, (uint)preview.ChannelPost.Colour);
        Assert.Equal(card.Footer, preview.ChannelPost.Footer);
        Assert.Equal(card.AuthorName, preview.ChannelPost.GroupName);
        Assert.Equal(card.ImageUrl, preview.ChannelPost.PictureUrl);
        Assert.Equal(
            card.Fields.Select(f => (f.Name, f.Value, f.Inline)),
            preview.ChannelPost.Fields.Select(f => (f.Name, f.Value, f.Inline)));
    }

    [Fact]
    public void ThePreviewCutsTheNameAt100_AndTheDescriptionAt1000_AsDiscordIsSent()
    {
        var now = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = new string('t', 120),
            Description = new string('d', 1200),
            StartsAt = now.AddDays(1),
            EndsAt = now.AddDays(1).AddHours(2),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            OccurrenceStartsAt = now.AddDays(1),
        };

        var preview = new CalendarDiscordPreviewer().Preview(e, null, new Core.Calendar.CalendarPreviewContext(null, null, null, now));

        Assert.Equal(CalendarCard.DiscordEventNameLimit, preview.DiscordEvent.Name.Length);
        Assert.EndsWith("…", preview.DiscordEvent.Name, StringComparison.Ordinal);
        Assert.Equal(CalendarCard.DiscordEventDescriptionLimit, preview.DiscordEvent.Description!.Length);

        // No world: the location is "VRChat", and there is no join address without an opened instance.
        Assert.Equal("VRChat", preview.DiscordEvent.Location);
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
