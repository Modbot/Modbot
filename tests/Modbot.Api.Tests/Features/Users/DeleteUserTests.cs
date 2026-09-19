using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>
/// Deleting an account from the users page (username rules and deleting accounts design §3).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DeleteUserTests
{
    private readonly PostgresFixture _db;

    public DeleteUserTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Path(Guid id) => $"/api/users/{id}/delete";

    [Fact]
    public async Task DeletingEmptiesTheAccountAndRecordsIt()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, theirs) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var was = user.Username;

        var response = await host.SendJsonAsync(HttpMethod.Post, Path(user.Id), new { username = was }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(DeletedAccount.NameFor(user.Id), body.GetProperty("username").GetString());
        Assert.True(body.GetProperty("isDeleted").GetBoolean());
        Assert.True(body.GetProperty("isDisabled").GetBoolean());
        Assert.Empty(body.GetProperty("roles").EnumerateArray());
        Assert.Null(body.GetProperty("email").GetString());
        Assert.False(body.GetProperty("vrChatLinked").GetBoolean());

        // The session goes on the spot, not at the next sign-in.
        var dead = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, theirs, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, dead.StatusCode);

        // And there is no signing in again.
        var login = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = was, password = TestAccounts.Password }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.UserDeleted, user.Id.ToString(), Ct));
        Assert.Equal(admin.Id.ToString(), fact.ActorId);
        var data = ApiTestHost.DataOf(fact);
        Assert.Equal(was, data.GetProperty("was").GetString());
        Assert.Equal(DeletedAccount.NameFor(user.Id), data.GetProperty("now").GetString());
    }

    /// <summary>
    /// The row stays, so everything that named the account still finds it. The copies other tables
    /// took of the username at the time stay too: they say what was true then, and rewriting them
    /// would make Modbot's history disagree with what actually happened.
    /// </summary>
    [Fact]
    public async Task EverythingTheAccountDidStillPointsAtIt()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var was = user.Username;

        var caseFileId = Guid.CreateVersion7();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.CaseFiles.Add(new CaseFile
            {
                Id = caseFileId,
                UserId = $"usr_{Guid.NewGuid()}",
                AuthorUserId = user.Id,
                AuthorUsername = was,
                WrittenReason = "Kept, and still theirs.",
                CreatedAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, Path(user.Id), new { username = was }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == user.Id, Ct);
            Assert.NotNull(row);
            Assert.NotNull(row.DeletedAt);

            var file = await db.CaseFiles.AsNoTracking().FirstAsync(c => c.Id == caseFileId, Ct);
            Assert.Equal(user.Id, file.AuthorUserId);
            Assert.Equal(was, file.AuthorUsername);

            // The facts about the account are still about the same id.
            Assert.True(await db.Events.AsNoTracking()
                .AnyAsync(e => e.SubjectId == user.Id.ToString() && e.Type == FactType.UserCreated, Ct));
        }
    }

    [Fact]
    public async Task AWrongUsernameIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        foreach (var typed in new[] { "", "somebody_else", user.Username + "_" })
        {
            var response = await host.SendJsonAsync(
                HttpMethod.Post, Path(user.Id), new { username = typed }, cookie, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id, Ct);
        Assert.Null(row.DeletedAt);
        Assert.Equal(user.Username, row.Username);
    }

    /// <summary>Case and surrounding spaces do not count; the characters do.</summary>
    [Fact]
    public async Task TheTypedNameIsMatchedTheWayEveryUsernameIs()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, Path(user.Id), new { username = $"  {user.Username.ToUpperInvariant()}  " }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task YouCannotDeleteYourself()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, Path(admin.Id), new { username = admin.Username }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "your own account", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastAdministratorCannotBeDeleted()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        // Nobody else in this database may be an enabled administrator for the guard to bite,
        // and other tests leave administrators behind -- so clear the table first.
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var (admin, _) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, Path(admin.Id), new { username = admin.Username }, manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "nobody who can administer Modbot",
            await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingNeedsManageUsers()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ManageSettings, Ct);

        var forbidden = await host.SendJsonAsync(
            HttpMethod.Post, Path(user.Id), new { username = user.Username }, viewer, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var anonymous = await host.SendJsonAsync(
            HttpMethod.Post, Path(user.Id), new { username = user.Username }, null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    /// <summary>There is nobody behind the row any more, so nothing else on the page applies.</summary>
    [Fact]
    public async Task ADeletedAccountCannotBeEnabledRenamedOrWrittenTo()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var deleted = await host.SendJsonAsync(
            HttpMethod.Post, Path(user.Id), new { username = user.Username }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"/api/users/{user.Id}/enable", null),
            (HttpMethod.Put, $"/api/users/{user.Id}/roles", new { roleIds = new[] { BuiltInRoles.ViewerId } }),
            (HttpMethod.Put, $"/api/users/{user.Id}/contact", new { email = $"u_{Guid.NewGuid():N}@example.com" }),
            (HttpMethod.Post, $"/api/users/{user.Id}/reset-link", null),
        };

        foreach (var (method, path, body) in attempts)
        {
            var response = await host.SendJsonAsync(method, path, body, cookie, Ct);
            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"{method} {path} answered {(int)response.StatusCode}, expected 400.");
        }
    }

    /// <summary>Deleting twice changes nothing and complains about nothing.</summary>
    [Fact]
    public async Task DeletingAnAlreadyDeletedAccountIsAnswered()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.SendJsonAsync(
            HttpMethod.Post, Path(user.Id), new { username = user.Username }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var again = await host.SendJsonAsync(
            HttpMethod.Post, Path(user.Id), new { username = user.Username }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        Assert.Single(await host.FactsAsync(FactType.UserDeleted, user.Id.ToString(), Ct));
    }

    [Fact]
    public async Task AnAccountThatDoesNotExistIsNotFound()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, Path(Guid.CreateVersion7()), new { username = "whoever" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
