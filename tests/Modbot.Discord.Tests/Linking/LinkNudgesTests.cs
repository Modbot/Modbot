using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.Linking;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Linking;

/// <summary>
/// The one-time link reminder (account links): staff start it, it sends one direct message per turn
/// to members who have not linked, and nobody is ever sent it twice.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LinkNudgesTests
{
    private const string Guild = "700";

    private readonly PostgresFixture _db;

    public LinkNudgesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, bool running = true, bool linkingSetUp = true)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        var started = services.Clock.UtcNow;
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.PublicAddress = "https://modbot.example.com";
            s.DiscordOAuthClientId = "123456";
            s.DiscordOAuthClientSecretEncrypted = linkingSetUp ? services.Protector.Protect("secret") : null;
            s.DiscordLinkNudgeStartedAt = running ? started : null;
        }, Ct);
        return services;
    }

    private static async Task AddMemberAsync(
        TestServices services, string userId, bool isBot = false, bool left = false, bool joinedAfterStart = false)
    {
        var joined = joinedAfterStart ? services.Clock.UtcNow.AddDays(1) : services.Clock.UtcNow.AddDays(-30);

        await using var db = services.Database.NewContext();
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = userId,
            Username = "user" + userId,
            DisplayName = "User " + userId,
            IsBot = isBot,
            JoinedAt = joined,
            FirstSeenAt = joined,
            LeftAt = left ? services.Clock.UtcNow.AddDays(-1) : null,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<LinkNudgeStep> TurnAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<LinkNudges>().SendNextAsync(gateway, Ct);
    }

    private static async Task<List<DiscordLinkNudge>> RowsAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordLinkNudges.AsNoTracking().OrderBy(n => n.DiscordUserId).ToListAsync(Ct);
    }

    [Fact]
    public async Task EachTurn_SendsOneDirectMessage_WithTheLinkPageButton()
    {
        await using var services = await SetUpAsync(_db);
        await AddMemberAsync(services, "3001");
        await AddMemberAsync(services, "3002");
        var gateway = new FakeGateway();

        Assert.Equal(LinkNudgeStep.Sent, await TurnAsync(services, gateway));

        var dm = Assert.Single(gateway.DirectMessages);
        Assert.Equal("3001", dm.UserId);
        var button = Assert.Single(dm.Links);
        Assert.Equal("https://modbot.example.com/link", button.Url);

        var row = Assert.Single(await RowsAsync(services));
        Assert.Equal(DiscordLinkNudgeStates.Sent, row.State);
        Assert.NotNull(row.SentAt);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordLinkPrompted, Ct));
        Assert.Equal("3001", fact.SubjectId);
        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal("dm", data.GetProperty("via").GetString());
        Assert.True(data.GetProperty("reminder").GetBoolean());

        Assert.Equal(LinkNudgeStep.Sent, await TurnAsync(services, gateway));
        Assert.Equal(["3001", "3002"], gateway.DirectMessages.Select(d => d.UserId));
    }

    [Fact]
    public async Task BotsLeftMembersLinkedMembersAndNewcomers_AreSkipped()
    {
        await using var services = await SetUpAsync(_db);
        await AddMemberAsync(services, "3101", isBot: true);
        await AddMemberAsync(services, "3102", left: true);
        await AddMemberAsync(services, "3103");
        await AddMemberAsync(services, "3104", joinedAfterStart: true);
        await AddMemberAsync(services, "3105");

        await using (var db = services.Database.NewContext())
        {
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = "3103",
                DiscordUsername = "linked",
                VRChatUserId = "usr_linked",
                LinkedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var gateway = new FakeGateway();

        Assert.Equal(LinkNudgeStep.Sent, await TurnAsync(services, gateway));
        Assert.Equal("3105", Assert.Single(gateway.DirectMessages).UserId);

        // Nobody else is waiting, so the job ends and clears its start time.
        Assert.Equal(LinkNudgeStep.Finished, await TurnAsync(services, gateway));
        Assert.Single(gateway.DirectMessages);

        await using var check = services.Database.NewContext();
        Assert.Null((await check.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct)).DiscordLinkNudgeStartedAt);
    }

    [Fact]
    public async Task AMemberWhoUnlinkedLater_IsStillReminded_ButNeverTwice()
    {
        await using var services = await SetUpAsync(_db);
        await AddMemberAsync(services, "3201");

        await using (var db = services.Database.NewContext())
        {
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = "3201",
                DiscordUsername = "old",
                VRChatUserId = "usr_old",
                LinkedAt = services.Clock.UtcNow.AddDays(-10),
                UnlinkedAt = services.Clock.UtcNow.AddDays(-2),
            });
            await db.SaveChangesAsync(Ct);
        }

        var gateway = new FakeGateway();
        Assert.Equal(LinkNudgeStep.Sent, await TurnAsync(services, gateway));
        Assert.Single(gateway.DirectMessages);

        // Staff start it again: the same person is not asked again.
        await services.ConfigureAsync(s => s.DiscordLinkNudgeStartedAt = services.Clock.UtcNow, Ct);
        Assert.Equal(LinkNudgeStep.Finished, await TurnAsync(services, gateway));
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task ClosedDirectMessages_AreMarkedNotReached_AndNotTriedAgain()
    {
        await using var services = await SetUpAsync(_db);
        await AddMemberAsync(services, "3301");
        await AddMemberAsync(services, "3302");
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        Assert.Equal(LinkNudgeStep.NotReached, await TurnAsync(services, gateway));
        gateway.DirectMessagesClosed = false;

        var first = Assert.Single(await RowsAsync(services));
        Assert.Equal("3301", first.DiscordUserId);
        Assert.Equal(DiscordLinkNudgeStates.NotReached, first.State);
        Assert.Null(first.SentAt);
        Assert.False(string.IsNullOrWhiteSpace(first.Error));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordLinkPrompted, Ct));
        Assert.Equal("none", JsonDocument.Parse(fact.Data).RootElement.GetProperty("via").GetString());

        // The next turn goes to the next member, not back to the one who could not be reached.
        Assert.Equal(LinkNudgeStep.Sent, await TurnAsync(services, gateway));
        Assert.Equal("3302", Assert.Single(gateway.DirectMessages).UserId);
        Assert.Equal(LinkNudgeStep.Finished, await TurnAsync(services, gateway));
    }

    [Fact]
    public async Task ADiscordOutage_GivesTheClaimBack_SoTheMemberIsTriedOnTheNextTurn()
    {
        await using var services = await SetUpAsync(_db);
        await AddMemberAsync(services, "3401");
        var gateway = new FakeGateway { NextDirectMessageOutcome = DiscordPostOutcome.Failed("Discord is down.") };

        Assert.Equal(LinkNudgeStep.Retry, await TurnAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Empty(await RowsAsync(services));

        Assert.Equal(LinkNudgeStep.Sent, await TurnAsync(services, gateway));
        Assert.Equal("3401", Assert.Single(gateway.DirectMessages).UserId);
        Assert.Single(await RowsAsync(services));
    }

    [Fact]
    public async Task AnUnclearAnswer_CountsAsSent_SoTheMemberIsNeverAskedTwice()
    {
        await using var services = await SetUpAsync(_db);
        await AddMemberAsync(services, "3501");
        var gateway = new FakeGateway { NextDirectMessageOutcome = DiscordPostOutcome.NoClearAnswer("Timed out.") };

        Assert.Equal(LinkNudgeStep.Sent, await TurnAsync(services, gateway));

        var row = Assert.Single(await RowsAsync(services));
        Assert.Equal(DiscordLinkNudgeStates.Sent, row.State);
        Assert.Equal(LinkNudgeStep.Finished, await TurnAsync(services, gateway));
    }

    [Fact]
    public async Task WhenItIsNotRunning_NothingIsSent()
    {
        await using var services = await SetUpAsync(_db, running: false);
        await AddMemberAsync(services, "3601");
        var gateway = new FakeGateway();

        Assert.Equal(LinkNudgeStep.Off, await TurnAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);
        Assert.Empty(await RowsAsync(services));
    }

    [Fact]
    public async Task WhenLinkingIsTakenApart_TheJobStopsItself_AndSendsNothing()
    {
        await using var services = await SetUpAsync(_db, linkingSetUp: false);
        await AddMemberAsync(services, "3701");
        var gateway = new FakeGateway();

        Assert.Equal(LinkNudgeStep.Finished, await TurnAsync(services, gateway));
        Assert.Empty(gateway.DirectMessages);

        await using var check = services.Database.NewContext();
        Assert.Null((await check.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct)).DiscordLinkNudgeStartedAt);
    }

    [Fact]
    public void TheMessageNamesTheServer_WhenThereIsOne()
    {
        Assert.Equal("Link your VRChat account with the button below.", LinkNudges.MessageText(null));
        Assert.Contains("**Cozy Place**", LinkNudges.MessageText("Cozy Place"));
    }
}
