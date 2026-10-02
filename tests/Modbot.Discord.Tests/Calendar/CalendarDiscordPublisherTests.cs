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
    public async Task CancellingOneDateEndsOnlyItsServerEvent_AndTheNextDateGetsItsOwn()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromHours(1), x => x.Repeat = CalendarRepeats.Daily);
        await RunAsync(services, gateway);
        var first = Assert.Single(gateway.ServerEvents.Values);

        // The first date is cancelled on its own; the event moves on to the next day.
        await ChangeAsync(services, e.Id, x =>
        {
            x.DateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = x.Id,
                PlannedStartsAt = x.StartsAt,
                Cancelled = true,
                CreatedAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            x.OccurrenceStartsAt = null;
            CalendarTimeline.Advance(x, services.Clock.UtcNow);
        });

        await RunAsync(services, gateway);

        Assert.Equal(2, gateway.ServerEvents.Count);
        Assert.True(gateway.ServerEvents[first.Id].Ended);
        var next = Assert.Single(gateway.ServerEvents.Values, s => !s.Ended);
        Assert.Equal(e.StartsAt + TimeSpan.FromDays(1), next.Details.StartsAt);

        // The first date's post says it was cancelled, not that it finished.
        Assert.Equal("Cancelled", Assert.Single(gateway.Edits).Embeds[0].Footer);
        Assert.Equal(2, gateway.Messages.Count);
    }

    [Fact]
    public async Task AOneDateCancelPostUsesTheDatesOwnTitle_AndIsSkippedWhenNoLongerNews()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1), x =>
        {
            x.Repeat = CalendarRepeats.Weekly;
            x.PostToChannel = false;
            x.PublishToDiscord = false;
        });

        var second = e.StartsAt + TimeSpan.FromDays(7);
        await ChangeAsync(services, e.Id, x => x.DateChanges.Add(new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = x.Id,
            PlannedStartsAt = second,
            Title = "Halloween special",
            Cancelled = true,
            CancelPostChannelId = Channel,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        }));

        await RunAsync(services, gateway);
        Assert.StartsWith("**Halloween special**", Assert.Single(gateway.Messages).Text, StringComparison.Ordinal);

        // A second event, deleted before its date's post went out: nothing is posted.
        var gone = await AddEventAsync(services, TimeSpan.FromDays(1), x =>
        {
            x.Repeat = CalendarRepeats.Weekly;
            x.PostToChannel = false;
            x.PublishToDiscord = false;
        });

        await ChangeAsync(services, gone.Id, x =>
        {
            x.DeletedAt = services.Clock.UtcNow;
            x.State = CalendarEventStates.Cancelled;
            x.DateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = x.Id,
                PlannedStartsAt = x.StartsAt + TimeSpan.FromDays(7),
                Cancelled = true,
                CancelPostChannelId = Channel,
                CreatedAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
        });

        await RunAsync(services, gateway);
        Assert.Single(gateway.Messages);

        await using var context = services.Database.NewContext();
        Assert.Null((await context.CalendarDateChanges.SingleAsync(c => c.EventId == gone.Id, Ct)).CancelPostChannelId);
    }

    [Fact]
    public async Task CancellingOneDateWithTheTickPostsThatItIsCancelled_Once()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1), x =>
        {
            x.Repeat = CalendarRepeats.Weekly;
            x.PostToChannel = false;
        });

        var second = e.StartsAt + TimeSpan.FromDays(7);
        await ChangeAsync(services, e.Id, x => x.DateChanges.Add(new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = x.Id,
            PlannedStartsAt = second,
            Cancelled = true,
            CancelPostChannelId = Channel,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        }));

        await RunAsync(services, gateway);

        var notice = Assert.Single(gateway.Messages, m => m.ChannelId == Channel);
        Assert.EndsWith("Cancelled", notice.Text, StringComparison.Ordinal);
        Assert.Contains($"<t:{second.ToUnixTimeSeconds()}:F>", notice.Text, StringComparison.Ordinal);

        // Posted once.
        await RunAsync(services, gateway);
        Assert.Single(gateway.Messages, m => m.ChannelId == Channel);

        await using var context = services.Database.NewContext();
        Assert.NotNull((await context.CalendarDateChanges.SingleAsync(c => c.EventId == e.Id, Ct)).CancelPostId);
    }

    [Fact]
    public async Task MovingTheCurrentDateUpdatesItsServerEvent_RatherThanMakingAnother()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromHours(5), x => x.Repeat = CalendarRepeats.Weekly);
        await RunAsync(services, gateway);
        var created = Assert.Single(gateway.ServerEvents.Values);

        var later = e.StartsAt + TimeSpan.FromHours(2);
        await ChangeAsync(services, e.Id, x =>
        {
            x.DateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = x.Id,
                PlannedStartsAt = x.StartsAt,
                StartsAt = later,
                EndsAt = later + TimeSpan.FromHours(1),
                Title = "Movie night, late",
                CreatedAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            x.OccurrenceStartsAt = null;
            CalendarTimeline.Advance(x, services.Clock.UtcNow);
        });

        await RunAsync(services, gateway);

        var serverEvent = Assert.Single(gateway.ServerEvents.Values);
        Assert.Equal(created.Id, serverEvent.Id);
        Assert.False(serverEvent.Ended);
        Assert.Equal(later, serverEvent.Details.StartsAt);
        Assert.Equal("Movie night, late", serverEvent.Details.Name);

        // The post is edited in place as well.
        Assert.Single(gateway.Messages);
        Assert.Equal("Movie night, late", gateway.Edits[^1].Embeds[0].Title);
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
    /// A place getting onto Discord for the first time writes one fact, which the live stream
    /// carries to the calendar page; an edit sent to a place Discord already holds writes none.
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

    // ── Deleting, and old posts coming down (2026-10-01) ────────────────────────────────

    private static async Task FinishAsync(TestServices services, Guid id, TimeSpan after)
    {
        services.Clock.Advance(after);
        await ChangeAsync(services, id, x => CalendarTimeline.Advance(x, services.Clock.UtcNow));
    }

    [Fact]
    public async Task DeletingAnEventMarksItsPostCancelled_AndEndsItsServerEvent()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1));
        await RunAsync(services, gateway);
        var post = Assert.Single(gateway.Messages);

        // What the delete endpoint leaves.
        await ChangeAsync(services, e.Id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = services.Clock.UtcNow;
            x.DeletedAt = services.Clock.UtcNow;
        });

        await RunAsync(services, gateway);

        Assert.True(Assert.Single(gateway.ServerEvents.Values).Ended);

        var edit = Assert.Single(gateway.Edits);
        Assert.Equal(post.MessageId, edit.MessageId);
        Assert.Equal("Cancelled", edit.Embeds[0].Footer);
        Assert.Empty(edit.Links);

        // Not taken down yet: it stays until a day after the date it was for.
        Assert.Empty(gateway.Deleted);

        await using var context = services.Database.NewContext();
        var kept = await context.CalendarOldPosts.AsNoTracking().SingleAsync(p => p.EventId == e.Id, Ct);
        Assert.Equal(post.MessageId, kept.MessageId);
        Assert.Equal(e.EndsAt, kept.EndsAt);
        Assert.Null(kept.RemovedAt);
    }

    [Fact]
    public async Task AFinishedEventsCardComesDownADayAfterItEnded_Once_AndWithoutAFact()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromHours(1), x => x.PublishToDiscord = false);
        await RunAsync(services, gateway);
        var post = Assert.Single(gateway.Messages);

        await FinishAsync(services, e.Id, TimeSpan.FromHours(3));
        await RunAsync(services, gateway);
        Assert.Equal("Finished", Assert.Single(gateway.Edits).Embeds[0].Footer);

        await using var context = services.Database.NewContext();
        var facts = await context.Events.CountAsync(Ct);

        // A minute short of a day after the end: still up.
        services.Clock.UtcNow = e.EndsAt + CalendarDiscordPublisher.PostKeptFor - TimeSpan.FromMinutes(1);
        await RunAsync(services, gateway);
        Assert.Empty(gateway.Deleted);

        services.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        var deleted = Assert.Single(gateway.Deleted);
        Assert.Equal(Channel, deleted.ChannelId);
        Assert.Equal(post.MessageId, deleted.MessageId);
        Assert.Equal(CalendarDiscordPublisher.OldPostReason, deleted.Reason);

        Assert.Equal(facts, await context.Events.CountAsync(Ct));
        Assert.NotNull((await context.CalendarOldPosts.AsNoTracking().SingleAsync(p => p.EventId == e.Id, Ct)).RemovedAt);
    }

    [Fact]
    public async Task AWholeEventCancelLineComesDownWithItsCard_ADayAfterTheDateWasDueToEnd()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1), x => x.PublishToDiscord = false);
        await RunAsync(services, gateway);
        var card = Assert.Single(gateway.Messages);

        await CancelAsync(services, e, post: true);
        await RunAsync(services, gateway);
        var line = Assert.Single(gateway.Messages, m => m.Text is not null);

        services.Clock.UtcNow = e.EndsAt + CalendarDiscordPublisher.PostKeptFor - TimeSpan.FromMinutes(1);
        await RunAsync(services, gateway);
        Assert.Empty(gateway.Deleted);

        services.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        Assert.Equal(
            new[] { card.MessageId, line.MessageId }.Order(StringComparer.Ordinal),
            gateway.Deleted.Select(d => d.MessageId).Order(StringComparer.Ordinal));

        // Taken down, and never posted again: the row keeps the message id.
        Assert.Equal(2, gateway.Messages.Count);

        await using var context = services.Database.NewContext();
        var place = await context.CalendarEventPlaces.AsNoTracking()
            .SingleAsync(p => p.EventId == e.Id && p.Place == CalendarPlaces.CancelPost, Ct);
        Assert.Equal(CalendarPlaceStates.Removed, place.State);
        Assert.Equal(line.MessageId, place.ExternalId);
    }

    [Fact]
    public async Task AOneDateCancelLineComesDownADayAfterThatDateWasDueToEnd()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromDays(1), x =>
        {
            x.Repeat = CalendarRepeats.Weekly;
            x.PostToChannel = false;
            x.PublishToDiscord = false;
        });

        // The second date, moved an hour later before it was cancelled: it ends an hour later too.
        var second = e.StartsAt + TimeSpan.FromDays(7);
        await ChangeAsync(services, e.Id, x => x.DateChanges.Add(new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = x.Id,
            PlannedStartsAt = second,
            StartsAt = second + TimeSpan.FromHours(1),
            EndsAt = second + TimeSpan.FromHours(3),
            Cancelled = true,
            CancelPostChannelId = Channel,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        }));

        await RunAsync(services, gateway);
        var line = Assert.Single(gateway.Messages);

        services.Clock.UtcNow = second + TimeSpan.FromHours(3) + CalendarDiscordPublisher.PostKeptFor - TimeSpan.FromMinutes(1);
        await RunAsync(services, gateway);
        Assert.Empty(gateway.Deleted);

        services.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        Assert.Equal(line.MessageId, Assert.Single(gateway.Deleted).MessageId);

        await using var context = services.Database.NewContext();
        var date = await context.CalendarDateChanges.AsNoTracking().SingleAsync(c => c.EventId == e.Id, Ct);
        Assert.NotNull(date.CancelPostRemovedAt);
        Assert.Equal(line.MessageId, date.CancelPostId);
    }

    [Fact]
    public async Task EachDateOfARepeatingEventHasItsCardTakenDownADayAfterThatDate()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, TimeSpan.FromHours(1), x =>
        {
            x.Repeat = CalendarRepeats.Daily;
            x.PublishToDiscord = false;
        });

        await RunAsync(services, gateway);
        var first = Assert.Single(gateway.Messages);

        // The first date ends: its card gets its last word, and the second date gets a card.
        await FinishAsync(services, e.Id, TimeSpan.FromHours(3));
        await RunAsync(services, gateway);
        var second = gateway.Messages[1];

        // A day later the second date has just ended too: the first card comes down, the second
        // gets its last word and stays.
        await FinishAsync(services, e.Id, CalendarDiscordPublisher.PostKeptFor);
        await RunAsync(services, gateway);

        Assert.Equal(first.MessageId, Assert.Single(gateway.Deleted).MessageId);
        Assert.Equal(3, gateway.Messages.Count);

        await FinishAsync(services, e.Id, CalendarDiscordPublisher.PostKeptFor);
        await RunAsync(services, gateway);

        Assert.Equal(new[] { first.MessageId, second.MessageId }, gateway.Deleted.Select(d => d.MessageId));

        await using var context = services.Database.NewContext();
        var kept = await context.CalendarOldPosts.AsNoTracking().Where(p => p.EventId == e.Id).ToListAsync(Ct);
        Assert.Equal(3, kept.Count);
        Assert.Single(kept, p => p.RemovedAt is null);
    }

    [Fact]
    public async Task APostSomeoneDeletedByHandIsForgotten_AndAFailureThatMayPassIsTriedAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        // One pass each, so which post is which is known.
        var gone = await AddEventAsync(services, TimeSpan.FromHours(1), x => x.PublishToDiscord = false);
        await RunAsync(services, gateway);
        var later = await AddEventAsync(services, TimeSpan.FromHours(2), x => x.PublishToDiscord = false);
        await RunAsync(services, gateway);
        var laterPost = gateway.Messages[1].MessageId;

        await FinishAsync(services, gone.Id, TimeSpan.FromHours(5));
        await ChangeAsync(services, later.Id, x => CalendarTimeline.Advance(x, services.Clock.UtcNow));
        await RunAsync(services, gateway);
        Assert.Equal(2, gateway.Edits.Count);

        services.Clock.Advance(CalendarDiscordPublisher.PostKeptFor);

        // The older one is gone already (a 404); the other meets a failure that may pass.
        gateway.FailNextDelete("Discord does not know that message.", notFound: true);
        gateway.FailNextDelete("Discord is rate limiting the bot; it will try again shortly.");
        await RunAsync(services, gateway);
        Assert.Empty(gateway.Deleted);

        await using var context = services.Database.NewContext();
        Assert.NotNull((await context.CalendarOldPosts.AsNoTracking().SingleAsync(p => p.EventId == gone.Id, Ct)).RemovedAt);
        Assert.Null((await context.CalendarOldPosts.AsNoTracking().SingleAsync(p => p.EventId == later.Id, Ct)).RemovedAt);

        // The next passes ask only about the second one.
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        Assert.Equal(laterPost, Assert.Single(gateway.Deleted).MessageId);
    }

    [Fact]
    public async Task APostTheBotMayNotDelete_IsNotMarkedDone_AndIsAskedAboutOnceADay()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        // One pass each, so which post is which is known.
        var refused = await AddEventAsync(services, TimeSpan.FromHours(1), x => x.PublishToDiscord = false);
        await RunAsync(services, gateway);
        var other = await AddEventAsync(services, TimeSpan.FromHours(2), x => x.PublishToDiscord = false);
        await RunAsync(services, gateway);
        var refusedPost = gateway.Messages[0].MessageId;
        var otherPost = gateway.Messages[1].MessageId;

        await FinishAsync(services, refused.Id, TimeSpan.FromHours(5));
        await ChangeAsync(services, other.Id, x => CalendarTimeline.Advance(x, services.Clock.UtcNow));
        await RunAsync(services, gateway);

        services.Clock.Advance(CalendarDiscordPublisher.PostKeptFor);

        // Discord says 403 for the older one: the bot may not delete it. Not gone, so not done.
        gateway.FailNextDelete("Discord refused: the bot needs Manage Messages.", permanent: true);
        await RunAsync(services, gateway);

        await using var context = services.Database.NewContext();
        Assert.Null((await context.CalendarOldPosts.AsNoTracking().SingleAsync(p => p.EventId == refused.Id, Ct)).RemovedAt);

        // The other post is not held up by it, and the refused one is not asked about again.
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);
        Assert.Equal(otherPost, Assert.Single(gateway.Deleted).MessageId);

        // A day later it is asked about once more, and this time the bot may.
        services.Clock.Advance(CalendarDiscordPublisher.RefusedPostRetryAfter + TimeSpan.FromMinutes(1));
        await RunAsync(services, gateway);

        Assert.Equal(new[] { otherPost, refusedPost }, gateway.Deleted.Select(d => d.MessageId));
        Assert.NotNull((await context.CalendarOldPosts.AsNoTracking().SingleAsync(p => p.EventId == refused.Id, Ct)).RemovedAt);
    }

    [Fact]
    public async Task OldPostsComeDownAFewAPass_AndOnlyWithWhatPostingLeaves()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        // An install that already had old cards and cancel lines: five, long over.
        var quiet = await AddEventAsync(services, TimeSpan.FromDays(1), x =>
        {
            x.PostToChannel = false;
            x.PublishToDiscord = false;
        });

        var seeded = new List<string>();
        var longAgo = services.Clock.UtcNow - TimeSpan.FromDays(30);

        await using (var context = services.Database.NewContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var id = (9000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                seeded.Add(id);
                context.CalendarOldPosts.Add(new CalendarOldPost
                {
                    Id = Guid.CreateVersion7(),
                    EventId = quiet.Id,
                    ChannelId = Channel,
                    MessageId = id,
                    EndsAt = longAgo.AddMinutes(i),
                });
            }

            await context.SaveChangesAsync(Ct);
        }

        for (var i = 0; i < 2; i++)
        {
            var id = (9100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            seeded.Add(id);

            var cancelled = await AddEventAsync(services, -TimeSpan.FromDays(30), x =>
            {
                x.State = CalendarEventStates.Cancelled;
                x.CancelledAt = longAgo;
            });

            await using var context = services.Database.NewContext();
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = cancelled.Id,
                Place = CalendarPlaces.CancelPost,
                State = CalendarPlaceStates.Published,
                ExternalId = id,
                ChannelId = Channel,
                OccurrenceStartsAt = cancelled.StartsAt,
                UpdatedAt = longAgo,
            });
            await context.SaveChangesAsync(Ct);
        }

        await RunAsync(services, gateway);
        Assert.Equal(CalendarDiscordPublisher.RemovalsPerPass, gateway.Deleted.Count);

        // Five new events want their posts: they take the whole pass, and nothing comes down.
        for (var i = 0; i < 5; i++)
            await AddEventAsync(services, TimeSpan.FromDays(2 + i));

        var before = gateway.Deleted.Count;
        await RunAsync(services, gateway);
        Assert.Equal(before, gateway.Deleted.Count);

        for (var i = 0; i < 10; i++)
            await RunAsync(services, gateway);

        // Every one of them, and nothing else: no card of the new events, no message Modbot did not post.
        Assert.Equal(
            seeded.Order(StringComparer.Ordinal),
            gateway.Deleted.Select(d => d.MessageId).Order(StringComparer.Ordinal));
    }
}
