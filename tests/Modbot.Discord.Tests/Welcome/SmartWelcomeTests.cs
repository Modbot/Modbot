using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.Discord.Welcome;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Welcome;

/// <summary>
/// The smart welcome picks up to three steps from a member's roles and sends them once, by direct
/// message, in a channel or both (smart welcome design).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SmartWelcomeTests
{
    private const string Guild = "700";
    private const string Channel = "910";
    private const string Artist = "801";
    private const string Builder = "802";
    private const string Gamer = "803";
    private const string Other = "804";

    private readonly PostgresFixture _db;

    public SmartWelcomeTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(
        PostgresFixture db, bool on = true, string via = DiscordWelcomeWhere.DirectMessage, string? channel = Channel)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.DiscordWelcomeEnabled = on;
            s.DiscordWelcomeVia = via;
            s.DiscordWelcomeChannelId = channel;
            s.DiscordWelcomeStartedAt = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        }, Ct);

        await using var context = services.Database.NewContext();
        context.DiscordWelcomeSteps.AddRange(
            Step(DiscordWelcomeStepKinds.Rule, 0, Artist, "Show your art", "https://example.com/art"),
            Step(DiscordWelcomeStepKinds.Rule, 1, Builder, "Join a build night", null),
            Step(DiscordWelcomeStepKinds.Rule, 2, Gamer, "Find a game", "https://example.com/games"),
            Step(DiscordWelcomeStepKinds.Default, 0, null, "Read the rules", "https://example.com/rules"),
            Step(DiscordWelcomeStepKinds.Default, 1, null, "Say hi", null),
            Step(DiscordWelcomeStepKinds.Default, 2, null, "Link your VRChat account", null));
        await context.SaveChangesAsync(Ct);

        return services;
    }

    private static DiscordWelcomeStep Step(string kind, int position, string? role, string text, string? link)
        => new() { Kind = kind, Position = position, RoleId = role, Text = text, Link = link };

    private static DiscordMemberSnapshot Member(string userId, string[] roles, DateTimeOffset? joinedAt = null)
        => new(userId, "newcomer", "Newcomer", null, false, joinedAt ?? new DateTimeOffset(2026, 9, 13, 11, 0, 0, TimeSpan.Zero), roles, null);

    private static async Task<string?> FinishAsync(TestServices services, FakeGateway gateway, DiscordMemberSnapshot member)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SmartWelcome>().FinishedOnboardingAsync(gateway, Guild, member, Ct);
    }

    private static async Task<string?> JoinAsync(TestServices services, FakeGateway gateway, string userId, bool gateHolds = false, bool isBot = false)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SmartWelcome>()
            .JoinedAsync(gateway, new DiscordMemberJoin(Guild, userId, "newcomer", isBot, Member(userId, [])), gateHolds, Ct);
    }

    [Fact]
    public async Task NoRuleMatching_SendsTheThreeDefaults()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        Assert.Equal("dm", await FinishAsync(services, gateway, Member("2001", [Other])));

        var dm = Assert.Single(gateway.DirectMessages);
        Assert.Contains("1. Read the rules", dm.Text);
        Assert.Contains("2. Say hi", dm.Text);
        Assert.Contains("3. Link your VRChat account", dm.Text);
        Assert.Equal("Step 1", Assert.Single(dm.Links).Label);
    }

    [Fact]
    public async Task OneRuleMatching_IsFirst_AndDefaultsFillTheRest()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        await FinishAsync(services, gateway, Member("2002", [Builder]));

        var dm = Assert.Single(gateway.DirectMessages);
        Assert.Contains("1. Join a build night", dm.Text);
        Assert.Contains("2. Read the rules", dm.Text);
        Assert.Contains("3. Say hi", dm.Text);
        Assert.DoesNotContain("Link your VRChat account", dm.Text);

        // The first step has no link, so the only button is the second step's.
        Assert.Equal("Step 2", Assert.Single(dm.Links).Label);
    }

    [Fact]
    public async Task ThreeOrMoreRulesMatching_SendsTheFirstThreeInOrder_AndNoDefaults()
    {
        await using var services = await SetUpAsync(_db);
        await using (var context = services.Database.NewContext())
        {
            context.DiscordWelcomeSteps.Add(Step(DiscordWelcomeStepKinds.Rule, 3, Other, "A fourth rule", null));
            await context.SaveChangesAsync(Ct);
        }

        var gateway = new FakeGateway();
        await FinishAsync(services, gateway, Member("2003", [Other, Gamer, Builder, Artist]));

        var dm = Assert.Single(gateway.DirectMessages);
        Assert.Contains("1. Show your art", dm.Text);
        Assert.Contains("2. Join a build night", dm.Text);
        Assert.Contains("3. Find a game", dm.Text);
        Assert.DoesNotContain("fourth", dm.Text);
        Assert.DoesNotContain("Read the rules", dm.Text);
        Assert.Equal(["Step 1", "Step 3"], dm.Links.Select(l => l.Label));
    }

    [Fact]
    public async Task ChannelOnly_MentionsTheMember_AndSendsNoDirectMessage()
    {
        await using var services = await SetUpAsync(_db, via: DiscordWelcomeWhere.Channel);
        var gateway = new FakeGateway();

        Assert.Equal("channel", await FinishAsync(services, gateway, Member("2004", [Artist])));

        Assert.Empty(gateway.DirectMessages);
        var mention = Assert.Single(gateway.Mentions);
        Assert.Equal(Channel, mention.ChannelId);
        Assert.Equal("2004", mention.UserId);
    }

    [Fact]
    public async Task Both_SendsTheDirectMessageAndTheChannelPost()
    {
        await using var services = await SetUpAsync(_db, via: DiscordWelcomeWhere.Both);
        var gateway = new FakeGateway();

        Assert.Equal("both", await FinishAsync(services, gateway, Member("2005", [Artist])));

        Assert.Single(gateway.DirectMessages);
        Assert.Single(gateway.Mentions);
    }

    [Fact]
    public async Task WithDirectMessagesClosed_NothingCrashes_AndTheFailureIsRecorded()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        Assert.Equal("none", await FinishAsync(services, gateway, Member("2006", [Artist])));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordWelcomeSent, Ct));
        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal("none", data.GetProperty("via").GetString());
        Assert.Contains("direct messages", data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithDirectMessagesClosed_AndBoth_TheChannelPostStillGoesOut()
    {
        await using var services = await SetUpAsync(_db, via: DiscordWelcomeWhere.Both);
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        Assert.Equal("channel", await FinishAsync(services, gateway, Member("2007", [Artist])));

        Assert.Single(gateway.Mentions);
        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordWelcomeSent, Ct));
        Assert.Equal("channel", JsonDocument.Parse(fact.Data).RootElement.GetProperty("via").GetString());
    }

    [Fact]
    public async Task OffByDefault_NobodyIsSentAnything()
    {
        await using var services = await SetUpAsync(_db, on: false);
        var gateway = new FakeGateway();

        Assert.Null(await FinishAsync(services, gateway, Member("2008", [Artist])));
        Assert.Null(await JoinAsync(services, gateway, "2009"));

        Assert.Empty(gateway.DirectMessages);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordWelcomeSent, Ct));
    }

    [Fact]
    public async Task AMemberIsWelcomedOnce_HoweverManyTimesTheyFinish()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        await FinishAsync(services, gateway, Member("2010", [Artist]));
        Assert.Null(await FinishAsync(services, gateway, Member("2010", [Artist])));
        Assert.Null(await JoinAsync(services, gateway, "2010"));

        Assert.Single(gateway.DirectMessages);
        Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordWelcomeSent, Ct));
    }

    [Fact]
    public async Task AMemberWhoJoinedBeforeItWasSwitchedOn_IsNotWelcomed()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        var early = Member("2011", [Artist], joinedAt: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Null(await FinishAsync(services, gateway, early));
        Assert.Empty(gateway.DirectMessages);
    }

    [Fact]
    public async Task ABot_IsNeverWelcomed()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        Assert.Null(await JoinAsync(services, gateway, "2012", isBot: true));
        Assert.Empty(gateway.DirectMessages);
    }

    [Fact]
    public async Task OnAServerWithNoOnboarding_TheWelcomeGoesOutOnJoining()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { OnboardingOn = false };

        Assert.Equal("dm", await JoinAsync(services, gateway, "2013"));
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task OnAServerWithOnboarding_JoiningSendsNothing()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { OnboardingOn = true };

        Assert.Null(await JoinAsync(services, gateway, "2014"));
        Assert.Empty(gateway.DirectMessages);
    }

    [Fact]
    public async Task WhileTheJoinGateHoldsThem_TheWelcomeWaits_ThenGoesWhenTheyHoldTheMemberRole()
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGateMode = DiscordGateModes.On;
            s.DiscordGateMemberRoleId = "850";
        }, Ct);

        var gateway = new FakeGateway { OnboardingOn = false };

        Assert.Null(await JoinAsync(services, gateway, "2015", gateHolds: true));
        Assert.Empty(gateway.DirectMessages);

        using (var scope = services.Scope())
        {
            var welcome = scope.ServiceProvider.GetRequiredService<SmartWelcome>();

            // Still held: the member role is not theirs yet.
            Assert.Null(await welcome.UpdatedAsync(gateway, Guild, Member("2015", []), Ct));
            Assert.Empty(gateway.DirectMessages);

            Assert.Equal("dm", await welcome.UpdatedAsync(gateway, Guild, Member("2015", ["850"]), Ct));
        }

        Assert.Single(gateway.DirectMessages);

        await using var context = services.Database.NewContext();
        Assert.Equal(DiscordWelcomeStates.Sent, (await context.DiscordWelcomes.SingleAsync(w => w.DiscordUserId == "2015", Ct)).State);
    }

    [Fact]
    public async Task WithNoStepsToSend_NobodyIsClaimed()
    {
        await using var services = await SetUpAsync(_db);
        await using (var context = services.Database.NewContext())
        {
            await context.DiscordWelcomeSteps.Where(s => s.Kind == DiscordWelcomeStepKinds.Default).ExecuteDeleteAsync(Ct);
        }

        var gateway = new FakeGateway();

        Assert.Null(await FinishAsync(services, gateway, Member("2016", [Other])));
        Assert.Empty(gateway.DirectMessages);

        await using var check = services.Database.NewContext();
        Assert.False(await check.DiscordWelcomes.AnyAsync(w => w.DiscordUserId == "2016", Ct));
    }
}
