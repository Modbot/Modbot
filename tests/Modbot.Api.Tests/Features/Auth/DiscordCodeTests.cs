using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// The account page's <c>/verify</c> code (Discord account linking design §14): one per account,
/// a new one replaces the old, offered only where the bot is set up, and the card says which ways
/// of connecting the server has.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordCodeTests
{
    private readonly PostgresFixture _db;

    public DiscordCodeTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync(bool bot, bool signIn)
    {
        var host = await ApiTestHost.StartAsync(_db, new FakeVRChatGate().SignedInAs());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var settings = await db.GetSettingsAsync(Ct);

        settings.DiscordBotTokenEncrypted = bot ? protector.Protect("a-bot-token") : null;
        settings.DiscordGuildId = bot ? "424242" : null;
        settings.PublicAddress = signIn ? "https://modbot.example.com" : null;
        settings.DiscordOAuthClientId = signIn ? "1111222233334444" : null;
        settings.DiscordOAuthClientSecretEncrypted = signIn ? protector.Protect("the-client-secret") : null;
        await db.SaveChangesAsync(Ct);

        return host;
    }

    private static async Task<int> CodesForAsync(ApiTestHost host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.StaffDiscordCodes.CountAsync(c => c.UserId == userId, Ct);
    }

    [Fact]
    public async Task ANewCode_IsShownWithADash_AndLastsFifteenMinutes()
    {
        await using var host = await StartAsync(bot: true, signIn: false);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/discord/code", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        var code = body.GetProperty("code").GetString()!;
        Assert.Matches("^[" + StaffDiscordCodes.Alphabet + "]{3}-[" + StaffDiscordCodes.Alphabet + "]{3}$", code);
        Assert.Equal(host.Clock.UtcNow + StaffDiscordCodes.Lifetime, body.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.True(body.GetProperty("commandSetUp").GetBoolean());
        Assert.False(body.GetProperty("signInSetUp").GetBoolean());

        // Reading it back shows the same code, so a reload keeps it on the page.
        var again = await ApiTestHost.BodyOf(
            await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, "/api/auth/discord/code", cookie), Ct), Ct);
        Assert.Equal(code, again.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ANewCode_ReplacesTheOldOne()
    {
        await using var host = await StartAsync(bot: true, signIn: true);
        var (me, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, "/api/auth/discord/code", null, cookie, Ct), Ct);
        var second = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, "/api/auth/discord/code", null, cookie, Ct), Ct);

        Assert.NotEqual(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
        Assert.Equal(1, await CodesForAsync(host, me.Id));
    }

    [Fact]
    public async Task NoCode_IsMade_WhereTheBotIsNotSetUp()
    {
        await using var host = await StartAsync(bot: false, signIn: true);
        var (me, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/discord/code", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CodesForAsync(host, me.Id));

        var status = await ApiTestHost.BodyOf(
            await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, "/api/auth/discord/code", cookie), Ct), Ct);
        Assert.False(status.GetProperty("commandSetUp").GetBoolean());
        Assert.True(status.GetProperty("signInSetUp").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, status.GetProperty("code").ValueKind);
    }

    [Fact]
    public async Task AnExpiredCode_IsNotShown()
    {
        await using var host = await StartAsync(bot: true, signIn: false);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        await host.SendJsonAsync(HttpMethod.Post, "/api/auth/discord/code", null, cookie, Ct);

        host.Clock.UtcNow += StaffDiscordCodes.Lifetime;

        var status = await ApiTestHost.BodyOf(
            await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, "/api/auth/discord/code", cookie), Ct), Ct);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, status.GetProperty("code").ValueKind);
    }

    [Fact]
    public async Task TheCode_NeedsASignedInAccount()
    {
        await using var host = await StartAsync(bot: true, signIn: false);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/auth/discord/code", null, null, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.SendJsonAsync(HttpMethod.Get, "/api/auth/discord/code", null, null, Ct)).StatusCode);
    }
}
