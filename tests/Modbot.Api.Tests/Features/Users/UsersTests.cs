using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>The users page's API (accounts and access design §4).</summary>
[Collection(nameof(PostgresCollection))]
public class UsersTests
{
    private readonly PostgresFixture _db;

    public UsersTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    /// <summary>Every account gets one, and no two share one (server info and account email design §4).</summary>
    private static string UniqueEmail() => $"u_{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task ListingUsers_Needs401Then403ThenManageUsers()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        var anonymous = await host.Client.GetAsync("/api/users", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var forbidden = await host.SendJsonAsync(HttpMethod.Get, "/api/users", null, viewer, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var allowed = await host.SendJsonAsync(HttpMethod.Get, "/api/users", null, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task EveryWriteEndpoint_Answers403WithoutManageUsers()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (target, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ManageSettings, Ct);

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/api/users", new { username = "x", password = "a-long-enough-password", roleIds = Array.Empty<Guid>() }),
            (HttpMethod.Put, $"/api/users/{target.Id}/roles", new { roleIds = Array.Empty<Guid>() }),
            (HttpMethod.Post, $"/api/users/{target.Id}/disable", null),
            (HttpMethod.Post, $"/api/users/{target.Id}/enable", null),
            (HttpMethod.Put, $"/api/users/{target.Id}/contact", new { email = "a@b.c" }),
            (HttpMethod.Post, $"/api/users/{target.Id}/reset-link", null),
            (HttpMethod.Post, "/api/invites", new { roleIds = Array.Empty<Guid>() }),
            (HttpMethod.Get, "/api/invites", null),
        };

        foreach (var (method, path, body) in attempts)
        {
            var response = await host.SendJsonAsync(method, path, body, viewer, Ct);
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{method} {path} answered {(int)response.StatusCode}, expected 403.");
        }
    }

    [Fact]
    public async Task CreatingAUserWithATemporaryPassword_MakesAnAccountThatCanSignIn_AndRecordsIt()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var name = UniqueName();

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new { username = name, password = "a-long-enough-password", email = UniqueEmail(), roleIds = new[] { BuiltInRoles.ModeratorId } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        var id = body.GetProperty("id").GetString()!;
        Assert.Contains("Kick", body.GetProperty("permissionNames").EnumerateArray().Select(p => p.GetString()));

        // Usable with the ordinary login endpoint: the users page must not create a second
        // class of account.
        var login = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "a-long-enough-password" }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.UserCreated, id, Ct));
        Assert.Equal(admin.Id.ToString(), fact.ActorId);
        var data = ApiTestHost.DataOf(fact);
        Assert.Equal("temporary-password", data.GetProperty("how").GetString());
        Assert.Equal("Moderator", data.GetProperty("roles").GetString());
    }

    [Fact]
    public async Task ADuplicateUsername_Conflicts()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (existing, cookie) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new { username = existing.Username.ToUpperInvariant(), password = "a-long-enough-password", email = UniqueEmail(), roleIds = Array.Empty<Guid>() },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AManagerCannotHandOutPermissionsTheyDoNotHold()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);

        // ManageUsers without Administrator, trying to create an Administrator: this is how
        // "manage users" would otherwise quietly become "become the owner".
        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new { username = UniqueName(), password = "a-long-enough-password", email = UniqueEmail(), roleIds = new[] { BuiltInRoles.AdministratorId } },
            manager,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangingRoles_TakesEffectOnTheNextRequest_AndRecordsBeforeAndAfter()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var before = await host.SendJsonAsync(HttpMethod.Get, ApiTestHost.AuditProbe, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        var change = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/users/{user.Id}/roles",
            new { roleIds = new[] { BuiltInRoles.ModeratorId } },
            admin,
            Ct);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        // Same cookie, no new sign-in: the session check refreshed the permission claim.
        var after = await host.SendJsonAsync(HttpMethod.Get, ApiTestHost.AuditProbe, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.UserRolesChanged, user.Id.ToString(), Ct));
        Assert.Equal("Moderator", ApiTestHost.DataOf(fact).GetProperty("after").GetString());
    }

    [Fact]
    public async Task DisablingAnAccount_EndsItsSessionNow_AndReEnablingDoesNotReviveIt()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var alive = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

        host.Clock.Advance(TimeSpan.FromSeconds(30));

        var disable = await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{user.Id}/disable", null, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        // The very next request, not the next sign-in (design §5).
        var dead = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, dead.StatusCode);

        var enable = await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{user.Id}/enable", null, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        // The old cookie stays dead: the cut-off it fell behind does not move back.
        var stillDead = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, stillDead.StatusCode);

        // But the person can sign in again.
        var again = await host.LoginAsync(user.Username, TestAccounts.Password, Ct);
        Assert.NotEmpty(again);

        Assert.Single(await host.FactsAsync(FactType.UserDisabled, user.Id.ToString(), Ct));
        Assert.Single(await host.FactsAsync(FactType.UserEnabled, user.Id.ToString(), Ct));
    }

    [Fact]
    public async Task YouCannotDisableYourself()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{admin.Id}/disable", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheLastAdministrator_CannotBeDemoted()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        // Nobody else in this database may be an enabled administrator for the guard to bite,
        // and other tests leave administrators behind -- so clear the table first.
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/users/{admin.Id}/roles",
            new { roleIds = new[] { BuiltInRoles.ViewerId } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nobody who can administer Modbot", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContactDetails_AreRecordedByFieldNotByValue()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/users/{user.Id}/contact",
            new { email = "mod@example.com", discordUserId = "123456789012345678" },
            admin,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.ContactChanged, user.Id.ToString(), Ct));
        Assert.Contains("email", fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("mod@example.com", fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012345678", fact.Data, StringComparison.Ordinal);
    }
}
