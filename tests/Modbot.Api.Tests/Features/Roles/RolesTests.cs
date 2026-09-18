using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Roles;

/// <summary>Roles as named permission sets (accounts and access design §3).</summary>
[Collection(nameof(PostgresCollection))]
public class RolesTests
{
    private readonly PostgresFixture _db;

    public RolesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"role_{Guid.NewGuid():N}";

    [Fact]
    public async Task TheBuiltInRoles_AreThereFromTheStart()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ApiTestHost.BodyOf(response, Ct);
        var names = body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();

        Assert.Contains("Administrator", names);
        Assert.Contains("Moderator", names);
        Assert.Contains("Viewer", names);

        // The catalogue travels with the roles, so the checklist and the API use the same words.
        var catalogue = body.GetProperty("permissions").EnumerateArray().ToList();
        Assert.Contains(catalogue, p => p.GetProperty("name").GetString() == "ManageRoles");
        Assert.All(catalogue, p => Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("label").GetString())));
    }

    [Fact]
    public async Task ChangingRoles_Needs401Then403ThenManageRoles()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var body = new { name = UniqueName(), description = "", permissions = new[] { "ViewMembers" } };

        var anonymous = await host.SendJsonAsync(HttpMethod.Post, "/api/roles", body, null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var (_, users) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var forbidden = await host.SendJsonAsync(HttpMethod.Post, "/api/roles", body, users, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var (_, roles) = await host.SignedInAsync(ModbotPermissions.ManageRoles | ModbotPermissions.ViewMembers, Ct);
        var allowed = await host.SendJsonAsync(HttpMethod.Post, "/api/roles", body, roles, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ACustomRole_CanBeMadeChangedAndDeleted_AndEachStepIsRecorded()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var created = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/roles",
            new { name = UniqueName(), description = "Greeters", permissions = new[] { "ViewMembers", "Warn" } },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var role = await ApiTestHost.BodyOf(created, Ct);
        var id = role.GetProperty("id").GetString()!;
        Assert.False(role.GetProperty("isBuiltIn").GetBoolean());

        var changed = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/roles/{id}",
            new { name = role.GetProperty("name").GetString(), description = "Greeters", permissions = new[] { "ViewMembers" } },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(["ViewMembers"], (await ApiTestHost.BodyOf(changed, Ct)).GetProperty("permissionNames").EnumerateArray().Select(p => p.GetString()));

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/roles/{id}", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Single(await host.FactsAsync(FactType.RoleCreated, id, Ct));
        Assert.Single(await host.FactsAsync(FactType.RoleChanged, id, Ct));
        Assert.Single(await host.FactsAsync(FactType.RoleDeleted, id, Ct));
    }

    [Fact]
    public async Task BuiltInRoles_CanBeEditedButNotDeleted_AndAdministratorNotEvenEdited()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var deleteViewer = await host.SendJsonAsync(HttpMethod.Delete, $"/api/roles/{BuiltInRoles.ViewerId}", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, deleteViewer.StatusCode);

        var renameViewer = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/roles/{BuiltInRoles.ViewerId}",
            new { name = "Watcher", description = "", permissions = new[] { "ViewMembers" } },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, renameViewer.StatusCode);

        var editAdministrator = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/roles/{BuiltInRoles.AdministratorId}",
            new { name = "Administrator", description = "", permissions = new[] { "ViewMembers" } },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, editAdministrator.StatusCode);

        // Editing what Viewer allows is fine -- and put it back afterwards, since the table is
        // shared by the whole assembly.
        var widen = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/roles/{BuiltInRoles.ViewerId}",
            new { name = "Viewer", description = "Read-only", permissions = new[] { "ViewMembers", "ViewProfile", "ViewAnalytics", "ViewAuditLog", "ViewEvidence" } },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.OK, widen.StatusCode);

        await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/roles/{BuiltInRoles.ViewerId}",
            new { name = "Viewer", description = "Can look at members, history and analytics, and change nothing.", permissions = new[] { "ViewMembers", "ViewProfile", "ViewAnalytics", "ViewAuditLog" } },
            cookie,
            Ct);
    }

    [Fact]
    public async Task ARoleSomebodyHolds_CannotBeDeleted()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var created = await ApiTestHost.BodyOf(await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/roles",
            new { name = UniqueName(), description = "", permissions = new[] { "ViewMembers" } },
            cookie,
            Ct), Ct);
        var id = created.GetProperty("id").GetGuid();

        await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new { username = $"u_{Guid.NewGuid():N}", password = "a-long-enough-password", email = $"u_{Guid.NewGuid():N}@example.com", roleIds = new[] { id } },
            cookie,
            Ct);

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/roles/{id}", null, cookie, Ct);

        // Silently stripping the holder's access is not what anyone pressing Delete expects.
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
    }

    [Fact]
    public async Task YouCannotPutAPermissionYouLackIntoARole()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageRoles, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/roles",
            new { name = UniqueName(), description = "", permissions = new[] { "Administrator" } },
            cookie,
            Ct);

        // Otherwise manage-roles plus manage-users would add up to owner.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownPermissionName_IsNamedInTheError()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/roles",
            new { name = UniqueName(), description = "", permissions = new[] { "FlyTheShip" } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("FlyTheShip", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
