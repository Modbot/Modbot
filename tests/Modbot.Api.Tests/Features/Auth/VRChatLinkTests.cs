using Modbot.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Net;
using Modbot.Api.Features.Auth.VRChatLink;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using VRChat.API.Model;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// Linking a VRChat account by putting a code in its bio (accounts and access design §4.3).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VRChatLinkTests
{
    private readonly PostgresFixture _db;

    public VRChatLinkTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The bio check reads the public profile, not the user object: VRChat stopped returning the
    // bio on GET /users/{userId} (research: vrchat-public-profile-findings.md).
    private static PublicProfile Profile(string id, string displayName, string bio)
        => new() { Id = id, DisplayName = displayName, Bio = bio };

    private static async Task<System.Text.Json.JsonElement> StartAsync(ApiTestHost host, string cookie, string input)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/start", new { userIdOrUrl = input }, cookie, Ct);
        response.EnsureSuccessStatusCode();
        return await ApiTestHost.BodyOf(response, Ct);
    }

    [Theory]
    [InlineData("usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469", "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469")]
    [InlineData("  usr_abc  ", "usr_abc")]
    [InlineData("https://vrchat.com/home/user/usr_abc", "usr_abc")]
    [InlineData("https://vrchat.com/home/user/usr_abc/", "usr_abc")]
    [InlineData("https://vrchat.com/home/user/usr_abc?tab=info#top", "usr_abc")]
    [InlineData("vrchat.com/home/user/8JoV9XEdpo", "8JoV9XEdpo")]
    [InlineData("SomeLegacyIdWithNoStructure", "SomeLegacyIdWithNoStructure")]
    public void TheIdComesOutOfAUrlOrIsTakenAsTyped(string input, string expected)
        => Assert.Equal(expected, VRChatLinkEndpoints.ParseUserIdOrUrl(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://vrchat.com/")]
    public void NothingUsable_IsNull(string input)
        => Assert.Null(VRChatLinkEndpoints.ParseUserIdOrUrl(input));

    [Fact]
    public void CodesAreTypeable()
    {
        var code = VRChatLinkEndpoints.NewCode();

        Assert.StartsWith("modbot-", code, StringComparison.Ordinal);
        Assert.Equal(13, code.Length);
        Assert.DoesNotContain('0', code);
        Assert.DoesNotContain('O', code);
        Assert.DoesNotContain('I', code);
        Assert.DoesNotContain('1', code);
    }

    [Fact]
    public async Task AnUnlinkedAccount_CanReachOnlyTheLinkAndMe()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct, linked: false);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, "/api/auth/vrchat-link", null, cookie, Ct)).StatusCode);

        // Administrator, and still shut out: the link is not a permission.
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/users", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, ApiTestHost.AuditProbe, null, cookie, Ct)).StatusCode);

        // Signing out is always allowed.
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Post, "/api/auth/logout", null, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheWholeFlow_StartCheckLinked_AndTheDoorOpensOnTheSameCookie()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ApiTestHost.StartAsync(_db, gate);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct, linked: false);
        var vrchatId = $"usr_{Guid.NewGuid():N}";

        var started = await StartAsync(host, cookie, $"https://vrchat.com/home/user/{vrchatId}");
        var pending = started.GetProperty("pending");
        Assert.Equal(vrchatId, pending.GetProperty("vrChatUserId").GetString());
        var code = pending.GetProperty("code").GetString()!;
        Assert.StartsWith("modbot-", code, StringComparison.Ordinal);
        Assert.Equal(VRChatLinkEndpoints.ProfileUrl, started.GetProperty("profileUrl").GetString());

        // Bio without the code: not linked, told what to do.
        gate.Returns("GetPublicProfile", Profile(vrchatId, "Gunner24", "hello there"));
        var notYet = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct), Ct);
        Assert.False(notYet.GetProperty("linked").GetBoolean());
        Assert.Contains(code, notYet.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The call went through the gate on the users.read class, interactively.
        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.UsersProfile, call.Endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);

        host.Clock.Advance(VRChatLinkEndpoints.MinimumGapBetweenChecks);

        // Bio with the code, in any case: linked.
        gate.Returns("GetPublicProfile", Profile(vrchatId, "Gunner24", $"hello there {code.ToLowerInvariant()} bye"));
        var linked = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct), Ct);
        Assert.True(linked.GetProperty("linked").GetBoolean());
        Assert.Contains("Gunner24", linked.GetProperty("message").GetString(), StringComparison.Ordinal);

        var me = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct), Ct);
        Assert.True(me.GetProperty("vrChatLinked").GetBoolean());
        Assert.Equal(vrchatId, me.GetProperty("vrChatUserId").GetString());
        Assert.Equal("Gunner24", me.GetProperty("vrChatDisplayName").GetString());

        // Same cookie, no new sign-in, and the rest of Modbot is open now.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct)).StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.VRChatLinked, user.Id.ToString(), Ct));
        Assert.Contains(vrchatId, fact.Data, StringComparison.Ordinal);
        Assert.Contains("Gunner24", fact.Data, StringComparison.Ordinal);

        // The two profile fetches were sightings too: the person now has a vrchat_user row with
        // what the check read, so the profile pane and the sticky 18+ rule start from here rather
        // than from the sync's next pass.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var row = await db.VRChatUsers.AsNoTracking().SingleAsync(u => u.UserId == vrchatId, Ct);
            Assert.Equal("Gunner24", row.DisplayName);
            Assert.NotNull(row.LastRefreshedAt);
        }
    }

    [Fact]
    public async Task ChecksAreSpacedOut_AndLimitedPerCode()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ApiTestHost.StartAsync(_db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct, linked: false);
        var vrchatId = $"usr_{Guid.NewGuid():N}";
        gate.Returns("GetPublicProfile", Profile(vrchatId, "Someone", "no code here"));

        await StartAsync(host, cookie, vrchatId);

        var first = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Straight away again: too soon.
        var tooSoon = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);

        for (var i = 1; i < VRChatLinkEndpoints.MaxChecksPerCode; i++)
        {
            host.Clock.Advance(VRChatLinkEndpoints.MinimumGapBetweenChecks);
            var next = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }

        host.Clock.Advance(VRChatLinkEndpoints.MinimumGapBetweenChecks);
        var spent = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, spent.StatusCode);

        // Exactly the allowed number of profile fetches reached the gate.
        Assert.Equal(VRChatLinkEndpoints.MaxChecksPerCode, gate.Calls.Count);
    }

    [Fact]
    public async Task AnExpiredCode_MustBeStartedAgain()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ApiTestHost.StartAsync(_db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct, linked: false);

        await StartAsync(host, cookie, "usr_whoever");
        host.Clock.Advance(VRChatLinkEndpoints.CodeLifetime + TimeSpan.FromSeconds(1));

        var status = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/vrchat-link", null, cookie, Ct), Ct);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, status.GetProperty("pending").ValueKind);

        var check = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, check.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task OneVRChatAccount_LinksToOneModbotAccount()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ApiTestHost.StartAsync(_db, gate);
        var (first, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct, linked: false);

        var started = await StartAsync(host, cookie, first.VRChatUserId!);
        var code = started.GetProperty("pending").GetProperty("code").GetString()!;
        gate.Returns("GetPublicProfile", Profile(first.VRChatUserId!, "Twin", code));

        var result = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct), Ct);

        Assert.False(result.GetProperty("linked").GetBoolean());
        Assert.Contains("already linked", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGateFailure_IsExplained_NotRetried()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ApiTestHost.StartAsync(_db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct, linked: false);
        gate.Returns("GetPublicProfile", VRChatResult<PublicProfile>.Failure(429, "Too many requests", kind: VRChatFailureKind.RateLimited));

        await StartAsync(host, cookie, "usr_whoever");
        var result = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/auth/vrchat-link/check", null, cookie, Ct), Ct);

        Assert.False(result.GetProperty("linked").GetBoolean());
        Assert.Contains("rate limiting", result.GetProperty("message").GetString(), StringComparison.Ordinal);

        // A 429 is a cold stop: one call, never a retry (spec 4.3.1).
        Assert.Single(gate.Calls);
    }
}
