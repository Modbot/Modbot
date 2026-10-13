using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// The pass that tells signed-up members an event is about to start (event alerts design §5): once
/// per date and across passes, never for a cancelled event, following a start that moved, closed
/// direct messages final, members who left skipped, pings chunked to Discord's limits, and old
/// alerts deleted after 30 days.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventAlertMessagesTests
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string PingChannel = "333333333333333333";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

    private readonly PostgresFixture _db;

    public EventAlertMessagesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(
        PostgresFixture db, bool on = true, bool direct = true, bool ping = false, int minutes = 60)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.EventAlertsOn = on;
            s.EventAlertsDirectMessage = direct;
            s.EventAlertsPing = ping;
            s.EventAlertsChannelId = ping ? PingChannel : null;
            s.EventAlertsMinutesBefore = minutes;
        }, Ct);
        return services;
    }

    /// <summary>One pass, with no real waiting; the pauses it asked for are put in <paramref name="delays"/>.</summary>
    private static async Task<int> PassAsync(TestServices services, FakeGateway gateway, List<TimeSpan>? delays = null)
    {
        await using var context = services.Database.NewContext();
        using var scope = services.Scope();

        return await new EventAlertMessages(
                context,
                services.Clock,
                scope.ServiceProvider.GetRequiredService<IFactWriter>(),
                scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>(),
                delay: (by, _) =>
                {
                    delays?.Add(by);
                    return Task.CompletedTask;
                })
            .RunOnceAsync(gateway, Ct);
    }

    /// <summary>A scheduled event published to this Discord, with the category (if any) it belongs to.</summary>
    private static async Task<CalendarEvent> AddEventAsync(
        TestServices services, TimeSpan startsIn, CalendarCategory? category = null, Action<CalendarEvent>? shape = null)
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
            PostToChannel = false,
            ChannelId = Channel,
            AccessType = "members",
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (category is not null)
            e.Categories.Add(category);

        shape?.Invoke(e);
        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        context.CalendarEventPlaces.Add(new CalendarEventPlace
        {
            EventId = e.Id,
            Place = CalendarPlaces.DiscordEvent,
            State = CalendarPlaceStates.Published,
            ExternalId = "5551234",
            ChannelId = Channel,
            OccurrenceStartsAt = e.OccurrenceStartsAt,
            UpdatedAt = now,
        });

        await context.SaveChangesAsync(Ct);
        return e;
    }

    private static CalendarCategory NewCategory(TestServices services, string name = "Movies") => new()
    {
        Id = Guid.CreateVersion7(),
        Name = name,
        CreatedAt = services.Clock.UtcNow,
        UpdatedAt = services.Clock.UtcNow,
    };

    private static async Task SignUpAsync(TestServices services, CalendarCategory category, params string[] users)
    {
        await using var context = services.Database.NewContext();

        foreach (var user in users)
        {
            context.EventAlertSignups.Add(new EventAlertSignup
            {
                DiscordUserId = user,
                CategoryId = category.Id,
                CreatedAt = services.Clock.UtcNow,
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    private static async Task ChooseWayAsync(TestServices services, string user, string way)
    {
        await using var context = services.Database.NewContext();
        context.EventAlertMembers.Add(new EventAlertMember { DiscordUserId = user, Way = way, UpdatedAt = services.Clock.UtcNow });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<List<EventAlertDelivery>> DeliveriesAsync(TestServices services)
    {
        await using var context = services.Database.NewContext();
        return await context.EventAlertDeliveries.AsNoTracking().OrderBy(d => d.DiscordUserId).ToListAsync(Ct);
    }

    private static async Task<List<EventAlert>> AlertsAsync(TestServices services)
    {
        await using var context = services.Database.NewContext();
        return await context.EventAlerts.AsNoTracking().ToListAsync(Ct);
    }

    private static async Task SetEventAsync(TestServices services, Guid id, Action<CalendarEvent> change)
    {
        await using var context = services.Database.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        await context.SaveChangesAsync(Ct);
    }

    private static int Count(ModbotEvent fact, string key) => JsonNode.Parse(fact.Data)![key]!.GetValue<int>();

    [Fact]
    public async Task AMemberIsToldOnceBeforeTheEvent_AndTheAlertFinishesWithOneFact()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        await AddEventAsync(services, TimeSpan.FromMinutes(30), category);
        await SignUpAsync(services, category, "100", "200");
        var gateway = new FakeGateway();
        var delays = new List<TimeSpan>();

        await PassAsync(services, gateway, delays);

        Assert.Equal(["100", "200"], gateway.DirectMessages.Select(m => m.UserId).Order());
        Assert.Contains("Movie night", gateway.DirectMessages[0].Text, StringComparison.Ordinal);
        Assert.Equal([EventReminderMessages.Between], delays);
        Assert.All(await DeliveriesAsync(services), d => Assert.Equal(EventAlertDeliveryStates.Sent, d.State));

        // Later passes do not tell them again, and the fact is written once.
        await PassAsync(services, gateway);
        await PassAsync(services, gateway);

        Assert.Equal(2, gateway.DirectMessages.Count);
        Assert.NotNull(Assert.Single(await AlertsAsync(services)).FinishedAt);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.EventAlertSent, Ct));
        Assert.Equal(2, Count(fact, "direct"));
        Assert.Equal(0, Count(fact, "ping"));
        Assert.Equal(0, Count(fact, "closed"));
        Assert.Equal(0, Count(fact, "failed"));
        Assert.Equal(0, Count(fact, "skipped"));
        Assert.Equal("Movie night", JsonNode.Parse(fact.Data)!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task NothingIsQueued_WhenAlertsAreOff_OrTheStartIsFurtherAwayThanTheMinutes()
    {
        await using var off = await SetUpAsync(_db, on: false);
        var category = NewCategory(off);
        await AddEventAsync(off, TimeSpan.FromMinutes(30), category);
        await SignUpAsync(off, category, "100");
        var gateway = new FakeGateway();

        await PassAsync(off, gateway);
        Assert.Empty(await AlertsAsync(off));
        Assert.Empty(gateway.DirectMessages);

        await using var far = await SetUpAsync(_db);
        var farCategory = NewCategory(far);
        await AddEventAsync(far, TimeSpan.FromMinutes(90), farCategory);
        await SignUpAsync(far, farCategory, "100");

        await PassAsync(far, gateway);
        Assert.Empty(await AlertsAsync(far));

        // The minutes are the admin's: 120 brings it in.
        await far.ConfigureAsync(s => s.EventAlertsMinutesBefore = 120, Ct);
        await PassAsync(far, gateway);
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task AMemberSignedUpThroughTwoCategoriesAndTheEventItself_IsToldOnce()
    {
        await using var services = await SetUpAsync(_db);
        var first = NewCategory(services, "Movies");
        var e = await AddEventAsync(services, TimeSpan.FromMinutes(30), first, x => x.Categories.Add(NewCategory(services, "Games")));
        await SignUpAsync(services, first, "100");

        await using (var context = services.Database.NewContext())
        {
            var second = (await context.CalendarEvents.SingleAsync(x => x.Id == e.Id, Ct)).Categories.Single(c => c.Name == "Games");
            context.EventAlertSignups.Add(new EventAlertSignup { DiscordUserId = "100", CategoryId = second.Id, CreatedAt = services.Clock.UtcNow });
            context.EventAlertSignups.Add(new EventAlertSignup { DiscordUserId = "100", EventId = e.Id, CreatedAt = services.Clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Single(gateway.DirectMessages);
        Assert.Single(await DeliveriesAsync(services));
    }

    [Fact]
    public async Task ACancelledEventQueuesNothing_AndWaitingDeliveriesForItAreSkipped()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        var cancelled = await AddEventAsync(services, TimeSpan.FromMinutes(30), category);
        await SetEventAsync(services, cancelled.Id, x => x.State = CalendarEventStates.Cancelled);
        await SignUpAsync(services, category, "100");
        var gateway = new FakeGateway();

        await PassAsync(services, gateway);

        Assert.Empty(gateway.DirectMessages);
        Assert.Empty(await AlertsAsync(services));

        // Queued while it was on, then cancelled before the message went.
        await using var later = await SetUpAsync(_db);
        var laterCategory = NewCategory(later);
        var e = await AddEventAsync(later, TimeSpan.FromMinutes(30), laterCategory);
        await SignUpAsync(later, laterCategory, "100");
        var alert = await QueueByHandAsync(later, e, "100");

        await SetEventAsync(later, e.Id, x => x.State = CalendarEventStates.Cancelled);
        await PassAsync(later, gateway);

        Assert.Empty(gateway.DirectMessages);
        var delivery = Assert.Single(await DeliveriesAsync(later));
        Assert.Equal(alert.Id, delivery.AlertId);
        Assert.Equal(EventAlertDeliveryStates.Skipped, delivery.State);
        Assert.Equal(1, Count(Assert.Single(await later.FactsOfTypeAsync(FactType.EventAlertSent, Ct)), "skipped"));
    }

    /// <summary>An alert already queued for the event's date, with one waiting direct delivery.</summary>
    private static async Task<EventAlert> QueueByHandAsync(TestServices services, CalendarEvent e, string user)
    {
        var alert = new EventAlert { EventId = e.Id, OccurrenceStartsAt = e.StartsAt, QueuedAt = services.Clock.UtcNow };

        await using var context = services.Database.NewContext();
        context.EventAlerts.Add(alert);
        context.EventAlertDeliveries.Add(new EventAlertDelivery
        {
            AlertId = alert.Id,
            DiscordUserId = user,
            Way = EventAlertSettings.Direct,
            UpdatedAt = services.Clock.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        return alert;
    }

    [Fact]
    public async Task AStartMovedBeforeTheQueue_AlertsAtTheNewTime()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        var e = await AddEventAsync(services, TimeSpan.FromHours(5), category);
        await SignUpAsync(services, category, "100");
        var gateway = new FakeGateway();

        await PassAsync(services, gateway);
        Assert.Empty(await AlertsAsync(services));

        var moved = services.Clock.UtcNow.AddMinutes(20);
        await SetEventAsync(services, e.Id, x =>
        {
            x.StartsAt = moved;
            x.EndsAt = moved.AddHours(2);
            CalendarTimeline.Advance(x, services.Clock.UtcNow);
        });

        await PassAsync(services, gateway);

        Assert.Single(gateway.DirectMessages);
        Assert.Equal(moved, Assert.Single(await AlertsAsync(services)).OccurrenceStartsAt);
    }

    [Fact]
    public async Task AStartMovedAfterTheAlertWasQueued_DoesNotAlertAgain_AndAWaitingDeliveryFollowsTheNewTime()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        var e = await AddEventAsync(services, TimeSpan.FromMinutes(30), category);
        await SignUpAsync(services, category, "100", "200");
        var gateway = new FakeGateway();

        await PassAsync(services, gateway);
        Assert.Equal(2, gateway.DirectMessages.Count);

        // Moved into another hour after it went: a one-time event is alerted once for good.
        var moved = services.Clock.UtcNow.AddMinutes(10);
        await SetEventAsync(services, e.Id, x =>
        {
            x.StartsAt = moved;
            x.EndsAt = moved.AddHours(2);
            CalendarTimeline.Advance(x, services.Clock.UtcNow);
        });

        await PassAsync(services, gateway);
        Assert.Equal(2, gateway.DirectMessages.Count);
        Assert.Single(await AlertsAsync(services));

        // A delivery still waiting when the start moves later waits for the new time.
        await using var waiting = await SetUpAsync(_db);
        var otherCategory = NewCategory(waiting);
        var other = await AddEventAsync(waiting, TimeSpan.FromMinutes(30), otherCategory);
        await QueueByHandAsync(waiting, other, "300");
        var later = waiting.Clock.UtcNow.AddHours(3);
        await SetEventAsync(waiting, other.Id, x =>
        {
            x.StartsAt = later;
            x.EndsAt = later.AddHours(2);
            CalendarTimeline.Advance(x, waiting.Clock.UtcNow);
        });

        var quiet = new FakeGateway();
        await PassAsync(waiting, quiet);
        Assert.Empty(quiet.DirectMessages);
        Assert.Equal(EventAlertDeliveryStates.Waiting, Assert.Single(await DeliveriesAsync(waiting)).State);

        waiting.Clock.Advance(TimeSpan.FromMinutes(121));
        await PassAsync(waiting, quiet);
        Assert.Single(quiet.DirectMessages);
    }

    [Fact]
    public async Task ARepeatingEvent_AlertsEachDateOnce()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        await AddEventAsync(services, TimeSpan.FromMinutes(30), category, e => e.Repeat = CalendarRepeats.Daily);
        await SignUpAsync(services, category, "100");
        var gateway = new FakeGateway();

        await PassAsync(services, gateway);
        await PassAsync(services, gateway);
        Assert.Single(gateway.DirectMessages);

        // The next day's date is a new date.
        services.Clock.Advance(TimeSpan.FromHours(24));
        await PassAsync(services, gateway);
        await PassAsync(services, gateway);

        Assert.Equal(2, gateway.DirectMessages.Count);
        Assert.Equal(2, (await AlertsAsync(services)).Count);
    }

    [Fact]
    public async Task AClosedDirectMessage_IsFailedAndLoggedInTheFact_AndNeverTriedAgain()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        await AddEventAsync(services, TimeSpan.FromMinutes(30), category);
        await SignUpAsync(services, category, "100");
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        await PassAsync(services, gateway);
        gateway.DirectMessagesClosed = false;
        await PassAsync(services, gateway);

        Assert.Empty(gateway.DirectMessages);

        var delivery = Assert.Single(await DeliveriesAsync(services));
        Assert.Equal(EventAlertDeliveryStates.Failed, delivery.State);
        Assert.True(delivery.DirectMessagesClosed);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.EventAlertSent, Ct));
        Assert.Equal(1, Count(fact, "closed"));
        Assert.Equal(0, Count(fact, "failed"));
        Assert.Equal(0, Count(fact, "direct"));
    }

    [Fact]
    public async Task AMemberWhoLeft_AndABannedOne_AreSkipped()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        await AddEventAsync(services, TimeSpan.FromMinutes(30), category);
        await SignUpAsync(services, category, "100", "200", "300");

        await using (var context = services.Database.NewContext())
        {
            foreach (var (id, left) in new[] { ("100", true), ("300", false) })
            {
                context.DiscordMembers.Add(new DiscordMember
                {
                    GuildId = Guild,
                    UserId = id,
                    Username = "u" + id,
                    DisplayName = "u" + id,
                    FirstSeenAt = services.Clock.UtcNow,
                    UpdatedAt = services.Clock.UtcNow,
                    LeftAt = left ? services.Clock.UtcNow.AddDays(-1) : null,
                });
            }

            context.DiscordBans.Add(new DiscordBan
            {
                GuildId = Guild,
                UserId = "200",
                FirstSeenAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });

            await context.SaveChangesAsync(Ct);
        }

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Equal(["300"], gateway.DirectMessages.Select(m => m.UserId));

        var states = (await DeliveriesAsync(services)).ToDictionary(d => d.DiscordUserId, d => d.State);
        Assert.Equal(EventAlertDeliveryStates.Skipped, states["100"]);
        Assert.Equal(EventAlertDeliveryStates.Skipped, states["200"]);
        Assert.Equal(EventAlertDeliveryStates.Sent, states["300"]);
        Assert.Equal(2, Count(Assert.Single(await services.FactsOfTypeAsync(FactType.EventAlertSent, Ct)), "skipped"));
    }

    [Fact]
    public async Task DirectMessagesGoFiveAPass_TwoSecondsApart()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        await AddEventAsync(services, TimeSpan.FromMinutes(30), category);
        await SignUpAsync(services, category, [.. Enumerable.Range(1, 7).Select(n => n.ToString(CultureInfo.InvariantCulture))]);
        var gateway = new FakeGateway();
        var delays = new List<TimeSpan>();

        await PassAsync(services, gateway, delays);

        Assert.Equal(EventReminderMessages.PerPass, gateway.DirectMessages.Count);
        Assert.Equal(EventReminderMessages.PerPass - 1, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(2), d));

        // Not finished yet: no fact until the last one is done.
        Assert.Empty(await services.FactsOfTypeAsync(FactType.EventAlertSent, Ct));

        await PassAsync(services, gateway);
        Assert.Equal(7, gateway.DirectMessages.Count);
        Assert.Single(await services.FactsOfTypeAsync(FactType.EventAlertSent, Ct));
    }

    [Fact]
    public async Task PingMembersGoInPostsThatFitDiscordsLimits_AndMentionOnlyThem()
    {
        await using var services = await SetUpAsync(_db, direct: false, ping: true);
        var category = NewCategory(services);
        await AddEventAsync(services, TimeSpan.FromMinutes(30), category);

        var users = Enumerable.Range(0, 130)
            .Select(n => (100000000000000000L + n).ToString(CultureInfo.InvariantCulture))
            .ToArray();
        await SignUpAsync(services, category, users);
        var gateway = new FakeGateway();

        // One post a pass.
        await PassAsync(services, gateway);
        Assert.Single(gateway.Pings);
        await PassAsync(services, gateway);
        await PassAsync(services, gateway);

        Assert.Equal(2, gateway.Pings.Count);
        Assert.All(gateway.Pings, p =>
        {
            Assert.Equal(PingChannel, p.ChannelId);
            Assert.InRange(p.UserIds.Count, 1, EventAlertMessages.MostPerPing);

            // The gateway writes each mention and a space, then the words.
            Assert.True(p.UserIds.Sum(u => u.Length + 4) + p.Text.Length <= EventAlertMessages.MessageLength);
        });

        // Everybody once, in no more posts than needed.
        Assert.Equal(users.Order(), gateway.Pings.SelectMany(p => p.UserIds).Order());
        Assert.Empty(gateway.DirectMessages);
        Assert.All(await DeliveriesAsync(services), d => Assert.Equal(EventAlertDeliveryStates.Sent, d.State));
        Assert.Equal(130, Count(Assert.Single(await services.FactsOfTypeAsync(FactType.EventAlertSent, Ct)), "ping"));
    }

    [Fact]
    public void FitsInPing_StopsAtHundredMembers_AndAtTheMessageLength()
    {
        var short1 = Enumerable.Range(1, 150).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(100, EventAlertMessages.FitsInPing(short1, "x"));

        var long18 = Enumerable.Range(0, 150).Select(n => (100000000000000000L + n).ToString(CultureInfo.InvariantCulture)).ToList();
        var fits = EventAlertMessages.FitsInPing(long18, new string('x', 150));
        Assert.InRange(fits, 80, 100);
        Assert.True(fits * (18 + 4) + 150 <= EventAlertMessages.MessageLength);
        Assert.True((fits + 1) * (18 + 4) + 150 > EventAlertMessages.MessageLength);

        // However long the words, at least one member goes.
        Assert.Equal(1, EventAlertMessages.FitsInPing(long18, new string('x', 1990)));
    }

    [Fact]
    public async Task APingMemberWithTheWayOff_FallsBackToTheOtherWay_AndTwoWaysOffQueuesNothing()
    {
        await using var services = await SetUpAsync(_db, direct: true, ping: false);
        var category = NewCategory(services);
        await AddEventAsync(services, TimeSpan.FromMinutes(30), category);
        await SignUpAsync(services, category, "100");
        await ChooseWayAsync(services, "100", EventAlertSettings.Ping);
        var gateway = new FakeGateway();

        await PassAsync(services, gateway);
        Assert.Single(gateway.DirectMessages);

        await using var none = await SetUpAsync(_db, direct: false, ping: false);
        var noneCategory = NewCategory(none);
        await AddEventAsync(none, TimeSpan.FromMinutes(30), noneCategory);
        await SignUpAsync(none, noneCategory, "100");

        await PassAsync(none, gateway);
        Assert.Empty(await AlertsAsync(none));
    }

    [Fact]
    public async Task AlertsAreDeletedWithTheirDeliveries30DaysAfterTheyWereQueued()
    {
        await using var services = await SetUpAsync(_db);
        var category = NewCategory(services);
        var e = await AddEventAsync(services, TimeSpan.FromDays(60), category);
        var now = services.Clock.UtcNow;

        var old = await QueueByHandAsync(services, e, "100");
        var recent = new EventAlert { EventId = e.Id, OccurrenceStartsAt = e.StartsAt.AddDays(1), QueuedAt = now - TimeSpan.FromDays(29) };

        await using (var context = services.Database.NewContext())
        {
            await context.EventAlerts.Where(a => a.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.QueuedAt, now - TimeSpan.FromDays(31)), Ct);
            context.EventAlerts.Add(recent);
            context.EventAlertDeliveries.Add(new EventAlertDelivery { AlertId = recent.Id, DiscordUserId = "200", UpdatedAt = now });
            await context.SaveChangesAsync(Ct);
        }

        await PassAsync(services, new FakeGateway());

        Assert.Equal(recent.Id, Assert.Single(await AlertsAsync(services)).Id);
        Assert.Equal("200", Assert.Single(await DeliveriesAsync(services)).DiscordUserId);
        Assert.Equal(TimeSpan.FromDays(30), EventAlertMessages.KeptFor);
    }
}
