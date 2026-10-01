using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>
/// Roles have an order, and Manage users and Manage roles only reach what is below the caller's
/// highest role (accounts and access design §3.5).
/// </summary>
/// <remarks>
/// Test accounts rank by how many permissions they hold: more is higher, the same number is the
/// same rank (<see cref="TestAccounts.PositionFor"/>). So "the lead" below holds three, "a peer"
/// holds the same three, "below" holds one and "above" holds four.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class RoleOrderTests
{
    private const string AccountsBelow = "You can only change accounts below your highest role.";
    private const string RolesBelow = "You can only change roles below your highest role.";

    private const ModbotPermissions Lead =
        ModbotPermissions.ManageUsers | ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;

    private readonly PostgresFixture _db;

    public RoleOrderTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"role_{Guid.NewGuid():N}";

    private static string Users(Guid id, string action) => $"/api/users/{id}/{action}";

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string sentence, string what)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"{what} answered {(int)response.StatusCode}, expected 403.");
        Assert.Contains(sentence, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>The test role holding exactly these permissions: the one an account made with them holds.</summary>
    private async Task<Guid> RoleForAsync(ModbotPermissions permissions)
    {
        await using var db = _db.NewContext();
        return await TestAccounts.RoleForAsync(db, permissions, Ct);
    }

    /// <summary>A role of exactly this rank, holding these permissions, held by nobody yet.</summary>
    private async Task<Guid> RoleAtAsync(ModbotPermissions permissions, int position)
    {
        await using var db = _db.NewContext();

        var name = UniqueName();
        var role = new ModbotRole
        {
            Name = name,
            NameNormalized = ModbotRole.Normalize(name),
            Permissions = permissions,
            Position = position,
            CreatedAt = new FakeClock().UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync(Ct);
        return role.Id;
    }

    /// <summary>Every attempt a manager can make against one account.</summary>
    private static (string What, HttpMethod Method, string Path, object? Body)[] SixChanges(ModbotUser target, Guid[] roleIds) =>
    [
        ("set roles", HttpMethod.Put, Users(target.Id, "roles"), new { roleIds }),
        ("set contact", HttpMethod.Put, Users(target.Id, "contact"), new { email = $"u_{Guid.NewGuid():N}@example.com" }),
        ("make a reset link", HttpMethod.Post, Users(target.Id, "reset-link"), null),
        ("disable", HttpMethod.Post, Users(target.Id, "disable"), null),
        ("enable", HttpMethod.Post, Users(target.Id, "enable"), null),
        ("delete", HttpMethod.Post, Users(target.Id, "delete"), new { username = target.Username }),
    ];

    [Fact]
    public async Task TheBuiltInRoles_AreInOrder_AdministratorFirst_ViewerLast()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct), Ct);
        var positions = body.GetProperty("roles").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("position").GetInt32());

        Assert.True(positions[BuiltInRoles.AdministratorId] < positions[BuiltInRoles.ModeratorId]);
        Assert.True(positions[BuiltInRoles.ModeratorId] < positions[BuiltInRoles.ViewerId]);
    }

    [Fact]
    public async Task AManager_CannotChangeAnAccountAtOrAboveTheirHighestRole_OnAnyOfTheSixEndpoints()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, lead) = await host.SignedInAsync(Lead, Ct);

        var peer = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, Lead, Ct);
        var above = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, Lead | ModbotPermissions.ViewAnalytics, Ct);
        var admin = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Administrator, Ct);

        foreach (var target in new[] { peer, above, admin })
        {
            foreach (var (what, method, path, body) in SixChanges(target, []))
            {
                var response = await host.SendJsonAsync(method, path, body, lead, Ct);
                await AssertRefusedAsync(response, AccountsBelow, $"{what} on {target.Username}");
            }
        }
    }

    [Fact]
    public async Task AManager_MayChangeAnAccountBelowThem_OnEveryEndpoint()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, lead) = await host.SignedInAsync(Lead, Ct);
        var below = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.ViewMembers, Ct);
        var nobody = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.None, Ct);

        // Disabling and deleting are refused for the last enabled administrator, so one must exist.
        await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Administrator, Ct);

        var viewerRole = await RoleForAsync(ModbotPermissions.ViewMembers);

        foreach (var target in new[] { below, nobody })
        {
            foreach (var (what, method, path, body) in SixChanges(target, [viewerRole]))
            {
                var response = await host.SendJsonAsync(method, path, body, lead, Ct);
                Assert.True(
                    response.StatusCode == HttpStatusCode.OK,
                    $"{what} on {target.Username} answered {(int)response.StatusCode}, expected 200.");
            }
        }
    }

    [Fact]
    public async Task AnAdministrator_MayChangeAnotherAdministrator()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var other = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Administrator, Ct);

        var link = await host.SendJsonAsync(HttpMethod.Post, Users(other.Id, "reset-link"), null, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);

        var contact = await host.SendJsonAsync(
            HttpMethod.Put, Users(other.Id, "contact"), new { email = $"u_{Guid.NewGuid():N}@example.com" }, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, contact.StatusCode);

        // The last-administrator guard is unchanged: another administrator remains, so this goes through.
        var disable = await host.SendJsonAsync(HttpMethod.Post, Users(other.Id, "disable"), null, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
    }

    [Fact]
    public async Task TheCallersOwnAccount_PassesTheAccountCheck()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (lead, cookie) = await host.SignedInAsync(Lead, Ct);

        var contact = await host.SendJsonAsync(
            HttpMethod.Put, Users(lead.Id, "contact"), new { email = $"u_{Guid.NewGuid():N}@example.com" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, contact.StatusCode);
    }

    [Fact]
    public async Task ARoleAtTheCallersOwnRank_CannotBeGivenOrTaken()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (lead, cookie) = await host.SignedInAsync(Lead, Ct);
        var below = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.ViewMembers, Ct);

        // Setting roles refuses when no administrator would remain, and the database is shared: this
        // test passed only if an earlier one had left an administrator behind. It brings its own.
        await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Administrator, Ct);

        // Holds nothing the lead lacks, so it is only the order that stops this.
        var equal = await RoleAtAsync(ModbotPermissions.ViewMembers, TestAccounts.PositionFor(Lead));
        var lower = await RoleAtAsync(ModbotPermissions.ViewMembers, TestAccounts.PositionFor(Lead) + 1);

        var giveEqual = await host.SendJsonAsync(HttpMethod.Put, Users(below.Id, "roles"), new { roleIds = new[] { equal } }, cookie, Ct);
        await AssertRefusedAsync(giveEqual, RolesBelow, "giving a role at the caller's rank");

        var giveLower = await host.SendJsonAsync(HttpMethod.Put, Users(below.Id, "roles"), new { roleIds = new[] { lower } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, giveLower.StatusCode);

        // Their own top role is not theirs to take off themselves: it is at their own rank.
        var stripSelf = await host.SendJsonAsync(HttpMethod.Put, Users(lead.Id, "roles"), new { roleIds = Array.Empty<Guid>() }, cookie, Ct);
        await AssertRefusedAsync(stripSelf, RolesBelow, "taking their own highest role");

        var create = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/users",
            new { username = $"u_{Guid.NewGuid():N}", password = "a-long-enough-password", email = $"u_{Guid.NewGuid():N}@example.com", roleIds = new[] { equal } },
            cookie,
            Ct);
        await AssertRefusedAsync(create, RolesBelow, "creating an account with a role at the caller's rank");

        var invite = await host.SendJsonAsync(HttpMethod.Post, "/api/invites", new { roleIds = new[] { equal } }, cookie, Ct);
        await AssertRefusedAsync(invite, RolesBelow, "an invite carrying a role at the caller's rank");

        var inviteBelow = await host.SendJsonAsync(HttpMethod.Post, "/api/invites", new { roleIds = new[] { lower } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, inviteBelow.StatusCode);
    }

    [Fact]
    public async Task AnAdministrator_MayGiveAnyRole()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var user = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.None, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, Users(user.Id, "roles"), new { roleIds = new[] { BuiltInRoles.AdministratorId } }, admin, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheUsersList_AndMe_SayWhoRanksWhere()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (lead, cookie) = await host.SignedInAsync(Lead, Ct);
        var nobody = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.None, Ct);

        var me = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct), Ct);
        Assert.Equal(TestAccounts.PositionFor(Lead), me.GetProperty("rank").GetInt32());

        var users = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/users", null, cookie, Ct), Ct);
        var byId = users.EnumerateArray().ToDictionary(u => u.GetProperty("id").GetGuid());

        Assert.Equal(TestAccounts.PositionFor(Lead), byId[lead.Id].GetProperty("rank").GetInt32());
        Assert.Equal(JsonValueKind.Null, byId[nobody.Id].GetProperty("rank").ValueKind);
    }

    [Fact]
    public async Task AKey_FollowsItsOwnersRank()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Lead | ModbotPermissions.ManageApiKeys, Ct);
        var below = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.ViewMembers, Ct);
        var admin = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Administrator, Ct);

        var made = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(
                HttpMethod.Post, "/api/api-keys", new { name = "Bot", permissions = new[] { "ManageUsers" } }, cookie, Ct),
            Ct);
        var key = made.GetProperty("key").GetString()!;

        async Task<HttpResponseMessage> WithKeyAsync(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            return await host.Client.SendAsync(request, Ct);
        }

        await AssertRefusedAsync(await WithKeyAsync(Users(admin.Id, "reset-link")), AccountsBelow, "a key making an administrator a reset link");
        Assert.Equal(HttpStatusCode.OK, (await WithKeyAsync(Users(below.Id, "reset-link"))).StatusCode);
    }

    [Fact]
    public async Task ANewRole_IsMadeAtTheBottom()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var created = await ApiTestHost.BodyOf(await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/roles",
            new { name = UniqueName(), description = "", permissions = new[] { "ViewMembers" } },
            cookie,
            Ct), Ct);

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct), Ct);
        var roles = list.GetProperty("roles").EnumerateArray().ToList();

        Assert.Equal(created.GetProperty("id").GetGuid(), roles[^1].GetProperty("id").GetGuid());
        Assert.Equal(roles.Max(r => r.GetProperty("position").GetInt32()), created.GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task ARoleMovesUpAndDown_AndAdministratorStaysFirst()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        async Task<Guid> MakeAsync() => (await ApiTestHost.BodyOf(await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/roles",
            new { name = UniqueName(), description = "", permissions = new[] { "ViewMembers" } },
            cookie,
            Ct), Ct)).GetProperty("id").GetGuid();

        var first = await MakeAsync();
        var second = await MakeAsync();

        async Task<List<Guid>> OrderAsync()
            => (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct), Ct))
                .GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

        var before = await OrderAsync();
        Assert.Equal(before.IndexOf(first) + 1, before.IndexOf(second));

        var up = await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{second}/move", new { direction = "up" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        // The answer is the new order.
        var answered = (await ApiTestHost.BodyOf(up, Ct)).GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(answered.IndexOf(second) + 1, answered.IndexOf(first));
        Assert.Equal(answered, await OrderAsync());

        var down = await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{second}/move", new { direction = "down" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        var after = await OrderAsync();
        Assert.Equal(after.IndexOf(first) + 1, after.IndexOf(second));

        // Recorded like any other change to a role.
        Assert.Single(await host.FactsAsync(FactType.RoleChanged, second.ToString(), Ct), f =>
            ApiTestHost.DataOf(f).GetProperty("moved").GetString() == "up");

        // Last cannot go down; Administrator cannot move at all, and nothing goes above it.
        var last = await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{second}/move", new { direction = "down" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, last.StatusCode);

        var moveAdministrator = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/roles/{BuiltInRoles.AdministratorId}/move", new { direction = "down" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, moveAdministrator.StatusCode);

        // The first role that is not an administrator role cannot go up past them.
        var rolesNow = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct), Ct))
            .GetProperty("roles").EnumerateArray().ToList();
        var firstBelow = rolesNow.First(r => r.GetProperty("position").GetInt32() != int.MinValue).GetProperty("id").GetGuid();
        var overAdministrator = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/roles/{firstBelow}/move", new { direction = "up" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, overAdministrator.StatusCode);

        var nonsense = await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{first}/move", new { direction = "sideways" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
    }

    [Fact]
    public async Task ManageRoles_OnlyReachesRolesBelowTheCallersHighest()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var caller = ModbotPermissions.ManageRoles | ModbotPermissions.ViewMembers;
        var (_, cookie) = await host.SignedInAsync(caller, Ct);
        var ownRole = await RoleForAsync(caller);
        var above = await RoleAtAsync(ModbotPermissions.ViewMembers, TestAccounts.PositionFor(caller) - 1);

        var body = new { name = UniqueName(), description = "", permissions = new[] { "ViewMembers" } };

        foreach (var (what, id) in new[] { ("their own role", ownRole), ("a role above them", above) })
        {
            await AssertRefusedAsync(
                await host.SendJsonAsync(HttpMethod.Put, $"/api/roles/{id}", body, cookie, Ct), RolesBelow, $"editing {what}");
            await AssertRefusedAsync(
                await host.SendJsonAsync(HttpMethod.Delete, $"/api/roles/{id}", null, cookie, Ct), RolesBelow, $"deleting {what}");
            await AssertRefusedAsync(
                await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{id}/move", new { direction = "down" }, cookie, Ct), RolesBelow, $"moving {what}");
        }

        // The Administrator role is above everybody but an administrator.
        await AssertRefusedAsync(
            await host.SendJsonAsync(HttpMethod.Put, $"/api/roles/{BuiltInRoles.AdministratorId}", body, cookie, Ct), RolesBelow, "editing Administrator");

        // A role they make sits at the bottom, so it is theirs to edit, move and delete.
        var made = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/roles", body, cookie, Ct), Ct);
        var mine = made.GetProperty("id").GetGuid();

        var edit = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/roles/{mine}", new { name = UniqueName(), description = "x", permissions = new[] { "ViewMembers" } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var move = await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{mine}/move", new { direction = "up" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/roles/{mine}", null, cookie, Ct)).StatusCode);
    }

    /// <summary>Gives an existing account one more role, straight in the database.</summary>
    private async Task GiveAsync(Guid userId, Guid roleId)
    {
        await using var db = _db.NewContext();
        db.UserRoles.Add(new ModbotUserRole { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ARoleThatCarriesAdministrator_CountsAsFirst_WhereverItIsStored()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, lead) = await host.SignedInAsync(Lead, Ct);

        // Made at the very bottom by number, but holding it is being an administrator.
        var custom = await RoleAtAsync(ModbotPermissions.Administrator, 100_000);
        var holder = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.None, Ct);
        await GiveAsync(holder.Id, custom);

        foreach (var (what, method, path, body) in SixChanges(holder, []))
        {
            var response = await host.SendJsonAsync(method, path, body, lead, Ct);
            await AssertRefusedAsync(response, AccountsBelow, $"{what} on an account whose only role carries Administrator");
        }

        // The list puts it first, ahead of Moderator, and says so.
        var roles = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, lead, Ct), Ct))
            .GetProperty("roles").EnumerateArray().ToList();
        var ids = roles.Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.True(ids.IndexOf(custom) < ids.IndexOf(BuiltInRoles.ModeratorId));
        Assert.Equal(int.MinValue, roles[ids.IndexOf(custom)].GetProperty("position").GetInt32());

        var users = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/users", null, lead, Ct), Ct);
        Assert.Equal(
            int.MinValue,
            users.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == holder.Id).GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task ARoleThatCarriesAdministrator_CannotBeEditedMovedOrDeletedByANonAdministrator()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageRoles | ModbotPermissions.ViewMembers, Ct);
        var custom = await RoleAtAsync(ModbotPermissions.Administrator, 100_000);

        var body = new { name = UniqueName(), description = "", permissions = new[] { "ViewMembers" } };

        await AssertRefusedAsync(
            await host.SendJsonAsync(HttpMethod.Put, $"/api/roles/{custom}", body, cookie, Ct), RolesBelow, "editing it");
        await AssertRefusedAsync(
            await host.SendJsonAsync(HttpMethod.Delete, $"/api/roles/{custom}", null, cookie, Ct), RolesBelow, "deleting it");
        await AssertRefusedAsync(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{custom}/move", new { direction = "up" }, cookie, Ct), RolesBelow, "moving it up");
        await AssertRefusedAsync(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{custom}/move", new { direction = "down" }, cookie, Ct), RolesBelow, "moving it down");
    }

    [Fact]
    public async Task ARoleThatCarriesAdministrator_IsNotMovedByAnAdministratorEither()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var custom = await RoleAtAsync(ModbotPermissions.Administrator, 100_000);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{custom}/move", new { direction = "up" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ARole_CannotBeMovedUpIntoTheCallersOwnPlace()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var caller = ModbotPermissions.ManageRoles | ModbotPermissions.ViewMembers;
        var (_, cookie) = await host.SignedInAsync(caller, Ct);
        var ownRole = await RoleForAsync(caller);

        // The role right under the caller's: moving it up would swap it with the caller's own.
        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/roles", null, cookie, Ct), Ct);
        var ids = list.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        var next = ids[ids.IndexOf(ownRole) + 1];

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/roles/{next}/move", new { direction = "up" }, cookie, Ct);

        await AssertRefusedAsync(response, RolesBelow, "moving a role up into the caller's own place");
    }
}
