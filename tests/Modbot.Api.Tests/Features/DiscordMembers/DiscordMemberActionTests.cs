using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordMembers;

/// <summary>
/// Ban, unban, remove and time out on Discord from the API (API conventions design §8): one
/// permission per action and never the VRChat one, the reason Discord's audit log gets, a fact
/// only when Discord did something, and plain answers when the bot is away or no server is set up.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordMemberActionTests
{
    private const string Guild = "100000000000000001";
    private const string Person = "200000000000000002";

    private readonly PostgresFixture _db;

    public DiscordMemberActionTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records what it was asked and answers as told.</summary>
    private sealed class FakeActions : IDiscordMemberActions
    {
        public List<(string Action, string UserId, string Reason)> Asked { get; } = [];

        public DiscordMemberOutcome Answer { get; set; } = DiscordMemberOutcome.Ok;

        public DiscordOffLimits OffLimits { get; set; } = DiscordOffLimits.None;

        public Task<DiscordOffLimits> OffLimitsAsync(string guildId, CancellationToken ct = default)
            => Task.FromResult(OffLimits);

        private Task<DiscordMemberOutcome> Record(string action, string userId, string reason)
        {
            Asked.Add((action, userId, reason));
            return Task.FromResult(Answer);
        }

        public Task<DiscordMemberOutcome> BanAsync(string guildId, string userId, string reason, int deleteMessageDays, CancellationToken ct = default)
            => Record("ban", userId, reason);

        public Task<DiscordMemberOutcome> UnbanAsync(string guildId, string userId, string reason, CancellationToken ct = default)
            => Record("unban", userId, reason);

        public Task<DiscordMemberOutcome> KickAsync(string guildId, string userId, string reason, CancellationToken ct = default)
            => Record("kick", userId, reason);

        public Task<DiscordMemberOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
            => Record("timeout", userId, reason);
    }

    private async Task<ApiTestHost> StartAsync(IDiscordMemberActions? actions, string? guild = Guild)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var host = await ApiTestHost.StartAsync(_db, configure: services =>
        {
            if (actions is not null)
                services.AddSingleton(actions);
        });

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(Ct);
        settings.DiscordGuildId = guild;
        await db.SaveChangesAsync(Ct);

        return host;
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ApiTestHost host, string type)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).ToListAsync(Ct);
    }

    [Fact]
    public async Task ABan_GoesToDiscordWithWhoAsked_AndIsRecorded()
    {
        var fake = new FakeActions();
        await using var host = await StartAsync(fake);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordBan, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/discord/bans", new { userId = Person, reason = "Spam links" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ApiTestHost.BodyOf(response, Ct)).GetProperty("changed").GetBoolean());

        var (action, userId, reason) = Assert.Single(fake.Asked);
        Assert.Equal("ban", action);
        Assert.Equal(Person, userId);
        Assert.Equal($"Modbot: banned by {user.Username}: Spam links", reason);

        var fact = Assert.Single(await FactsAsync(host, FactType.ActionDiscordBan));
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Equal(Person, fact.SubjectId);
        Assert.Equal(user.Id.ToString(), fact.ActorId);
    }

    [Fact]
    public async Task TheVRChatBan_IsNotPermissionToBanOnDiscord()
    {
        var fake = new FakeActions();
        await using var host = await StartAsync(fake);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.Kick | ModbotPermissions.Unban, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/discord/bans", new { userId = Person }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "DiscordBan",
            (await ApiTestHost.BodyOf(response, Ct)).GetProperty("neededPermissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Empty(fake.Asked);
    }

    [Fact]
    public async Task NothingToDo_IsNotRecorded()
    {
        var fake = new FakeActions { Answer = DiscordMemberOutcome.Already };
        await using var host = await StartAsync(fake);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordUnban, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Delete, $"/api/discord/bans/{Person}", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(response, Ct)).GetProperty("changed").GetBoolean());
        Assert.Empty(await FactsAsync(host, FactType.ActionDiscordUnban));
    }

    [Fact]
    public async Task ARefusal_IsDiscordRefused_AndNotRecorded()
    {
        var fake = new FakeActions { Answer = DiscordMemberOutcome.Failed("The bot may not remove that person.") };
        await using var host = await StartAsync(fake);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordKick, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord/members/{Person}/kick", new { reason = "Raid" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal("discord-refused", body.GetProperty("code").GetString());
        Assert.Equal("The bot may not remove that person.", body.GetProperty("detail").GetString());
        Assert.Empty(await FactsAsync(host, FactType.ActionDiscordKick));
    }

    [Fact]
    public async Task WithNoBot_ItSaysSo()
    {
        // No fake: the host's own stand-in, which is what a deployment without the bot has.
        await using var host = await StartAsync(actions: null);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordTimeOut, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord/members/{Person}/timeout", new { minutes = 10 }, cookie, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unavailable", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ATimeout_HasALengthDiscordAllows()
    {
        var fake = new FakeActions();
        await using var host = await StartAsync(fake);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordTimeOut, Ct);

        foreach (var minutes in new[] { 0, 28 * 24 * 60 + 1 })
        {
            var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord/members/{Person}/timeout", new { minutes }, cookie, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var given = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord/members/{Person}/timeout", new { minutes = 60 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, given.StatusCode);
        Assert.Equal("timeout", Assert.Single(fake.Asked).Action);
        Assert.Single(await FactsAsync(host, FactType.ActionDiscordTimeOut));
    }

    [Fact]
    public async Task WithNoDiscordServer_NothingIsAsked()
    {
        var fake = new FakeActions();
        await using var host = await StartAsync(fake, guild: null);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordKick, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord/members/{Person}/kick", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(fake.Asked);
    }

    [Theory]
    [InlineData("bot")]
    [InlineData("owner")]
    public async Task TheBotItself_AndTheServersOwner_AreNeverActedOn(string who)
    {
        const string Bot = "300000000000000003";
        const string Owner = "400000000000000004";
        var fake = new FakeActions { OffLimits = new DiscordOffLimits(Bot, Owner) };
        await using var host = await StartAsync(fake);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordBan | ModbotPermissions.DiscordKick, Ct);

        // The same number with a leading zero is the same account to Discord, so it is refused too.
        var id = "0" + (who == "bot" ? Bot : Owner);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/discord/bans", new { userId = id }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("refused", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("code").GetString());

        var kick = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord/members/{id}/kick", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, kick.StatusCode);

        Assert.Empty(fake.Asked);
        Assert.Empty(await FactsAsync(host, FactType.ActionDiscordBan));
    }

    [Fact]
    public async Task ADiscordAccountLinkedToAStaffAccount_IsNeverActedOn()
    {
        var fake = new FakeActions();
        await using var host = await StartAsync(fake);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordTimeOut, Ct);
        var (colleague, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var row = await db.Users.SingleAsync(u => u.Id == colleague.Id, Ct);
            row.DiscordUserId = Person;
            await db.SaveChangesAsync(Ct);
        }

        var refused = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/discord/members/{Person}/timeout", new { minutes = 10 }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("refused", (await ApiTestHost.BodyOf(refused, Ct)).GetProperty("code").GetString());
        Assert.Empty(fake.Asked);

        var other = await host.SendJsonAsync(
            HttpMethod.Post, "/api/discord/members/500000000000000005/timeout", new { minutes = 10 }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    /// <summary>
    /// The endpoints now answer from <see cref="Modbot.Api.Features.DiscordMembers.DiscordMemberActionService"/>
    /// (Discord commands design step 3), which the bot's <c>/ban</c> and <c>/kick</c> share. These are
    /// the sentences and codes they have always given, so a script that reads them is not surprised.
    /// </summary>
    [Fact]
    public async Task AfterTheMoveIntoAService_TheEndpointsStillSayWhatTheyAlwaysSaid()
    {
        const string Bot = "300000000000000003";
        const string Owner = "400000000000000004";
        const string Colleague = "500000000000000005";

        var fake = new FakeActions { OffLimits = new DiscordOffLimits(Bot, Owner) };
        await using var host = await StartAsync(fake);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.DiscordBan, Ct);
        var (colleague, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var row = await db.Users.SingleAsync(u => u.Id == colleague.Id, Ct);
            row.DiscordUserId = Colleague;
            await db.SaveChangesAsync(Ct);
        }

        async Task<(HttpStatusCode Status, string Code, string Detail)> BanAsync(string userId)
        {
            var response = await host.SendJsonAsync(HttpMethod.Post, "/api/discord/bans", new { userId }, cookie, Ct);
            var body = await ApiTestHost.BodyOf(response, Ct);
            return (
                response.StatusCode,
                body.GetProperty("code").GetString() ?? string.Empty,
                body.GetProperty("detail").GetString() ?? string.Empty);
        }

        Assert.Equal(
            (HttpStatusCode.Forbidden, "refused", "That is the Discord account Modbot's bot runs as. Modbot will not act on itself."),
            await BanAsync(Bot));
        Assert.Equal(
            (HttpStatusCode.Forbidden, "refused", "That is the owner of the Discord server. Modbot will not act on them."),
            await BanAsync(Owner));
        Assert.Equal(
            (HttpStatusCode.Forbidden, "refused", "That Discord account belongs to a Modbot staff account. Modbot will not act on staff."),
            await BanAsync(Colleague));

        var nobody = await host.SendJsonAsync(HttpMethod.Post, "/api/discord/bans", new { userId = " " }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nobody.StatusCode);
        Assert.Equal("Say who, by their Discord id.", (await ApiTestHost.BodyOf(nobody, Ct)).GetProperty("detail").GetString());

        var tooMany = await host.SendJsonAsync(HttpMethod.Post, "/api/discord/bans", new { userId = Person, deleteMessageDays = 8 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("`deleteMessageDays` is 0 to 7.", (await ApiTestHost.BodyOf(tooMany, Ct)).GetProperty("detail").GetString());

        Assert.Empty(fake.Asked);

        // And a ban that goes through still carries the days and is recorded with them.
        var given = await host.SendJsonAsync(HttpMethod.Post, "/api/discord/bans", new { userId = Person, deleteMessageDays = 3 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, given.StatusCode);

        var fact = Assert.Single(await FactsAsync(host, FactType.ActionDiscordBan));
        Assert.Equal(3, System.Text.Json.JsonDocument.Parse(fact.Data).RootElement.GetProperty("deleteMessageDays").GetInt32());
    }
}
