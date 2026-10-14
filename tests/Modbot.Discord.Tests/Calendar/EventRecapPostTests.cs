using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// The event recap: it goes in the chosen channel once the instance of a date has closed, with who
/// came, the most people at once and the next date, and starts a photo thread on the post.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventRecapPostTests(PostgresFixture db)
{
    private const string Channel = "333333333333333333";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Number = "54321";
    private const string Location = World + ":" + Number + "~group(grp_x)~groupAccessType(members)~region(us)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> RecapAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new EventRecapPost(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    /// <summary>The recaps on, in the channel, from a moment before the event.</summary>
    private static Task TurnOnAsync(TestServices services) =>
        services.ConfigureAsync(s =>
        {
            s.EventRecapsOn = true;
            s.EventRecapChannelId = Channel;
            s.EventRecapsFrom = services.Clock.UtcNow - TimeSpan.FromHours(1);
        }, Ct);

    /// <summary>A weekly event that opened an instance an hour ago, ending in an hour.</summary>
    private static async Task<(CalendarEvent Event, Guid InstanceId)> OpenedAsync(
        TestServices services, Action<CalendarEvent>? shape = null)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + TimeSpan.FromMinutes(5),
            EndsAt = now + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            Repeat = CalendarRepeats.Weekly,
            WorldId = World,
            State = CalendarEventStates.Scheduled,
            AutoOpen = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        shape?.Invoke(e);
        CalendarTimeline.Advance(e, now);

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = Location,
            WorldId = World,
            VRChatInstanceId = Number,
            OpenedAt = now,
            LastSeenAt = now,
            PeakUserCount = 7,
        };

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        context.VRChatInstances.Add(instance);
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = e.StartsAt,
            AttemptedAt = now,
            Location = Location,
            InstanceId = instance.Id,
        });

        await context.SaveChangesAsync(Ct);
        return (e, instance.Id);
    }

    private static async Task SawArriveAsync(TestServices services, string userId, string name, string type = FactType.InstanceJoined)
    {
        await using var context = services.Database.NewContext();
        await new FactWriter(context, services.Clock).WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = services.Clock.UtcNow,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = userId,
                WorldId = World,
                InstanceId = Number,
                Source = FactSource.Manual,
            }, Ct);

        if (!await context.VRChatUsers.AnyAsync(u => u.UserId == userId, Ct))
        {
            context.VRChatUsers.Add(new VRChatUser { UserId = userId, DisplayName = name });
            await context.SaveChangesAsync(Ct);
        }
    }

    private static async Task CloseAsync(TestServices services, Guid instanceId)
    {
        await using var context = services.Database.NewContext();
        var instance = await context.VRChatInstances.SingleAsync(i => i.Id == instanceId, Ct);
        instance.ClosedAt = services.Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task NothingIsPostedWhileTheInstanceIsOpen_AndTheRecapGoesOnceItHasClosed()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        var (_, instanceId) = await OpenedAsync(services);
        await SawArriveAsync(services, "usr_a", "Ada");
        await SawArriveAsync(services, "usr_b", "Bo", FactType.InstancePresenceObserved);

        Assert.Equal(0, await RecapAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        services.Clock.Advance(TimeSpan.FromMinutes(30));
        await CloseAsync(services, instanceId);

        Assert.Equal(1, await RecapAsync(services, gateway));
        Assert.Equal(0, await RecapAsync(services, gateway));

        var post = Assert.Single(gateway.Messages);
        Assert.Equal(Channel, post.ChannelId);
        var card = Assert.Single(post.Embeds);
        Assert.Equal("Recap: Movie night", card.Title);
        Assert.Equal("Ada, Bo", card.Description);
        Assert.Contains(card.Fields, f => f.Name == "Came" && f.Value == "2 people");
        Assert.Contains(card.Fields, f => f.Name == "Most at once" && f.Value == "7");
        Assert.Contains(card.Fields, f => f.Name == "Next date");
    }

    [Fact]
    public async Task APhotoThreadIsStartedOnThePost_NamedAfterTheEventAndTheDate()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        var (_, instanceId) = await OpenedAsync(services);
        await SawArriveAsync(services, "usr_a", "Ada");
        await CloseAsync(services, instanceId);

        Assert.Equal(1, await RecapAsync(services, gateway));

        var thread = Assert.Single(gateway.ThreadsStarted);
        Assert.Equal(Channel, thread.ChannelId);
        Assert.Equal(Assert.Single(gateway.Messages).MessageId, thread.MessageId);
        Assert.Equal("Movie night 13 Sep 2026", thread.Name);
    }

    [Fact]
    public async Task WithoutCreatePublicThreads_TheRecapStillGoesUp_AndTheCardShowsWhy()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway { StartThreadError = "Could not start the thread: the bot may not start threads in that channel." };

        await TurnOnAsync(services);
        var (_, instanceId) = await OpenedAsync(services);
        await SawArriveAsync(services, "usr_a", "Ada");
        await CloseAsync(services, instanceId);

        Assert.Equal(1, await RecapAsync(services, gateway));
        Assert.Single(gateway.Messages);
        Assert.Empty(gateway.ThreadsStarted);

        await using var context = services.Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.Contains("without its photo thread", settings.EventRecapProblem);
    }

    [Fact]
    public async Task NothingIsPostedWhileTheRecapsAreOff_OrForAnInstanceThatClosedBeforeTheyWereTurnedOn()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        var (_, instanceId) = await OpenedAsync(services);
        await SawArriveAsync(services, "usr_a", "Ada");
        await CloseAsync(services, instanceId);

        Assert.Equal(0, await RecapAsync(services, gateway));

        services.Clock.Advance(TimeSpan.FromHours(2));
        await services.ConfigureAsync(s =>
        {
            s.EventRecapsOn = true;
            s.EventRecapChannelId = Channel;
            s.EventRecapsFrom = services.Clock.UtcNow;
        }, Ct);

        Assert.Equal(0, await RecapAsync(services, gateway));
        Assert.Empty(gateway.Messages);
    }

    [Fact]
    public async Task ARefusedRecapThatIsFinalIsNotTriedAgain_AndTheCardShowsWhy()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        var (_, instanceId) = await OpenedAsync(services);
        await SawArriveAsync(services, "usr_a", "Ada");
        await CloseAsync(services, instanceId);

        gateway.FailNextPost("Could not post to Discord: the bot may not post in that channel.", permanent: true);

        Assert.Equal(0, await RecapAsync(services, gateway));
        Assert.Equal(0, await RecapAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        await using var context = services.Database.NewContext();
        Assert.Contains("may not post", (await context.GetSettingsAsync(Ct)).EventRecapProblem);
    }

    [Fact]
    public async Task ADateNobodyWasSeenAtGetsNoRecap()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        var (_, instanceId) = await OpenedAsync(services);

        await using (var context = services.Database.NewContext())
        {
            var instance = await context.VRChatInstances.SingleAsync(i => i.Id == instanceId, Ct);
            instance.PeakUserCount = 0;
            await context.SaveChangesAsync(Ct);
        }

        await CloseAsync(services, instanceId);

        Assert.Equal(0, await RecapAsync(services, gateway));
        Assert.Empty(gateway.Messages);
    }

    [Fact]
    public async Task AnEventThatDoesNotRepeat_HasNoNextDateLine()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        var (_, instanceId) = await OpenedAsync(services, e => e.Repeat = CalendarRepeats.None);
        await SawArriveAsync(services, "usr_a", "Ada");
        await CloseAsync(services, instanceId);

        Assert.Equal(1, await RecapAsync(services, gateway));

        var card = Assert.Single(Assert.Single(gateway.Messages).Embeds);
        Assert.DoesNotContain(card.Fields, f => f.Name == "Next date");
    }

    [Fact]
    public async Task TheAttendanceCountsEachPersonOnce_AndOnlyThoseSeenInThatInstance()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);

        var (e, _) = await OpenedAsync(services);
        await SawArriveAsync(services, "usr_a", "Ada");
        await SawArriveAsync(services, "usr_a", "Ada", FactType.InstancePresenceObserved);
        await SawArriveAsync(services, "usr_b", "Bo");

        await using var context = services.Database.NewContext();
        var turnout = Assert.Single(await new EventAttendance(context).ForEventAsync(e.Id, Ct));

        Assert.Equal(["usr_a", "usr_b"], turnout.People);
        Assert.Equal(7, turnout.PeakHeadCount);
    }
}
