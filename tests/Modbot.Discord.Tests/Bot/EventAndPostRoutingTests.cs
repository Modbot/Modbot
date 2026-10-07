using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Bot;

/// <summary>
/// <c>/event</c> and <c>/post</c> through the bot's own routing: a typed command, its form, its
/// buttons and its suggestions each reach the right handler, over the fake gateway, as they do in a
/// server (Discord commands design §3.10).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventAndPostRoutingTests
{
    private const string Guild = "424242";
    private const string Caller = "100";
    private const string Channel = "222222222222222222";

    private static readonly Guid Movie = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Friday = new(2026, 9, 18, 20, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public EventAndPostRoutingTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static async Task<(TestServices Services, FakeGateway Gateway)> ConnectedAsync(
        PostgresFixture db, ModbotPermissions held = ModbotPermissions.ManageCalendar | ModbotPermissions.ViewCalendar | ModbotPermissions.ManagePosts | ModbotPermissions.ViewPosts)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = Guild;
        }, Ct);
        await services.LinkedAccountAsync(Caller, held, ct: Ct);
        await bot.TickAsync(Ct);

        return (services, gateway);
    }

    [Fact]
    public async Task BothCommands_AreRegistered_ShownToEventHosts()
    {
        var (services, gateway) = await ConnectedAsync(_db);
        await using var _ = services;

        foreach (var name in new[] { "event", "post" })
        {
            var registered = Assert.Single(gateway.RegisteredCommands, c => c.Name == name);
            Assert.Equal(DiscordShownTo.EventHosts, registered.ShownTo);
        }
    }

    [Fact]
    public async Task APostCommand_ThatOpensAForm_IsNotAnsweredWithAReply()
    {
        var (services, gateway) = await ConnectedAsync(_db);
        await using var _ = services;

        var replies = new List<DiscordReply>();
        var forms = new List<DiscordForm>();

        await gateway.RaiseCommandAsync(new DiscordCommandCall(
            Caller,
            "host",
            DiscordCommands.Post,
            new Dictionary<string, string> { [DiscordCommands.PostChannelOption] = Channel },
            (reply, _) =>
            {
                replies.Add(reply);
                return Task.CompletedTask;
            },
            (form, _) =>
            {
                forms.Add(form);
                return Task.CompletedTask;
            })
        {
            Subcommand = DiscordCommands.PostNew,
        });

        Assert.Single(forms);
        Assert.Empty(replies);
    }

    [Fact]
    public async Task APostForm_AndItsButtons_AreAnsweredByThePostCommand_NotTheStaffHandler()
    {
        var (services, gateway) = await ConnectedAsync(_db);
        await using var _ = services;

        DiscordForm? form = null;
        await gateway.RaiseCommandAsync(new DiscordCommandCall(
            Caller,
            "host",
            DiscordCommands.Post,
            new Dictionary<string, string> { [DiscordCommands.PostChannelOption] = Channel },
            (_, _) => Task.CompletedTask,
            (shown, _) =>
            {
                form = shown;
                return Task.CompletedTask;
            })
        {
            Subcommand = DiscordCommands.PostNew,
        });

        Assert.NotNull(form);

        var answers = new List<DiscordReply>();
        await gateway.RaiseFormAsync(new DiscordFormSubmit(
            Caller,
            "host",
            form.Id,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [PostCommand.TitleField] = ["Movie night"],
                [PostCommand.TextField] = ["Friday at eight."],
            },
            (reply, _) =>
            {
                answers.Add(reply);
                return Task.CompletedTask;
            }));

        var preview = Assert.Single(answers);
        Assert.Equal($"Post this to <#{Channel}>?", preview.Text);
        Assert.Equal("**Movie night**\nFriday at eight.", Assert.Single(preview.Embeds).Description);

        var yes = preview.Actions!.Single(a => a.Label == PostCommand.PostNowLabel).Id;
        var updates = new List<DiscordReply>();

        await gateway.RaiseButtonAsync(new DiscordButtonPress(
            Caller, "host", yes, (_, _) => Task.CompletedTask, null, (reply, _) =>
            {
                updates.Add(reply);
                return Task.CompletedTask;
            }));

        Assert.Equal(["Posting…", $"Posting to <#{Channel}>."], updates.Select(u => u.Text));
        Assert.Single(services.Posts.Saved);
    }

    [Fact]
    public async Task AnEventButton_IsAnsweredByTheEventCommand()
    {
        var (services, gateway) = await ConnectedAsync(_db);
        await using var _ = services;
        services.Calendar.Plan = new CalendarCancelPlan(true, null, "Movie night", Friday, WholeEvent: false);

        var asked = new List<DiscordReply>();
        await gateway.RaiseCommandAsync(new DiscordCommandCall(
            Caller,
            "host",
            DiscordCommands.Event,
            new Dictionary<string, string>
            {
                [DiscordCommands.EventOption] = Movie.ToString(),
                [DiscordCommands.EventDateOption] = Friday.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            },
            (reply, _) =>
            {
                asked.Add(reply);
                return Task.CompletedTask;
            })
        {
            Subcommand = DiscordCommands.EventCancelDate,
        });

        var question = Assert.Single(asked);
        var yes = question.Actions!.Single(a => a.Label == EventCommand.CancelDateLabel).Id;
        var updates = new List<DiscordReply>();

        await gateway.RaiseButtonAsync(new DiscordButtonPress(
            Caller, "host", yes, (_, _) => Task.CompletedTask, null, (reply, _) =>
            {
                updates.Add(reply);
                return Task.CompletedTask;
            }));

        Assert.Equal("Cancelling…", updates[0].Text);
        Assert.Single(services.Calendar.Cancelled);
    }

    [Fact]
    public async Task TheEventSuggestions_AreAnsweredThroughTheGateway()
    {
        var (services, gateway) = await ConnectedAsync(_db);
        await using var _ = services;
        services.Calendar.Events.Add(new CalendarEventChoice(Movie, "Movie night", Friday, "Fri 18 Sep, 20:00 UTC"));

        IReadOnlyList<DiscordSuggestion>? offered = null;
        await gateway.RaiseSuggestionAskedAsync(new DiscordSuggestionAsk(
            Caller,
            DiscordCommands.Event,
            DiscordCommands.EventOption,
            "mov",
            (suggestions, _) =>
            {
                offered = suggestions;
                return Task.CompletedTask;
            },
            DiscordCommands.EventOpen));

        var one = Assert.Single(offered!);
        Assert.Equal(Movie.ToString(), one.Value);
    }

    [Fact]
    public async Task AFormForAnotherCommand_StillGoesToTheStaffHandler()
    {
        var (services, gateway) = await ConnectedAsync(_db);
        await using var _ = services;

        var answers = new List<DiscordReply>();
        await gateway.RaiseFormAsync(new DiscordFormSubmit(
            Caller,
            "host",
            "modbot:form:act:unknowntoken",
            new Dictionary<string, IReadOnlyList<string>>(),
            (reply, _) =>
            {
                answers.Add(reply);
                return Task.CompletedTask;
            }));

        // The staff handler's own answer for a form it no longer holds.
        Assert.Equal(Modbot.Discord.Interactions.StaffInteractionHandler.RunOutMessage, Assert.Single(answers).Text);
    }
}
