using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// One sign-in field, matched against the username and the email address (server info and account
/// email design §5).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SignInWithEitherTests
{
    private readonly PostgresFixture _db;

    public SignInWithEitherTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    [Fact]
    public async Task TheUsernameStillWorks()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", ModbotPermissions.ViewMembers, Ct);

        var response = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "hunter2" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheEmailAddressWorksToo()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        var user = await host.CreateUserAsync(name, "hunter2", ModbotPermissions.ViewMembers, Ct);

        var response = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = user.Email, password = "hunter2" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Set-Cookie", response.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task EitherOneInTheWrongCaseIsStillTheSameAccount()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        var user = await host.CreateUserAsync(name, "hunter2", ModbotPermissions.ViewMembers, Ct);

        var byName = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name.ToUpperInvariant(), password = "hunter2" }, Ct);

        var byEmail = await host.Client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = user.Email!.ToUpperInvariant(), password = "hunter2" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, byName.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byEmail.StatusCode);
    }

    [Fact]
    public async Task AnUnknownAddressIsRefusedTheSameWayEverythingElseIs()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        var user = await host.CreateUserAsync(name, "hunter2", ModbotPermissions.ViewMembers, Ct);

        var wrongPassword = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "not the password" }, Ct);

        var unknownName = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = UniqueName(), password = "hunter2" }, Ct);

        var unknownAddress = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = "nobody@example.com", password = "hunter2" }, Ct);

        // Byte for byte the same answer, or the sign-in form becomes a way to find out both which
        // usernames are real and who has an account here.
        foreach (var response in new[] { wrongPassword, unknownName, unknownAddress })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
            Assert.DoesNotContain("Set-Cookie", response.Headers.Select(h => h.Key));
        }

        Assert.NotNull(user.Email);
    }

    [Fact]
    public async Task ABlankSignInMatchesNobody()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        // An account made before addresses were required has email null, and null equals nothing
        // in SQL -- but an empty string would equal an empty column, so this is worth pinning.
        var name = UniqueName();
        var user = await host.CreateUserAsync(name, "hunter2", ModbotPermissions.None, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Core.Data.ModbotContext>();
            var row = await db.Users.FirstAsync(u => u.Id == user.Id, Ct);
            row.Email = null;
            await db.SaveChangesAsync(Ct);
        }

        var response = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = "", password = "hunter2" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AFailedSignInRecordsWhatWasTypedAndNotThePassword()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        var user = await host.CreateUserAsync(name, "hunter2", ModbotPermissions.None, Ct);

        await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = user.Email, password = "not the password" }, Ct);

        // Typed as an address, and still attributed to the account it names, so that account's
        // history shows the attempts against it.
        var facts = await host.FactsAsync(FactType.LoginFailed, user.Id.ToString(), Ct);
        var payload = ApiTestHost.DataOf(facts[0]);

        Assert.Equal(user.Email, payload.GetProperty("username").GetString());
        Assert.DoesNotContain("not the password", facts[0].Data, StringComparison.Ordinal);
    }
}
