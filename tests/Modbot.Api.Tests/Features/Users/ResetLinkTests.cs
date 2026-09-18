using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Users;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Email;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>Reset links (accounts and access design §4.1) and forgot-password (§4.2).</summary>
[Collection(nameof(PostgresCollection))]
public class ResetLinkTests
{
    private readonly PostgresFixture _db;

    public ResetLinkTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ApiPath(string path) => "/api" + path;

    /// <summary>
    /// A relay that captures what would have been emailed and says email is set up. It sits under
    /// the real email sender, so these messages pass through the daily email limit like any other.
    /// </summary>
    private static async Task<FakeMailRelay> CapturingEmailAsync(PostgresFixture db)
    {
        await ApiTestHost.ClearEmailQueueAsync(db, Ct);
        return new FakeMailRelay();
    }

    private static async Task SetPublicAddressAsync(PostgresFixture db, string? address)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.PublicAddress = address;
        await context.SaveChangesAsync(Ct);
    }

    private static async Task SetEmailAsync(PostgresFixture db, Guid userId, string email)
    {
        await using var context = db.NewContext();
        var user = await context.Users.FindAsync([userId], Ct);
        user!.Email = email;
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AnAdministratorsResetLink_SetsANewPassword_EndsOldSessions_AndWorksOnce()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var (user, oldSession) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var created = await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{user.Id}/reset-link", null, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var path = (await ApiTestHost.BodyOf(created, Ct)).GetProperty("path").GetString()!;
        Assert.StartsWith("/reset/", path, StringComparison.Ordinal);

        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync(ApiPath(path), Ct), Ct);
        Assert.True(described.GetProperty("usable").GetBoolean());
        Assert.Equal(user.Username, described.GetProperty("username").GetString());

        host.Clock.Advance(TimeSpan.FromSeconds(5));

        var used = await host.SendJsonAsync(
            HttpMethod.Post, ApiPath(path),
            new { password = "a-brand-new-password", confirmPassword = "a-brand-new-password" },
            null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, used.StatusCode);

        // The session that existed before the reset is dead: the reset may be happening exactly
        // because that session was not theirs.
        var dead = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, oldSession, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, dead.StatusCode);

        Assert.NotEmpty(await host.LoginAsync(user.Username, "a-brand-new-password", Ct));

        var again = await host.SendJsonAsync(
            HttpMethod.Post, ApiPath(path),
            new { password = "yet-another-password!" },
            null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        Assert.Single(await host.FactsAsync(FactType.ResetLinkCreated, user.Id.ToString(), Ct));
        Assert.Single(await host.FactsAsync(FactType.ResetLinkUsed, user.Id.ToString(), Ct));
    }

    [Fact]
    public async Task AnExpiredResetLink_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var created = await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{user.Id}/reset-link", null, admin, Ct);
        var path = (await ApiTestHost.BodyOf(created, Ct)).GetProperty("path").GetString()!;

        host.Clock.Advance(OneTimeLinkService.ResetLifetime + TimeSpan.FromMinutes(1));

        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync(ApiPath(path), Ct), Ct);
        Assert.False(described.GetProperty("usable").GetBoolean());
    }

    [Fact]
    public async Task ANewResetLink_ReplacesTheOldOne()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var first = (await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{user.Id}/reset-link", null, admin, Ct), Ct))
            .GetProperty("path").GetString()!;
        await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{user.Id}/reset-link", null, admin, Ct);

        var described = await ApiTestHost.BodyOf(await host.Client.GetAsync(ApiPath(first), Ct), Ct);
        Assert.False(described.GetProperty("usable").GetBoolean());
    }

    [Fact]
    public async Task ForgotPassword_AnswersTheSameForARealAndAnUnknownUsername()
    {
        var email = await CapturingEmailAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IMailRelay>(email));
        await SetPublicAddressAsync(_db, "https://modbot.example.com");
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var userEmail = $"mod-{user.Id:N}@example.com";
        await SetEmailAsync(_db, user.Id, userEmail);

        var real = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = user.Username }, null, Ct);
        var unknown = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = $"nobody_{Guid.NewGuid():N}" }, null, Ct);

        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Equal(real.StatusCode, unknown.StatusCode);
        Assert.Equal(await real.Content.ReadAsStringAsync(Ct), await unknown.Content.ReadAsStringAsync(Ct));

        // And only the real one got a message -- which the caller cannot see.
        var message = Assert.Single(email.Sent);
        Assert.Equal(userEmail, message.To);
        Assert.Contains("https://modbot.example.com/reset/", message.Body, StringComparison.Ordinal);
        Assert.Equal(EmailKind.Account, message.Kind);
    }

    [Fact]
    public async Task ForgotPassword_BuildsTheLinkFromThePublicAddress_NotFromForgedHeaders()
    {
        var email = await CapturingEmailAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IMailRelay>(email));
        await SetPublicAddressAsync(_db, "https://modbot.example.com");
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        await SetEmailAsync(_db, user.Id, "victim@example.com");

        // The attack: anyone can ask for a reset for a victim's username and forge the host the
        // request appears to be for. If the link were built from the request, the victim's
        // genuine email would point at the attacker's server, which would collect the token.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/forgot-password")
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { username = user.Username }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Headers.Host = "evil.example";

        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var message = Assert.Single(email.Sent);
        Assert.Contains("https://modbot.example.com/reset/", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgotPassword_SendsNothingWithoutAPublicAddress_AndTheSignInPageIsToldWhy()
    {
        var email = await CapturingEmailAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IMailRelay>(email));
        await SetPublicAddressAsync(_db, null);
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        await SetEmailAsync(_db, user.Id, $"mod-{user.Id:N}@example.com");

        var ways = await ApiTestHost.BodyOf(await host.Client.GetAsync("/api/auth/forgot-password", Ct), Ct);
        Assert.False(ways.GetProperty("available").GetBoolean());
        Assert.Contains("public address", ways.GetProperty("reason").GetString(), StringComparison.Ordinal);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = user.Username }, null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task ForgotPassword_SendsAtMostOneLinkPerAccountPerTenMinutes()
    {
        var email = await CapturingEmailAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IMailRelay>(email));
        await SetPublicAddressAsync(_db, "https://modbot.example.com");
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        await SetEmailAsync(_db, user.Id, $"mod-{user.Id:N}@example.com");

        await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = user.Username }, null, Ct);
        await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = user.Username }, null, Ct);
        Assert.Single(email.Sent);

        host.Clock.Advance(ResetEndpoints.SelfRequestGap + TimeSpan.FromSeconds(1));

        await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = user.Username }, null, Ct);
        Assert.Equal(2, email.Sent.Count);
    }

    [Fact]
    public async Task ForgotPassword_TheEmailedLinkWorks()
    {
        var email = await CapturingEmailAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IMailRelay>(email));
        await SetPublicAddressAsync(_db, "https://modbot.example.com");
        var (user, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        await SetEmailAsync(_db, user.Id, $"mod-{user.Id:N}@example.com");

        await host.SendJsonAsync(HttpMethod.Post, "/api/auth/forgot-password", new { username = user.Username }, null, Ct);

        var body = Assert.Single(email.Sent).Body;
        var start = body.IndexOf("https://modbot.example.com/reset/", StringComparison.Ordinal);
        var token = body[(start + "https://modbot.example.com/reset/".Length)..].Split('\n')[0].Trim();

        var used = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/reset/{token}",
            new { password = "a-brand-new-password" },
            null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, used.StatusCode);
        Assert.NotEmpty(await host.LoginAsync(user.Username, "a-brand-new-password", Ct));

        var fact = Assert.Single(await host.FactsAsync(FactType.ResetLinkCreated, user.Id.ToString(), Ct));
        var data = ApiTestHost.DataOf(fact);
        Assert.Equal("self", data.GetProperty("requestedBy").GetString());
        Assert.Equal("email", data.GetProperty("sentVia").GetString());
    }
}
