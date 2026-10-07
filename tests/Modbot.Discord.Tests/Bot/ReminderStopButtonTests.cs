using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Bot;

/// <summary>
/// A reminder's Stop pressed in the direct message <c>/remindme</c> sent, through the bot's routing:
/// answered once, by the reminder and nothing else, for the member who was reminded.
/// </summary>
/// <remarks>
/// The press carries no server, so the gateway lets it through only by its server mark (checked in
/// <c>RemindMeCommandTests</c>); this is what happens after that.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class ReminderStopButtonTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public ReminderStopButtonTests(PostgresFixture db) => _db = db;

    private static DiscordBotService Service(TestServices services, FakeGatewayFactory gateways)
        => new(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new DiscordBotOptions
            {
                FirstRetry = TimeSpan.FromSeconds(30),
                MaxRetry = TimeSpan.FromMinutes(10),
                RebuildAfterDisconnected = TimeSpan.FromMinutes(3),
            },
            (_, _) => Task.CompletedTask);

    private static async Task<FakeGateway> ConnectedAsync(TestServices services, CancellationToken ct)
    {
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = Guild;
        }, ct);
        await bot.TickAsync(ct);

        return gateway;
    }

    private static async Task<EventReminder> AddReminderAsync(TestServices services, string user, CancellationToken ct)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now.AddDays(3),
            EndsAt = now.AddDays(3).AddHours(2),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            AccessType = "members",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var reminder = new EventReminder
        {
            DiscordUserId = user,
            EventId = e.Id,
            OccurrenceStartsAt = e.StartsAt,
            MinutesBefore = 60,
            RemindAt = e.StartsAt.AddHours(-1),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        context.EventReminders.Add(reminder);
        await context.SaveChangesAsync(ct);
        return reminder;
    }

    [Fact]
    public async Task APressInADirectMessage_IsAnsweredOnceByTheReminder_AndStopsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = await ConnectedAsync(services, ct);
        var reminder = await AddReminderAsync(services, "8080", ct);

        var answers = new List<DiscordReply>();
        var press = new DiscordButtonPress(
            "8080", "member", RemindMeCommand.StopFor(reminder.Id, Guild).Id,
            (reply, _) =>
            {
                answers.Add(reply);
                return Task.CompletedTask;
            });

        await gateway.RaiseButtonAsync(press);

        Assert.Equal(RemindMeCommand.StoppedMessage, Assert.Single(answers).Text);

        await using var context = services.Database.NewContext();
        Assert.Equal(EventReminderStates.Stopped, (await context.EventReminders.AsNoTracking().SingleAsync(ct)).State);
    }

    [Fact]
    public async Task APressByAnotherMember_IsAnsweredOnce_AndStopsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = await ConnectedAsync(services, ct);
        var reminder = await AddReminderAsync(services, "8080", ct);

        var answers = new List<DiscordReply>();
        var press = new DiscordButtonPress(
            "9090", "someone else", RemindMeCommand.StopFor(reminder.Id, Guild).Id,
            (reply, _) =>
            {
                answers.Add(reply);
                return Task.CompletedTask;
            });

        await gateway.RaiseButtonAsync(press);

        Assert.Equal(RemindMeCommand.NothingToStopMessage, Assert.Single(answers).Text);

        await using var context = services.Database.NewContext();
        Assert.Equal(EventReminderStates.Waiting, (await context.EventReminders.AsNoTracking().SingleAsync(ct)).State);
    }
}
