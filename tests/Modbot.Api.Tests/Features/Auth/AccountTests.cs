using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Auth.Login;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// The signed-in person's own account (accounts and access design §4), and what signing in
/// leaves in the fact log (§6, §7).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AccountTests
{
    private readonly PostgresFixture _db;

    public AccountTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records the waits instead of taking them.</summary>
    private sealed class RecordingDelay : IDelayScheduler
    {
        public List<TimeSpan> Waits { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
        {
            Waits.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ChangingYourPassword_EndsOtherSessions_AndKeepsThisOne()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, here) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var elsewhere = await host.LoginAsync(user.Username, TestAccounts.Password, Ct);

        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var response = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/password",
            new { currentPassword = TestAccounts.Password, newPassword = "a-brand-new-password", confirmPassword = "a-brand-new-password" },
            here, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The browser it was typed in gets a fresh cookie and carries on.
        var refreshed = ApiTestHost.SessionCookie(response);
        var stillHere = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, refreshed, Ct);
        Assert.Equal(HttpStatusCode.OK, stillHere.StatusCode);

        // Every other one is done.
        var gone = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, elsewhere, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);

        Assert.NotEmpty(await host.LoginAsync(user.Username, "a-brand-new-password", Ct));
        Assert.Single(await host.FactsAsync(FactType.PasswordChanged, user.Id.ToString(), Ct));
    }

    [Fact]
    public async Task ChangingYourPassword_NeedsTheCurrentOne()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/password",
            new { currentPassword = "not-it", newPassword = "a-brand-new-password" },
            cookie, Ct);

        // A browser left open must not be enough to take the account over.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangingYourUsername_KeepsTheUniquenessRule_AndRecordsOldAndNew()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (other, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var taken = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/username",
            new { username = other.Username.ToUpperInvariant(), currentPassword = TestAccounts.Password },
            cookie, Ct);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        var newName = $"renamed_{Guid.NewGuid():N}";
        var changed = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/username",
            new { username = newName, currentPassword = TestAccounts.Password },
            cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // The re-issued cookie carries the new name.
        var refreshed = ApiTestHost.SessionCookie(changed);
        var me = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, refreshed, Ct), Ct);
        Assert.Equal(newName, me.GetProperty("username").GetString());

        var fact = Assert.Single(await host.FactsAsync(FactType.UsernameChanged, user.Id.ToString(), Ct));
        var data = ApiTestHost.DataOf(fact);
        Assert.Equal(user.Username, data.GetProperty("from").GetString());
        Assert.Equal(newName, data.GetProperty("to").GetString());

        Assert.NotEmpty(await host.LoginAsync(newName, TestAccounts.Password, Ct));
    }

    [Fact]
    public async Task SignOutEverywhere_EndsEverySessionIncludingThisOne()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, here) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var elsewhere = await host.LoginAsync(user.Username, TestAccounts.Password, Ct);

        host.Clock.Advance(TimeSpan.FromSeconds(1));

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/sign-out-everywhere", null, here, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, here, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, elsewhere, Ct)).StatusCode);

        Assert.Single(await host.FactsAsync(FactType.SignedOutEverywhere, user.Id.ToString(), Ct));
    }

    [Fact]
    public async Task SigningIn_LeavesAFactWithTheAccountAsActor()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var fact = Assert.Single(await host.FactsAsync(FactType.Login, user.Id.ToString(), Ct));
        Assert.Equal(user.Id.ToString(), fact.ActorId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(user.Username, ApiTestHost.DataOf(fact).GetProperty("actorDisplayName").GetString());
    }

    [Fact]
    public async Task AFailedSignIn_RecordsTheUsernameAttempted_AndNeverThePassword()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var user = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.None, Ct);

        await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = user.Username, password = "the-wrong-and-distinctive-password" }, Ct);

        // About the account, because the username matched one -- so its history shows the
        // attempts against it.
        var fact = Assert.Single(await host.FactsAsync(FactType.LoginFailed, user.Id.ToString(), Ct));
        Assert.Contains(user.Username, fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("the-wrong-and-distinctive-password", fact.Data, StringComparison.Ordinal);
        Assert.Null(fact.ActorId);

        var unknownName = $"nobody_{Guid.NewGuid():N}";
        await host.Client.PostAsJsonAsync("/api/auth/login", new { username = unknownName, password = "whatever-it-was" }, Ct);

        var unknown = Assert.Single(
            await host.FactsAsync(FactType.LoginFailed, Api.Features.Users.AccountFacts.NoAccount, Ct),
            f => f.Data.Contains(unknownName, StringComparison.Ordinal));
        Assert.DoesNotContain("whatever-it-was", unknown.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedFailures_MakeTheNextAttemptWait_ButNeverLockOut()
    {
        var delay = new RecordingDelay();
        await using var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IDelayScheduler>(delay));
        var user = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.None, Ct);

        for (var i = 0; i < 3; i++)
            await host.Client.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "wrong" }, Ct);

        // 0 before any failure, then 1s, 2s, and 4s before the fourth attempt.
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delay.Waits);

        // The fourth attempt, with the right password, waits and then works: a slowdown, not a
        // lockout (design §7).
        var success = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = user.Username, password = TestAccounts.Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(4), delay.Waits[^1]);

        // And success clears the count.
        await host.Client.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = TestAccounts.Password }, Ct);
        Assert.Equal(3, delay.Waits.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 20)]
    [InlineData(50, 20)]
    public void TheWait_DoublesPerFailure_AndIsCapped(int failures, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), AttemptSlowdown.WaitFor(failures));
}
