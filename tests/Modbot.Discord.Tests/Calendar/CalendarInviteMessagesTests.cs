using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// Calendar auto-invite design §3 and §7: the direct message for somebody VRChat would not take an
/// invite for goes once, with the title and a Join button and nothing else; a closed inbox is "could
/// not reach"; and the Discord post for the first person goes once.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarInviteMessagesTests(PostgresFixture db)
{
    private const string Channel = "222222222222222222";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Location = World + ":12345~group(grp_x)~groupAccessType(members)~region(us)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> MessageAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        var invites = new CalendarInvites(
            context, new FactWriter(context, services.Clock), new EventPartitionMaintainer(context, services.Clock));

        return await new CalendarInviteMessages(context, services.Clock, invites, delay: (_, _) => Task.CompletedTask)
            .RunOnceAsync(gateway, Ct);
    }

    private static async Task<int> PostAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new CalendarFirstJoinPost(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    /// <summary>An open event with its instance, and the invite rows given.</summary>
    private static async Task<(CalendarEvent Event, Guid InstanceId)> OpenEventAsync(
        TestServices services, Action<CalendarEvent>? shape = null, params CalendarInvite[] rows)
    {
        // /me on: while it is off, no member's "Get event invites" counts.
        await services.ConfigureAsync(s => s.DiscordMeCommand = true, Ct);

        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + TimeSpan.FromMinutes(5),
            EndsAt = now + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            WorldId = World,
            State = CalendarEventStates.Scheduled,
            AutoOpen = true,
            PostToChannel = true,
            ChannelId = Channel,
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
            VRChatInstanceId = "12345",
            OpenedAt = now,
            LastSeenAt = now,
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
            InvitesQueuedAt = now,
        });

        foreach (var row in rows)
        {
            // Everybody here asked for event invites with /me; nobody else is sent anything.
            context.EventInviteChoices.Add(new EventInviteChoice
            {
                DiscordUserId = row.DiscordUserId!,
                Wants = true,
                ChangedAt = now,
            });

            row.EventId = e.Id;
            row.OccurrenceStartsAt = e.StartsAt;
            row.QueuedAt = now;
            row.UpdatedAt = now;
            context.CalendarInvites.Add(row);
        }

        await context.SaveChangesAsync(Ct);
        return (e, instance.Id);
    }

    private static CalendarInvite ToMessage(int position, string discordUserId) => new()
    {
        Position = position,
        Role = CalendarInviteRoles.List,
        PersonKey = $"discord:{discordUserId}",
        DiscordUserId = discordUserId,
        State = CalendarInviteStates.ToMessage,
    };

    private static async Task<List<CalendarInvite>> RowsAsync(TestServices services)
    {
        await using var context = services.Database.NewContext();
        return await context.CalendarInvites.AsNoTracking().OrderBy(i => i.Position).ToListAsync(Ct);
    }

    [Fact]
    public async Task TheDirectMessageIsTheTitleAndAJoinButton_SentOnce()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await OpenEventAsync(services, null, ToMessage(0, "100"), ToMessage(1, "200"));

        Assert.Equal(2, await MessageAsync(services, gateway));
        Assert.Equal(0, await MessageAsync(services, gateway));

        Assert.Equal(["100", "200"], gateway.DirectMessages.Select(m => m.UserId));
        var message = gateway.DirectMessages[0];
        Assert.Equal(CalendarInviteMessages.Text("Movie night"), message.Text);
        var join = Assert.Single(message.Links);
        Assert.Equal("Join", join.Label);
        Assert.Equal(Core.Data.InstanceJoinLink.For(Location), join.Url);

        Assert.All(await RowsAsync(services), r => Assert.Equal(CalendarInviteStates.Messaged, r.State));

        await using var context = services.Database.NewContext();
        Assert.Equal(2, await context.Events.CountAsync(f => f.Type == FactType.PlannedEventInviteSent, Ct));
    }

    [Fact]
    public async Task ClosedDirectMessagesAreCouldNotReach_AndNotAskedAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        await OpenEventAsync(services, null, ToMessage(0, "100"));

        Assert.Equal(0, await MessageAsync(services, gateway));
        gateway.DirectMessagesClosed = false;
        Assert.Equal(0, await MessageAsync(services, gateway));

        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(CalendarInviteStates.CouldNotReach, Assert.Single(await RowsAsync(services)).State);
    }

    [Fact]
    public async Task SomebodyWhoStoppedEventInvites_GetsNoMessage()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await OpenEventAsync(services, null, ToMessage(0, "100"));

        // They pressed "Stop event invites" after the queue was written.
        await using (var context = services.Database.NewContext())
        {
            var choice = await context.EventInviteChoices.SingleAsync(c => c.DiscordUserId == "100", Ct);
            choice.Wants = false;
            await context.SaveChangesAsync(Ct);
        }

        await MessageAsync(services, gateway);

        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(CalendarInviteStates.NotAsked, Assert.Single(await RowsAsync(services)).State);
    }

    [Fact]
    public async Task WhileMeIsOff_NoMemberIsMessaged()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await OpenEventAsync(services, null, ToMessage(0, "100"));
        await services.ConfigureAsync(s => s.DiscordMeCommand = false, Ct);

        await MessageAsync(services, gateway);

        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(CalendarInviteStates.NotAsked, Assert.Single(await RowsAsync(services)).State);
    }

    [Fact]
    public async Task NothingIsSentOnceTheInstanceHasClosed()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        var (_, instanceId) = await OpenEventAsync(services, null, ToMessage(0, "100"));

        await using (var context = services.Database.NewContext())
        {
            var instance = await context.VRChatInstances.SingleAsync(i => i.Id == instanceId, Ct);
            instance.ClosedAt = services.Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        await MessageAsync(services, gateway);

        Assert.Empty(gateway.DirectMessages);
        Assert.Equal(CalendarInviteStates.Stopped, Assert.Single(await RowsAsync(services)).State);
    }

    [Fact]
    public async Task TheDiscordPostForTheFirstPersonGoesOnce()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        var (_, instanceId) = await OpenEventAsync(services, e => e.AnnounceFirstJoinInDiscord = true);

        // Nobody in yet.
        Assert.Equal(0, await PostAsync(services, gateway));

        await using (var context = services.Database.NewContext())
        {
            var instance = await context.VRChatInstances.SingleAsync(i => i.Id == instanceId, Ct);
            instance.LastUserCount = 1;
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await PostAsync(services, gateway));
        Assert.Equal(0, await PostAsync(services, gateway));

        var post = Assert.Single(gateway.Messages);
        Assert.Equal(Channel, post.ChannelId);
        Assert.Equal(CalendarFirstJoinPost.Text("Movie night"), post.Text);
        Assert.Equal("Join", Assert.Single(post.Links).Label);
    }
}
