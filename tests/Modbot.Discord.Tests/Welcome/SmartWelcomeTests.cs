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

    private static DiscordMemberSnapshot Member(
        string userId, string[] roles, DateTimeOffset? joinedAt = null, bool started = false, bool finished = false)
        => new(
            userId, "newcomer", "Newcomer", null, false, joinedAt ?? new DateTimeOffset(2026, 9, 13, 11, 0, 0, TimeSpan.Zero), roles, null,
            StartedOnboarding: started, FinishedOnboarding: finished);

    private static async Task<string?> FinishAsync(TestServices services, FakeGateway gateway, DiscordMemberSnapshot member)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SmartWelcome>().FinishedOnboardingAsync(gateway, Guild, member, Ct);
    }

    private static async Task<string?> JoinAsync(
        TestServices services, FakeGateway gateway, string userId, bool gateHolds = false, bool isBot = false, bool started = false)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SmartWelcome>()
            .JoinedAsync(gateway, new DiscordMemberJoin(Guild, userId, "newcomer", isBot, Member(userId, [], started: started)), gateHolds, Ct);
    }

    private static async Task<string?> UpdateAsync(TestServices services, FakeGateway gateway, DiscordMemberSnapshot member)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SmartWelcome>().UpdatedAsync(gateway, Guild, member, Ct);
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

    [Fact]
    public async Task GateOn_AMemberWhoFinishedOnboardingWhileHeld_IsWelcomedWhenTheGateLetsThemIn_NotBefore()
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGateMode = DiscordGateModes.On;
            s.DiscordGateMemberRoleId = "850";
        }, Ct);

        var gateway = new FakeGateway { OnboardingOn = true };

        // Onboarding on: joining sends nothing, held or not.
        Assert.Null(await JoinAsync(services, gateway, "2017", gateHolds: true));

        // They finish onboarding, but the gate has not let them in: nothing goes out.
        Assert.Null(await FinishAsync(services, gateway, Member("2017", [], started: true, finished: true)));
        Assert.Empty(gateway.DirectMessages);

        await using (var context = services.Database.NewContext())
        {
            Assert.Equal(DiscordWelcomeStates.Waiting, (await context.DiscordWelcomes.SingleAsync(w => w.DiscordUserId == "2017", Ct)).State);
        }

        // The gate gives them the member role: now it goes out, once.
        Assert.Equal("dm", await UpdateAsync(services, gateway, Member("2017", ["850", Artist], started: true, finished: true)));
        Assert.Null(await UpdateAsync(services, gateway, Member("2017", ["850", Artist], started: true, finished: true)));
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task GateOn_AMemberAlreadyLetIn_IsWelcomedAtOnceOnFinishingOnboarding()
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGateMode = DiscordGateModes.On;
            s.DiscordGateMemberRoleId = "850";
        }, Ct);

        var gateway = new FakeGateway { OnboardingOn = true };

        Assert.Equal("dm", await FinishAsync(services, gateway, Member("2018", ["850"], started: true, finished: true)));
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task TheOnboardingReadIsKeptForAnHour_ThenAskedAgain()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { OnboardingOn = false };

        Assert.Equal("dm", await JoinAsync(services, gateway, "2019"));
        Assert.Equal("dm", await JoinAsync(services, gateway, "2020"));
        Assert.Equal("dm", await JoinAsync(services, gateway, "2021"));
        Assert.Equal(1, gateway.OnboardingReads);

        services.Clock.Advance(OnboardingCheck.KeepAnswer - TimeSpan.FromMinutes(1));
        Assert.Equal("dm", await JoinAsync(services, gateway, "2022"));
        Assert.Equal(1, gateway.OnboardingReads);

        services.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("dm", await JoinAsync(services, gateway, "2023"));
        Assert.Equal(2, gateway.OnboardingReads);
    }

    [Fact]
    public async Task AFailedOnboardingRead_IsKeptForFiveMinutes_NotAskedAgainWithEveryJoin()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { OnboardingOn = null };

        // The read failed: no onboarding is assumed, so the welcome goes out on joining.
        Assert.Equal("dm", await JoinAsync(services, gateway, "2024"));
        Assert.Equal("dm", await JoinAsync(services, gateway, "2025"));
        Assert.Equal(1, gateway.OnboardingReads);

        services.Clock.Advance(OnboardingCheck.KeepFailure + TimeSpan.FromSeconds(1));
        Assert.Equal("dm", await JoinAsync(services, gateway, "2026"));
        Assert.Equal(2, gateway.OnboardingReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task TheStartedOnboardingFlag_OverridesAnOffOrFailedRead_AndTheWelcomeWaitsForTheFinish(bool? read)
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { OnboardingOn = read };

        Assert.Null(await JoinAsync(services, gateway, "2027", started: true));
        Assert.Empty(gateway.DirectMessages);

        Assert.Equal("dm", await FinishAsync(services, gateway, Member("2027", [], started: true, finished: true)));
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task AMemberHeldByTheGateWhoStartsOnboarding_IsNotWelcomedOnLetIn_UntilTheyFinish()
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGateMode = DiscordGateModes.On;
            s.DiscordGateMemberRoleId = "850";
        }, Ct);

        // The read said no onboarding, so the welcome is waiting for the gate.
        var gateway = new FakeGateway { OnboardingOn = false };
        Assert.Null(await JoinAsync(services, gateway, "2028", gateHolds: true));

        // The gate lets them in, but Discord says they are partway through onboarding.
        Assert.Null(await UpdateAsync(services, gateway, Member("2028", ["850"], started: true)));
        Assert.Empty(gateway.DirectMessages);

        Assert.Equal("dm", await UpdateAsync(services, gateway, Member("2028", ["850"], started: true, finished: true)));
        Assert.Single(gateway.DirectMessages);
    }

    private static Task SetUpLinkingAsync(TestServices services, bool linkButton = true, bool linkingSetUp = true)
        => services.ConfigureAsync(s =>
        {
            s.DiscordWelcomeLinkButton = linkButton;
            s.PublicAddress = "https://modbot.example.com";
            s.DiscordOAuthClientId = "123456";
            s.DiscordOAuthClientSecretEncrypted = linkingSetUp ? services.Protector.Protect("secret") : null;
        }, Ct);

    [Fact]
    public async Task WithTheLinkSwitchOn_AnUnlinkedMemberGetsALinkYourVRChatButton_AfterTheStepButtons()
    {
        await using var services = await SetUpAsync(_db);
        await SetUpLinkingAsync(services);
        var gateway = new FakeGateway();

        await FinishAsync(services, gateway, Member("2040", [Artist]));

        var dm = Assert.Single(gateway.DirectMessages);
        Assert.Equal(["Step 1", "Link your VRChat"], dm.Links.Select(l => l.Label));
        Assert.Equal("https://modbot.example.com/link", dm.Links[^1].Url);
    }

    [Fact]
    public async Task TheLinkButton_AlsoGoesWithTheChannelPost()
    {
        await using var services = await SetUpAsync(_db, via: DiscordWelcomeWhere.Channel);
        await SetUpLinkingAsync(services);
        var gateway = new FakeGateway();

        await FinishAsync(services, gateway, Member("2041", [Artist]));

        var mention = Assert.Single(gateway.Mentions);
        Assert.Contains(mention.Links, l => l.Label == "Link your VRChat");
    }

    [Fact]
    public async Task TheLinkButton_IsLeftOut_WhenTheSwitchIsOff_LinkingIsNotSetUp_OrTheMemberHasLinked()
    {
        await using (var off = await SetUpAsync(_db))
        {
            await SetUpLinkingAsync(off, linkButton: false);
            var gateway = new FakeGateway();
            await FinishAsync(off, gateway, Member("2042", [Artist]));
            Assert.DoesNotContain(Assert.Single(gateway.DirectMessages).Links, l => l.Label == "Link your VRChat");
        }

        await using (var notSetUp = await SetUpAsync(_db))
        {
            await SetUpLinkingAsync(notSetUp, linkingSetUp: false);
            var gateway = new FakeGateway();
            await FinishAsync(notSetUp, gateway, Member("2043", [Artist]));
            Assert.DoesNotContain(Assert.Single(gateway.DirectMessages).Links, l => l.Label == "Link your VRChat");
        }

        await using var linked = await SetUpAsync(_db);
        await SetUpLinkingAsync(linked);
        await using (var context = linked.Database.NewContext())
        {
            context.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = "2044",
                DiscordUsername = "linked",
                VRChatUserId = "usr_linked",
                LinkedAt = linked.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var linkedGateway = new FakeGateway();
        await FinishAsync(linked, linkedGateway, Member("2044", [Artist]));
        Assert.DoesNotContain(Assert.Single(linkedGateway.DirectMessages).Links, l => l.Label == "Link your VRChat");
    }
}
