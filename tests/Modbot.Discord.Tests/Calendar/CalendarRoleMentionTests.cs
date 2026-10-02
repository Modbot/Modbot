using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// Calendar design §3.3.1 (2026-10-02): an event's channel post can mention one role. The role is
/// pinged once per date, when that date's post first goes up; an edit, or the same date's post made
/// again, shows the role and pings nobody. Never @everyone.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarRoleMentionTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string GameNight = "333333333333333333";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

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
            Title = "Game night",
            Description = "Bring a controller",
            StartsAt = now + TimeSpan.FromHours(1),
            EndsAt = now + TimeSpan.FromHours(3),
            TimeZone = "UTC",
            WorldId = World,
            State = CalendarEventStates.Scheduled,
            PostToChannel = true,
            ChannelId = Channel,
            MentionRoleId = GameNight,
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

    private static async Task<TestServices> StartAsync(PostgresFixture db)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        return services;
    }

    [Fact]
    public async Task TheFirstPostPingsTheRole_AndNothingElse()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        await AddEventAsync(services);
        await RunAsync(services, gateway);

        var ping = Assert.Single(gateway.RolePings);
        Assert.Equal(Channel, ping.ChannelId);
        Assert.Equal(GameNight, ping.RoleId);
        Assert.Equal($"<@&{GameNight}>", Assert.Single(gateway.Messages).Text);
    }

    [Fact]
    public async Task AnEditShowsTheRole_ButNeverPingsItAgain()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services);
        await RunAsync(services, gateway);

        await ChangeAsync(services, e.Id, x => x.Description = "Bring two controllers");
        await RunAsync(services, gateway);

        var edit = Assert.Single(gateway.Edits);
        Assert.Equal($"<@&{GameNight}>", edit.Text);

        // Edits go through EditAsync, which sends mentions off; the only ping is the first post's.
        Assert.Single(gateway.RolePings);
        Assert.Single(gateway.Messages);
    }

    [Fact]
    public async Task APostMadeAgainForTheSameDate_DoesNotPingAgain()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services);
        await RunAsync(services, gateway);

        // Somebody deleted the post in Discord: the next edit finds it gone.
        gateway.FailNextEdit("That message is gone, or was not posted by the bot.", permanent: true);
        await ChangeAsync(services, e.Id, x => x.Description = "Changed");
        await RunAsync(services, gateway);

        // The event changes again, so the post is made again, for the same date.
        await ChangeAsync(services, e.Id, x => x.Description = "Changed again");
        await RunAsync(services, gateway);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Equal($"<@&{GameNight}>", gateway.Messages[1].Text);
        Assert.Single(gateway.RolePings);
    }

    [Fact]
    public async Task APostTurnedOffAndOnAgain_DoesNotPingTheSameDateTwice()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services);
        await RunAsync(services, gateway);

        await ChangeAsync(services, e.Id, x => x.PostToChannel = false);
        await RunAsync(services, gateway);
        Assert.Single(gateway.Deleted);

        await ChangeAsync(services, e.Id, x => x.PostToChannel = true);
        await RunAsync(services, gateway);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Single(gateway.RolePings);
    }

    [Fact]
    public async Task EachDateOfARepeatingEvent_PingsOnce_OnItsOwnFirstPost()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services, x => x.Repeat = CalendarRepeats.Daily);
        await RunAsync(services, gateway);

        // The first date ends; the scheduler moves the event on to the next.
        services.Clock.Advance(TimeSpan.FromHours(4));
        await ChangeAsync(services, e.Id, x => CalendarTimeline.Advance(x, services.Clock.UtcNow));
        await RunAsync(services, gateway);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Equal(2, gateway.RolePings.Count);

        // The old card's last word is an edit, and pings nobody.
        Assert.Equal("Finished", Assert.Single(gateway.Edits).Embeds[0].Footer);

        // Both dates are remembered, not only the latest.
        await using var context = services.Database.NewContext();
        Assert.Equal(2, await context.CalendarRolePings.CountAsync(p => p.EventId == e.Id, Ct));
    }

    [Fact]
    public async Task AnEventMovedAwayFromADateAndBack_DoesNotPingTheDateTwice()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        var e = await AddEventAsync(services);
        var dateA = e.StartsAt;
        await RunAsync(services, gateway);

        // Moved to another date: a post for that date, and its own ping.
        await ChangeAsync(services, e.Id, x =>
        {
            x.StartsAt = dateA.AddDays(1);
            x.EndsAt = dateA.AddDays(1).AddHours(2);
            CalendarTimeline.Advance(x, services.Clock.UtcNow);
        });
        await RunAsync(services, gateway);
        Assert.Equal(2, gateway.RolePings.Count);

        // And back to the first date: the post is made again, shows the role, and pings nobody.
        await ChangeAsync(services, e.Id, x =>
        {
            x.StartsAt = dateA;
            x.EndsAt = dateA.AddHours(2);
            CalendarTimeline.Advance(x, services.Clock.UtcNow);
        });
        await RunAsync(services, gateway);

        Assert.Equal(3, gateway.Messages.Count);
        Assert.Equal($"<@&{GameNight}>", gateway.Messages[2].Text);
        Assert.Equal(2, gateway.RolePings.Count);
    }

    [Fact]
    public async Task Everyone_IsNeverMentioned()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        // @everyone's role id is the server's own.
        await AddEventAsync(services, x => x.MentionRoleId = Guild);
        await RunAsync(services, gateway);

        Assert.Empty(gateway.RolePings);
        Assert.Null(Assert.Single(gateway.Messages).Text);
        Assert.Null(CalendarDiscordPublisher.RoleMention(new CalendarEvent { MentionRoleId = Guild }, Guild));
    }

    [Fact]
    public async Task WithoutARole_ThePostHasNoTextAndPingsNobody()
    {
        await using var services = await StartAsync(db);
        var gateway = new FakeGateway();

        await AddEventAsync(services, x => x.MentionRoleId = null);
        await RunAsync(services, gateway);

        Assert.Empty(gateway.RolePings);
        Assert.Null(Assert.Single(gateway.Messages).Text);
    }
}
