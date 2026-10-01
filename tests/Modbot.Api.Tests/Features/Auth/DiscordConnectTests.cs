using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// Connect Discord on the account page (accounts and access design §4.6): the person signs in to
/// Discord, the id Discord gives back goes on their account as proven, and nobody else can put one
/// there.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordConnectTests
{
    private const string PublicAddress = "https://modbot.example.com";
    private const string ClientId = "1111222233334444";
    private const string ClientSecret = "the-client-secret";

    private readonly PostgresFixture _db;

    public DiscordConnectTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Harness(ApiTestHost Host, FakeDiscordOAuthHandler Discord) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private async Task<Harness> StartAsync(bool configured = true)
    {
        var discord = new FakeDiscordOAuthHandler { UserId = NewDiscordId(), Username = "staff_one" };

        var host = await ApiTestHost.StartAsync(_db, new FakeVRChatGate().SignedInAs(), services =>
            services.AddHttpClient(DiscordOAuth.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => discord));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var settings = await db.GetSettingsAsync(Ct);
        settings.PublicAddress = configured ? PublicAddress : null;
        settings.DiscordOAuthClientId = ClientId;
        settings.DiscordOAuthClientSecretEncrypted = protector.Protect(ClientSecret);
        await db.SaveChangesAsync(Ct);

        return new Harness(host, discord);
    }

    private static string NewDiscordId() => RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture) + "37";

    private static HttpRequestMessage Get(string path, params string?[] cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        var present = cookies.OfType<string>().ToArray();
        if (present.Length > 0)
            request.Headers.Add("Cookie", string.Join("; ", present));
        return request;
    }

    private static string? CookieFrom(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return null;

        var last = values.LastOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal));
        if (last is null)
            return null;

        var pair = last.Split(';')[0];
        return pair.Length == name.Length + 1 ? null : pair;
    }

    /// <summary>Connect Discord from the account page to Discord's redirect back. Returns the callback's response.</summary>
    private static async Task<HttpResponseMessage> ConnectAsync(Harness h, string startedWith, string? cameBackWith)
    {
        var start = await h.Host.Client.SendAsync(Get("/api/auth/discord/connect", startedWith), Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);

        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var signInCookie = CookieFrom(start, LinkCookies.SignInCookie);
        Assert.NotNull(signInCookie);

        return await h.Host.Client.SendAsync(
            Get($"/api/discord-link/callback?code=the-code&state={Uri.EscapeDataString(query["state"]!)}", signInCookie, cameBackWith),
            Ct);
    }

    private static async Task<ModbotUser> ReloadAsync(ApiTestHost host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, Ct);
    }

    private static async Task SetDiscordAsync(ApiTestHost host, Guid id, string discordUserId, bool proven)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var user = await db.Users.SingleAsync(u => u.Id == id, Ct);
        user.DiscordUserId = discordUserId;
        user.DiscordUsername = proven ? "earlier" : null;
        user.DiscordVerifiedAt = proven ? host.Clock.UtcNow : null;
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Connect_SendsTheBrowserToDiscord_ThroughTheSameCallbackAsTheLinkPage()
    {
        await using var h = await StartAsync();
        var (_, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await h.Host.Client.SendAsync(Get("/api/auth/discord/connect", cookie), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(PublicAddress + "/api/discord-link/callback", query["redirect_uri"]);

        var signIn = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(LinkCookies.SignInCookie, StringComparison.Ordinal));
        Assert.Contains("path=/api/discord-link", signIn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connect_NeedsASignedInAccount()
    {
        await using var h = await StartAsync();

        var response = await h.Host.Client.SendAsync(Get("/api/auth/discord/connect"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_WhenSignInIsNotSetUp_GoesBackToTheAccountPage()
    {
        await using var h = await StartAsync(configured: false);
        var (_, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await h.Host.Client.SendAsync(Get("/api/auth/discord/connect", cookie), Ct);

        Assert.Equal("/account?discord=not-set-up", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task TheCallback_StoresTheProvenId_AndRecordsIt()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var callback = await ConnectAsync(h, cookie, cookie);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/account?discord=connected", callback.Headers.Location!.OriginalString);

        // The link page's own session is not touched: this was not a member signing in there.
        Assert.Null(CookieFrom(callback, LinkCookies.SessionCookie));

        // Discord was asked once who signed in, and the token was given back.
        Assert.Single(h.Discord.Requests, r => r.Path == "/api/v10/users/@me");
        Assert.Single(h.Discord.Requests, r => r.Path == "/api/v10/oauth2/token/revoke");

        var stored = await ReloadAsync(h.Host, me.Id);
        Assert.Equal(h.Discord.UserId, stored.DiscordUserId);
        Assert.Equal("staff_one", stored.DiscordUsername);
        Assert.Equal(h.Host.Clock.UtcNow, stored.DiscordVerifiedAt);

        var fact = Assert.Single(await h.Host.FactsAsync(FactType.DiscordConnected, me.Id.ToString(), Ct));
        var data = ApiTestHost.DataOf(fact);
        Assert.Equal(h.Discord.UserId, data.GetProperty("discordUserId").GetString());
        Assert.Equal("staff_one", data.GetProperty("discordUsername").GetString());
        Assert.Equal(me.Id.ToString(), fact.ActorId);

        var session = await ApiTestHost.BodyOf(await h.Host.Client.SendAsync(Get("/api/auth/me", cookie), Ct), Ct);
        Assert.True(session.GetProperty("discordProven").GetBoolean());
        Assert.Equal("staff_one", session.GetProperty("discordUsername").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("discordWorksUntil").ValueKind);
    }

    [Fact]
    public async Task TheCallback_RefusesABrowserNoLongerSignedIn_AndStoresNothing()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var callback = await ConnectAsync(h, cookie, cameBackWith: null);

        Assert.Equal("/account?discord=signed-out", callback.Headers.Location!.OriginalString);
        Assert.Empty(h.Discord.Requests);
        Assert.Null((await ReloadAsync(h.Host, me.Id)).DiscordUserId);
        Assert.Empty(await h.Host.FactsAsync(FactType.DiscordConnected, me.Id.ToString(), Ct));
    }

    [Fact]
    public async Task TheCallback_RefusesABrowserSignedInAsSomebodyElse()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (other, otherCookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var callback = await ConnectAsync(h, cookie, otherCookie);

        Assert.Equal("/account?discord=signed-out", callback.Headers.Location!.OriginalString);
        Assert.Null((await ReloadAsync(h.Host, me.Id)).DiscordUserId);
        Assert.Null((await ReloadAsync(h.Host, other.Id)).DiscordUserId);
    }

    [Fact]
    public async Task ADiscordAccountAnotherAccountProved_IsRefused()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (other, _) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        await SetDiscordAsync(h.Host, other.Id, h.Discord.UserId, proven: true);

        var callback = await ConnectAsync(h, cookie, cookie);

        Assert.Equal("/account?discord=taken", callback.Headers.Location!.OriginalString);
        Assert.Null((await ReloadAsync(h.Host, me.Id)).DiscordUserId);
        Assert.Equal(h.Discord.UserId, (await ReloadAsync(h.Host, other.Id)).DiscordUserId);
    }

    [Fact]
    public async Task ADiscordAccountAnotherAccountOnlyTypedIn_ComesOffIt()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (other, _) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        await SetDiscordAsync(h.Host, other.Id, h.Discord.UserId, proven: false);

        var callback = await ConnectAsync(h, cookie, cookie);

        Assert.Equal("/account?discord=connected", callback.Headers.Location!.OriginalString);
        Assert.Equal(h.Discord.UserId, (await ReloadAsync(h.Host, me.Id)).DiscordUserId);
        Assert.Null((await ReloadAsync(h.Host, other.Id)).DiscordUserId);

        var fact = Assert.Single(await h.Host.FactsAsync(FactType.DiscordDisconnected, other.Id.ToString(), Ct));
        Assert.Equal("proven-by-another-account", ApiTestHost.DataOf(fact).GetProperty("why").GetString());
        Assert.Equal(me.Id.ToString(), fact.ActorId);
    }

    [Fact]
    public async Task ATypedIdOnYourOwnAccount_IsReplacedByTheProvenOne()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        await SetDiscordAsync(h.Host, me.Id, "999999999999999999", proven: false);

        var before = await ApiTestHost.BodyOf(await h.Host.Client.SendAsync(Get("/api/auth/me", cookie), Ct), Ct);
        Assert.False(before.GetProperty("discordProven").GetBoolean());
        Assert.Equal(JsonValueKind.String, before.GetProperty("discordWorksUntil").ValueKind);

        await ConnectAsync(h, cookie, cookie);

        var stored = await ReloadAsync(h.Host, me.Id);
        Assert.Equal(h.Discord.UserId, stored.DiscordUserId);
        Assert.NotNull(stored.DiscordVerifiedAt);

        var fact = Assert.Single(await h.Host.FactsAsync(FactType.DiscordConnected, me.Id.ToString(), Ct));
        Assert.Equal("999999999999999999", ApiTestHost.DataOf(fact).GetProperty("replaced").GetString());
    }

    [Fact]
    public async Task CancellingAtDiscord_GoesBackToTheAccountPage()
    {
        await using var h = await StartAsync();
        var (_, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var start = await h.Host.Client.SendAsync(Get("/api/auth/discord/connect", cookie), Ct);
        var signInCookie = CookieFrom(start, LinkCookies.SignInCookie);

        var callback = await h.Host.Client.SendAsync(
            Get("/api/discord-link/callback?error=access_denied", signInCookie, cookie), Ct);

        Assert.Equal("/account?discord=cancelled", callback.Headers.Location!.OriginalString);
        Assert.Empty(h.Discord.Requests);
    }

    [Fact]
    public async Task Disconnect_TakesItOff_AndRecordsIt()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        await ConnectAsync(h, cookie, cookie);

        var response = await h.Host.SendJsonAsync(HttpMethod.Delete, "/api/auth/discord", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("discordUserId").ValueKind);
        Assert.False(body.GetProperty("discordProven").GetBoolean());

        var stored = await ReloadAsync(h.Host, me.Id);
        Assert.Null(stored.DiscordUserId);
        Assert.Null(stored.DiscordUsername);
        Assert.Null(stored.DiscordVerifiedAt);

        var fact = Assert.Single(await h.Host.FactsAsync(FactType.DiscordDisconnected, me.Id.ToString(), Ct));
        Assert.Equal("removed", ApiTestHost.DataOf(fact).GetProperty("why").GetString());
    }

    [Fact]
    public async Task NobodyCanTypeADiscordId_IntoTheirOwnAccount()
    {
        await using var h = await StartAsync();
        var (me, cookie) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await h.Host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/contact", new { discordUserId = "123456789012345678" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await ReloadAsync(h.Host, me.Id)).DiscordUserId);
    }

    [Fact]
    public async Task AnAdministrator_CannotSetAnotherPersonsDiscordId()
    {
        await using var h = await StartAsync();
        var (_, admin) = await h.Host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, _) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var contact = await h.Host.SendJsonAsync(
            HttpMethod.Put, $"/api/users/{user.Id}/contact", new { discordUserId = "123456789012345678" }, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, contact.StatusCode);
        Assert.Null((await ReloadAsync(h.Host, user.Id)).DiscordUserId);

        var name = $"u_{Guid.NewGuid():N}";
        var created = await h.Host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new
            {
                username = name,
                password = "a-long-enough-password",
                confirmPassword = "a-long-enough-password",
                roleIds = Array.Empty<Guid>(),
                email = $"{name}@example.com",
                discordUserId = "123456789012345678",
            },
            admin,
            Ct);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = Guid.Parse((await ApiTestHost.BodyOf(created, Ct)).GetProperty("id").GetString()!);
        Assert.Null((await ReloadAsync(h.Host, id)).DiscordUserId);
    }
}
