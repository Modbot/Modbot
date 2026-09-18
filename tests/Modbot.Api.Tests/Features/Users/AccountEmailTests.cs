using System.Net;
using Modbot.Api.Tests.Features.Onboarding;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>
/// Every account gets an address, and no two share one (server info and account email design §4).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AccountEmailTests
{
    private readonly PostgresFixture _db;

    public AccountEmailTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    private static string UniqueEmail() => $"u_{Guid.NewGuid():N}@example.com";

    // ── The wizard's first administrator ────────────────────────────────────────────────────

    [Fact]
    public async Task TheFirstAdministratorIsRefusedWithoutAnAddress()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "a-long-enough-password" },
            cookie: null,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheFirstAdministratorIsRefusedWithSomethingThatIsNotAnAddress()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "a-long-enough-password", email = "gunner24" },
            cookie: null,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheAddressIsStoredTrimmedAndLowerCased()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "a-long-enough-password", email = "  Bin@Example.COM " },
            cookie: null,
            Ct);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal("bin@example.com", body.GetProperty("email").GetString());
    }

    // ── The users page's temporary-password path ────────────────────────────────────────────

    [Fact]
    public async Task CreatingAnAccountFromTheUsersPageNeedsAnAddress()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new { username = UniqueName(), password = "a-long-enough-password", roleIds = Array.Empty<Guid>() },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TwoAccountsCannotShareAnAddress()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var email = UniqueEmail();

        var first = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new { username = UniqueName(), password = "a-long-enough-password", roleIds = Array.Empty<Guid>(), email },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Upper case does not make it a different address: the stored form is lower-cased and the
        // unique index is over that.
        var second = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new
            {
                username = UniqueName(),
                password = "a-long-enough-password",
                roleIds = Array.Empty<Guid>(),
                email = email.ToUpperInvariant(),
            },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // ── Changing an address afterwards ──────────────────────────────────────────────────────

    [Fact]
    public async Task SomebodyCannotTakeAnAddressAnotherAccountHolds()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var other = await host.CreateUserAsync(UniqueName(), "hunter2", ModbotPermissions.None, Ct);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/contact", new { email = other.Email }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task KeepingYourOwnAddressIsNotACollisionWithYourself()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (me, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/contact", new { email = me.Email }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnAddressCannotBeClearedAnyMore()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        // An empty string used to clear the field. It cannot now: an account with no address
        // cannot be reset and cannot sign in by email.
        var response = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/contact", new { email = "" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangingOnlyTheDiscordIdLeavesTheAddressAlone()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (me, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/contact", new { discordUserId = "123" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal(me.Email, body.GetProperty("email").GetString());
    }
}
