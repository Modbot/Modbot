using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Linking;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Linking;

/// <summary>
/// A new member is asked to link by DM, or in the backup channel when their DMs are closed
/// (Discord account linking design §8); and <c>/link</c> answers anyone with the page.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LinkPromptTests
{
    private const string Guild = "700";
    private const string Backup = "900";

    private readonly PostgresFixture _db;

    public LinkPromptTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(PostgresFixture db, bool promptOn = true, string? backup = Backup)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.PublicAddress = "https://modbot.example.com";
            s.DiscordOAuthClientId = "123456";
            s.DiscordOAuthClientSecretEncrypted = services.Protector.Protect("secret");
            s.DiscordLinkPromptNewMembers = promptOn;
            s.DiscordLinkBackupChannelId = backup;
        }, Ct);
        return services;
    }

    private static async Task<string?> JoinAsync(TestServices services, FakeGateway gateway, string userId, bool isBot = false)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<LinkPrompt>()
            .HandleAsync(gateway, new DiscordMemberJoin(Guild, userId, "newcomer", isBot), Ct);
    }

    [Fact]
    public async Task ANewMember_IsSentADirectMessageWithTheLinkPage()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        var via = await JoinAsync(services, gateway, "1001");

        Assert.Equal(LinkPromptVia.DirectMessage, via);
        var dm = Assert.Single(gateway.DirectMessages);
        Assert.Equal("1001", dm.UserId);
        var button = Assert.Single(dm.Links);
        Assert.Equal("https://modbot.example.com/link", button.Url);
        Assert.Empty(gateway.Mentions);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordLinkPrompted, Ct));
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Equal("1001", fact.SubjectId);
        Assert.Equal("dm", JsonDocument.Parse(fact.Data).RootElement.GetProperty("via").GetString());
    }

    [Fact]
    public async Task WithDirectMessagesClosed_TheMemberIsMentionedInTheBackupChannel()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        var via = await JoinAsync(services, gateway, "1002");

        Assert.Equal(LinkPromptVia.BackupChannel, via);
        Assert.Empty(gateway.DirectMessages);
        var mention = Assert.Single(gateway.Mentions);
        Assert.Equal(Backup, mention.ChannelId);
        Assert.Equal("1002", mention.UserId);
        Assert.Equal("https://modbot.example.com/link", Assert.Single(mention.Links).Url);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordLinkPrompted, Ct));
        Assert.Equal("channel", JsonDocument.Parse(fact.Data).RootElement.GetProperty("via").GetString());
    }

    [Fact]
    public async Task WithDirectMessagesClosed_AndNoBackupChannel_NothingIsPosted()
    {
        await using var services = await SetUpAsync(_db, backup: null);
        var gateway = new FakeGateway { DirectMessagesClosed = true };

        Assert.Equal(LinkPromptVia.None, await JoinAsync(services, gateway, "1003"));
        Assert.Empty(gateway.Mentions);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordLinkPrompted, Ct));
        Assert.Equal("none", JsonDocument.Parse(fact.Data).RootElement.GetProperty("via").GetString());
    }

    [Fact]
    public async Task WithTheSwitchOff_OrForABot_NobodyIsAsked()
    {
        await using (var off = await SetUpAsync(_db, promptOn: false))
        {
            var gateway = new FakeGateway();
            Assert.Null(await JoinAsync(off, gateway, "1004"));
            Assert.Empty(gateway.DirectMessages);
        }

        await using var on = await SetUpAsync(_db);
        var botGateway = new FakeGateway();
        Assert.Null(await JoinAsync(on, botGateway, "1005", isBot: true));
        Assert.Empty(botGateway.DirectMessages);
    }

    [Fact]
    public async Task AnAlreadyLinkedMember_IsNotAsked_AndTheirRolesAreGivenAgain()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        Guid id;
        await using (var db = services.Database.NewContext())
        {
            var link = new DiscordAccountLink
            {
                DiscordUserId = "1006",
                DiscordUsername = "back",
                VRChatUserId = "usr_back",
                LinkedAt = services.Clock.UtcNow,
                LinkedRoleId = "801",
                NotInServerAt = services.Clock.UtcNow,
            };
            db.DiscordAccountLinks.Add(link);
            await db.SaveChangesAsync(Ct);
            id = link.Id;
        }

        Assert.Null(await JoinAsync(services, gateway, "1006"));
        Assert.Empty(gateway.DirectMessages);

        await using var check = services.Database.NewContext();
        var row = await check.DiscordAccountLinks.AsNoTracking().SingleAsync(l => l.Id == id, Ct);
        Assert.Null(row.LinkedRoleId);
        Assert.Null(row.NotInServerAt);
    }

    [Fact]
    public async Task SlashLink_AnswersAnyone_WithAButtonToTheLinkPage()
    {
        await using var services = await SetUpAsync(_db);

        using var scope = services.Scope();
        var handler = scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>();
        var reply = await handler.HandleAsync(
            new DiscordCommandCall("5555", "nobody", DiscordCommands.Link, new Dictionary<string, string>(), (_, _) => Task.CompletedTask),
            Ct);

        var button = Assert.Single(reply.Links!);
        Assert.Equal("https://modbot.example.com/link", button.Url);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Equal("answered", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task SlashLink_SaysSoWhenLinkingIsNotSetUp()
    {
        await using var services = await SetUpAsync(_db);
        await services.ConfigureAsync(s => s.DiscordOAuthClientSecretEncrypted = null, Ct);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(
            new DiscordCommandCall("5556", "nobody", DiscordCommands.Link, new Dictionary<string, string>(), (_, _) => Task.CompletedTask),
            Ct);

        Assert.Null(reply.Links);
        Assert.Equal("Account linking is not set up on this server.", reply.Text);
    }
}
