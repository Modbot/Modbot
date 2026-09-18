using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Auth.VRChatLink;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Security;
using Modbot.TestSupport;
using VRChat.API.Model;

namespace Modbot.Api.Tests.Features.DiscordLink;

/// <summary>
/// The member link page: Sign in with Discord (state and PKCE), name a VRChat account, prove it
/// with the bio code, and unlink (Discord account linking design §2, §3, §7).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordLinkTests
{
    private const string PublicAddress = "https://modbot.example.com";
    private const string ClientId = "1111222233334444";
    private const string ClientSecret = "the-client-secret";

    private readonly PostgresFixture _db;

    public DiscordLinkTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The bio check reads the public profile, not the user object: VRChat stopped returning the
    // bio on GET /users/{userId} (research: vrchat-public-profile-findings.md).
    private static PublicProfile Profile(string id, string displayName, string bio)
        => new() { Id = id, DisplayName = displayName, Bio = bio };

    private sealed record Harness(ApiTestHost Host, FakeVRChatGate Gate, FakeDiscordOAuthHandler Discord) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private async Task<Harness> StartAsync(bool configured = true)
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var discord = new FakeDiscordOAuthHandler { UserId = NewDiscordId() };

        var host = await ApiTestHost.StartAsync(_db, gate, services =>
            services.AddHttpClient(DiscordOAuth.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => discord));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var settings = await db.GetSettingsAsync(Ct);
        settings.PublicAddress = configured ? PublicAddress : null;
        settings.DiscordOAuthClientId = ClientId;
        settings.DiscordOAuthClientSecretEncrypted = protector.Protect(ClientSecret);
        await db.SaveChangesAsync(Ct);

        return new Harness(host, gate, discord);
    }

    private static string NewDiscordId() => RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture) + "77";

    private static HttpRequestMessage Get(string path, params string?[] cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddCookies(request, cookies);
        return request;
    }

    private static void AddCookies(HttpRequestMessage request, string?[] cookies)
    {
        var present = cookies.OfType<string>().ToArray();
        if (present.Length > 0)
            request.Headers.Add("Cookie", string.Join("; ", present));
    }

    private static Task<HttpResponseMessage> PostAsync(ApiTestHost host, string path, object? body, string? cookie)
        => host.SendJsonAsync(HttpMethod.Post, path, body, cookie, Ct);

    /// <summary>The last value set for a cookie, or null when it was deleted or never set.</summary>
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

    /// <summary>Sign in with Discord from start to finish, returning the page's session cookie.</summary>
    private static async Task<string> SignInAsync(Harness h, string? sessionCookie = null)
    {
        var start = await h.Host.Client.SendAsync(Get("/api/discord-link/sign-in"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);

        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var signInCookie = CookieFrom(start, LinkCookies.SignInCookie);
        Assert.NotNull(signInCookie);

        var callback = await h.Host.Client.SendAsync(
            Get($"/api/discord-link/callback?code=the-code&state={Uri.EscapeDataString(query["state"]!)}", signInCookie, sessionCookie),
            Ct);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/link", callback.Headers.Location!.OriginalString);

        return CookieFrom(callback, LinkCookies.SessionCookie)!;
    }

    [Fact]
    public async Task SignIn_SendsTheBrowserToDiscord_WithStateAndAnS256Challenge_AndTheRedirectUrlFromThePublicAddress()
    {
        await using var h = await StartAsync();

        var response = await h.Host.Client.SendAsync(Get("/api/discord-link/sign-in"), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(DiscordOAuth.AuthorizeUrl, location.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal(PublicAddress + "/api/discord-link/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(43, query["state"].ToString().Length);
        Assert.Equal(43, query["code_challenge"].ToString().Length);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(LinkCookies.SignInCookie, StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        // The state and verifier are sealed in the cookie, not readable from it.
        Assert.DoesNotContain(query["state"].ToString(), cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAPublicAddress_SignInGoesBackToThePage()
    {
        await using var h = await StartAsync(configured: false);

        var response = await h.Host.Client.SendAsync(Get("/api/discord-link/sign-in"), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/link?error=not-set-up", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task TheCallback_ExchangesTheCodeWithTheVerifier_AndSignsTheMemberIn()
    {
        await using var h = await StartAsync();

        var start = await h.Host.Client.SendAsync(Get("/api/discord-link/sign-in"), Ct);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var signInCookie = CookieFrom(start, LinkCookies.SignInCookie);

        var callback = await h.Host.Client.SendAsync(
            Get($"/api/discord-link/callback?code=the-code&state={Uri.EscapeDataString(query["state"]!)}", signInCookie),
            Ct);

        Assert.Equal("/link", callback.Headers.Location!.OriginalString);

        var exchange = Assert.Single(h.Discord.Requests, r => r.Path == "/api/v10/oauth2/token");
        Assert.Equal("authorization_code", exchange.Form["grant_type"]);
        Assert.Equal("the-code", exchange.Form["code"]);
        Assert.Equal(ClientId, exchange.Form["client_id"]);
        Assert.Equal(ClientSecret, exchange.Form["client_secret"]);
        Assert.Equal(PublicAddress + "/api/discord-link/callback", exchange.Form["redirect_uri"]);

        // The verifier sent now is the one whose hash went to Discord at the start.
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(exchange.Form["code_verifier"])));
        Assert.Equal(query["code_challenge"].ToString(), challenge);

        Assert.Single(h.Discord.Requests, r => r.Path == "/api/v10/users/@me" && r.Authorization == "Bearer " + h.Discord.AccessToken);
        Assert.Single(h.Discord.Requests, r => r.Path == "/api/v10/oauth2/token/revoke");

        var session = CookieFrom(callback, LinkCookies.SessionCookie);
        var status = await ApiTestHost.BodyOf(await h.Host.Client.SendAsync(Get("/api/discord-link", session), Ct), Ct);
        Assert.Equal(h.Discord.UserId, status.GetProperty("discord").GetProperty("userId").GetString());
        Assert.Equal("member_one", status.GetProperty("discord").GetProperty("username").GetString());
    }

    [Theory]
    [InlineData("wrong-state", true)]
    [InlineData(null, false)]
    public async Task TheCallback_RefusesAWrongStateOrAMissingCookie_WithoutCallingDiscord(string? state, bool sendCookie)
    {
        await using var h = await StartAsync();

        var start = await h.Host.Client.SendAsync(Get("/api/discord-link/sign-in"), Ct);
        var realState = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();
        var signInCookie = sendCookie ? CookieFrom(start, LinkCookies.SignInCookie) : null;

        var callback = await h.Host.Client.SendAsync(
            Get($"/api/discord-link/callback?code=the-code&state={Uri.EscapeDataString(state ?? realState)}", signInCookie),
            Ct);

        Assert.Equal("/link?error=sign-in-expired", callback.Headers.Location!.OriginalString);
        Assert.Empty(h.Discord.Requests);
        Assert.Null(CookieFrom(callback, LinkCookies.SessionCookie));
    }

    [Fact]
    public async Task AnOldSignInCookie_IsRefused()
    {
        await using var h = await StartAsync();

        var start = await h.Host.Client.SendAsync(Get("/api/discord-link/sign-in"), Ct);
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();
        var signInCookie = CookieFrom(start, LinkCookies.SignInCookie);

        h.Host.Clock.Advance(LinkCookies.SignInLifetime + TimeSpan.FromSeconds(1));

        var callback = await h.Host.Client.SendAsync(
            Get($"/api/discord-link/callback?code=the-code&state={Uri.EscapeDataString(state)}", signInCookie), Ct);

        Assert.Equal("/link?error=sign-in-expired", callback.Headers.Location!.OriginalString);
        Assert.Empty(h.Discord.Requests);
    }

    [Fact]
    public async Task ARefusedCodeExchange_GoesBackToThePageWithAnError()
    {
        await using var h = await StartAsync();
        h.Discord.TokenStatus = HttpStatusCode.BadRequest;

        var start = await h.Host.Client.SendAsync(Get("/api/discord-link/sign-in"), Ct);
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();

        var callback = await h.Host.Client.SendAsync(
            Get($"/api/discord-link/callback?code=bad&state={Uri.EscapeDataString(state)}", CookieFrom(start, LinkCookies.SignInCookie)), Ct);

        Assert.Equal("/link?error=discord", callback.Headers.Location!.OriginalString);
        Assert.Null(CookieFrom(callback, LinkCookies.SessionCookie));
    }

    [Fact]
    public async Task StartingFromDiscord_TheWholeFlowLinks_AndRecordsIt()
    {
        await using var h = await StartAsync();
        var session = await SignInAsync(h);
        var vrchatId = $"usr_{Guid.NewGuid():N}";

        var named = await PostAsync(h.Host, "/api/discord-link/vrchat", new { userIdOrUrl = $"https://vrchat.com/home/user/{vrchatId}" }, session);
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        var pending = (await ApiTestHost.BodyOf(named, Ct)).GetProperty("pending");
        Assert.Equal(vrchatId, pending.GetProperty("vrChatUserId").GetString());
        var code = pending.GetProperty("code").GetString()!;

        h.Gate.Returns("GetPublicProfile", Profile(vrchatId, "LinkPageTester", "hello"));
        var notYet = await ApiTestHost.BodyOf(await PostAsync(h.Host, "/api/discord-link/check", null, session), Ct);
        Assert.False(notYet.GetProperty("linked").GetBoolean());
        Assert.Contains(code, notYet.GetProperty("message").GetString(), StringComparison.Ordinal);

        var call = Assert.Single(h.Gate.Calls);
        Assert.Equal(Modbot.VRChat.VRChatEndpointClass.UsersLookup, call.Endpoint.Class);
        Assert.Equal(Modbot.VRChat.VRChatCallPriority.Interactive, call.Priority);

        h.Host.Clock.Advance(VRChatLinkEndpoints.MinimumGapBetweenChecks);
        h.Gate.Returns("GetPublicProfile", new PublicProfile { Id = vrchatId, DisplayName = "LinkPageTester", Bio = $"hi {code}", AgeVerificationStatus = AgeVerificationStatus.plus18 });

        var linked = await ApiTestHost.BodyOf(await PostAsync(h.Host, "/api/discord-link/check", null, session), Ct);
        Assert.True(linked.GetProperty("linked").GetBoolean());
        var link = linked.GetProperty("status").GetProperty("link");
        Assert.Equal(vrchatId, link.GetProperty("vrChatUserId").GetString());
        Assert.Equal("LinkPageTester", link.GetProperty("vrChatDisplayName").GetString());

        using var scope = h.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var row = await db.DiscordAccountLinks.AsNoTracking().SingleAsync(l => l.VRChatUserId == vrchatId, Ct);
        Assert.Equal(h.Discord.UserId, row.DiscordUserId);
        Assert.Equal(LinkStartedFrom.Discord, row.StartedFrom);
        Assert.False(await db.DiscordLinkCodes.AnyAsync(c => c.DiscordUserId == h.Discord.UserId, Ct));

        // The fetched profile set the sticky 18+ flag the role job reads.
        Assert.True((await db.VRChatUsers.AsNoTracking().SingleAsync(u => u.UserId == vrchatId, Ct)).Is18PlusVerified);

        var fact = Assert.Single(await h.Host.FactsAsync(FactType.DiscordLinkCreated, vrchatId, Ct));
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
        Assert.Equal(FactPlatform.Discord, fact.ActorPlatform);
        Assert.Equal(h.Discord.UserId, fact.ActorId);
        Assert.Equal("discord", ApiTestHost.DataOf(fact).GetProperty("startedFrom").GetString());
    }

    [Fact]
    public async Task StartingFromVRChat_TheCodeWaitsForDiscordSignIn_ThenLinks()
    {
        await using var h = await StartAsync();
        var vrchatId = $"usr_{Guid.NewGuid():N}";

        var named = await PostAsync(h.Host, "/api/discord-link/vrchat", new { userIdOrUrl = vrchatId }, null);
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        var before = CookieFrom(named, LinkCookies.SessionCookie)!;
        var code = (await ApiTestHost.BodyOf(named, Ct)).GetProperty("pending").GetProperty("code").GetString()!;

        // Checking costs a VRChat request, so it waits for a Discord account to count it against.
        var tooEarly = await PostAsync(h.Host, "/api/discord-link/check", null, before);
        Assert.Equal(HttpStatusCode.Unauthorized, tooEarly.StatusCode);
        Assert.Empty(h.Gate.Calls);

        var session = await SignInAsync(h, before);

        var status = await ApiTestHost.BodyOf(await h.Host.Client.SendAsync(Get("/api/discord-link", session), Ct), Ct);
        Assert.Equal(code, status.GetProperty("pending").GetProperty("code").GetString());

        h.Gate.Returns("GetPublicProfile", Profile(vrchatId, "FromVRChat", code));
        var linked = await ApiTestHost.BodyOf(await PostAsync(h.Host, "/api/discord-link/check", null, session), Ct);
        Assert.True(linked.GetProperty("linked").GetBoolean());

        using var scope = h.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var row = await db.DiscordAccountLinks.AsNoTracking().SingleAsync(l => l.VRChatUserId == vrchatId, Ct);
        Assert.Equal(LinkStartedFrom.VRChat, row.StartedFrom);
    }

    [Fact]
    public async Task AVRChatAccountLinkedToAnotherDiscordAccount_IsRefused()
    {
        await using var h = await StartAsync();
        var vrchatId = $"usr_{Guid.NewGuid():N}";

        using (var scope = h.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = NewDiscordId(),
                DiscordUsername = "someone_else",
                VRChatUserId = vrchatId,
                LinkedAt = h.Host.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var session = await SignInAsync(h);
        var named = await ApiTestHost.BodyOf(await PostAsync(h.Host, "/api/discord-link/vrchat", new { userIdOrUrl = vrchatId }, session), Ct);
        var code = named.GetProperty("pending").GetProperty("code").GetString()!;

        h.Gate.Returns("GetPublicProfile", Profile(vrchatId, "Taken", code));
        var result = await ApiTestHost.BodyOf(await PostAsync(h.Host, "/api/discord-link/check", null, session), Ct);

        Assert.False(result.GetProperty("linked").GetBoolean());
        Assert.Equal(DiscordAccountLinks.AlreadyLinkedElsewhere, result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ChecksAreLimited_PerCode_AndSpacedOut()
    {
        await using var h = await StartAsync();
        var session = await SignInAsync(h);
        await PostAsync(h.Host, "/api/discord-link/vrchat", new { userIdOrUrl = "usr_limits" }, session);
        h.Gate.Returns("GetPublicProfile", Profile("usr_limits", "Limits", "nothing"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(h.Host, "/api/discord-link/check", null, session)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(h.Host, "/api/discord-link/check", null, session)).StatusCode);

        // Naming the account again is not a way round the gap.
        await PostAsync(h.Host, "/api/discord-link/vrchat", new { userIdOrUrl = "usr_limits" }, session);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(h.Host, "/api/discord-link/check", null, session)).StatusCode);

        for (var i = 0; i < VRChatLinkEndpoints.MaxChecksPerCode; i++)
        {
            h.Host.Clock.Advance(VRChatLinkEndpoints.MinimumGapBetweenChecks);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(h.Host, "/api/discord-link/check", null, session)).StatusCode);
        }

        h.Host.Clock.Advance(VRChatLinkEndpoints.MinimumGapBetweenChecks);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(h.Host, "/api/discord-link/check", null, session)).StatusCode);
        Assert.Equal(VRChatLinkEndpoints.MaxChecksPerCode + 1, h.Gate.Calls.Count);
    }

    [Fact]
    public async Task TheMember_CanUnlink_AndTheRowAndHistoryStay()
    {
        await using var h = await StartAsync();
        var session = await SignInAsync(h);
        var vrchatId = $"usr_{Guid.NewGuid():N}";
        var code = (await ApiTestHost.BodyOf(await PostAsync(h.Host, "/api/discord-link/vrchat", new { userIdOrUrl = vrchatId }, session), Ct))
            .GetProperty("pending").GetProperty("code").GetString()!;
        h.Gate.Returns("GetPublicProfile", Profile(vrchatId, "Leaving", code));
        await PostAsync(h.Host, "/api/discord-link/check", null, session);

        using (var linkedScope = h.Host.Services.CreateScope())
        {
            var linkedDb = linkedScope.ServiceProvider.GetRequiredService<ModbotContext>();
            Assert.Equal(h.Discord.UserId, await linkedDb.LinkedDiscordUserIdAsync(vrchatId, Ct));
            Assert.Equal(vrchatId, await linkedDb.LinkedVRChatUserIdAsync(h.Discord.UserId, Ct));
        }

        var unlinked = await ApiTestHost.BodyOf(await PostAsync(h.Host, "/api/discord-link/unlink", null, session), Ct);
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("link").ValueKind);

        using var scope = h.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var row = await db.DiscordAccountLinks.AsNoTracking().SingleAsync(l => l.VRChatUserId == vrchatId, Ct);
        Assert.NotNull(row.UnlinkedAt);
        Assert.Equal(LinkEndedBy.Member, row.UnlinkedBy);

        // Two separate people again, each with their own history.
        Assert.Null(await db.LinkedDiscordUserIdAsync(vrchatId, Ct));
        Assert.Null(await db.LinkedVRChatUserIdAsync(h.Discord.UserId, Ct));

        Assert.Single(await h.Host.FactsAsync(FactType.DiscordLinkCreated, vrchatId, Ct));
        var removed = Assert.Single(await h.Host.FactsAsync(FactType.DiscordLinkRemoved, vrchatId, Ct));
        Assert.Equal("member", ApiTestHost.DataOf(removed).GetProperty("by").GetString());
    }

    [Fact]
    public async Task UnlinkAndCheck_NeedDiscordSignIn()
    {
        await using var h = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(h.Host, "/api/discord-link/unlink", null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(h.Host, "/api/discord-link/check", null, null)).StatusCode);

        // A cookie that has been tampered with is no cookie.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostAsync(h.Host, "/api/discord-link/check", null, LinkCookies.SessionCookie + "=not-a-real-value")).StatusCode);
    }

    [Fact]
    public async Task Moderators_SeeTheLinkWithViewProfile_AndUnlinkWithManageDiscordLinks()
    {
        await using var h = await StartAsync();
        var vrchatId = $"usr_{Guid.NewGuid():N}";
        var discordId = NewDiscordId();
        Guid linkId;

        using (var scope = h.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var link = new DiscordAccountLink
            {
                DiscordUserId = discordId,
                DiscordUsername = "popup_person",
                VRChatUserId = vrchatId,
                LinkedAt = h.Host.Clock.UtcNow,
                LinkedRoleId = "801",
            };
            db.DiscordAccountLinks.Add(link);
            await db.SaveChangesAsync(Ct);
            linkId = link.Id;
        }

        var (_, nobody) = await h.Host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (_, viewer) = await h.Host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var (moderator, manager) = await h.Host.SignedInAsync(ModbotPermissions.ManageDiscordLinks, Ct);

        var path = $"/api/discord-links?vrchatUserId={vrchatId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Host.SendJsonAsync(HttpMethod.Get, path, null, nobody, Ct)).StatusCode);

        var seen = await ApiTestHost.BodyOf(await h.Host.SendJsonAsync(HttpMethod.Get, path, null, viewer, Ct), Ct);
        Assert.Equal(discordId, seen.GetProperty("link").GetProperty("discordUserId").GetString());
        Assert.Equal("801", seen.GetProperty("link").GetProperty("roles")[0].GetProperty("id").GetString());

        var unlinkPath = $"/api/discord-links/{linkId}/unlink";
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Host.SendJsonAsync(HttpMethod.Post, unlinkPath, null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Host.SendJsonAsync(HttpMethod.Post, unlinkPath, null, manager, Ct)).StatusCode);

        var fact = Assert.Single(await h.Host.FactsAsync(FactType.DiscordLinkRemoved, vrchatId, Ct));
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(moderator.Id.ToString(), fact.ActorId);

        using var check = h.Host.Services.CreateScope();
        var row = await check.ServiceProvider.GetRequiredService<ModbotContext>().DiscordAccountLinks.AsNoTracking().SingleAsync(l => l.Id == linkId, Ct);
        Assert.Equal(LinkEndedBy.Moderator, row.UnlinkedBy);

        // Ending a link wakes the role job, which takes the role back.
        Assert.True(await h.Host.Services.GetRequiredService<DiscordLinkSignal>().WaitAsync(TimeSpan.Zero, Ct));
    }
}
