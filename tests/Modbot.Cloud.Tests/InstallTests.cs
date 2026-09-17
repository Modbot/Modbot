using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Features.Installs;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class InstallTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegisteringReturnsAnIdAndASecretAndKeepsOnlyTheHash()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/installs", new { companionVersion = "2026.9.0", platform = "windows" }, ip: "203.0.113.1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("installId").GetGuid();
        var secret = body.GetProperty("secret").GetString()!;
        Assert.Equal(43, secret.Length);
        Assert.Equal(CloudTestHost.Start, body.GetProperty("serverTime").GetDateTimeOffset());

        await using var cloud = db.NewCloudContext();
        var install = await cloud.Installs.SingleAsync(i => i.Id == id, Ct);
        Assert.Equal(InstallSecrets.Hash(secret), install.SecretHash);
        Assert.NotEqual(secret, install.SecretHash);
        Assert.Equal("2026.9.0", install.CompanionVersion);
        Assert.Equal("windows", install.Platform);
        Assert.Null(install.ModbotServerId);
    }

    [Fact]
    public async Task RegistrationIsLimitedPerAddress()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        for (var i = 0; i < RegistrationLimit.PerHour; i++)
        {
            using var ok = await host.SendAsync(HttpMethod.Post, "/api/v1/installs", new { }, ip: "203.0.113.2");
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var refused = await host.SendAsync(HttpMethod.Post, "/api/v1/installs", new { }, ip: "203.0.113.2");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(TimeSpan.FromHours(1), refused.Headers.RetryAfter?.Delta);

        // Another address is not held back by the first.
        using var other = await host.SendAsync(HttpMethod.Post, "/api/v1/installs", new { }, ip: "203.0.113.3");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        // And the first may register again once the hour is up.
        host.Time.Advance(TimeSpan.FromHours(1));
        using var later = await host.SendAsync(HttpMethod.Post, "/api/v1/installs", new { }, ip: "203.0.113.2");
        Assert.Equal(HttpStatusCode.Created, later.StatusCode);

        await using var cloud = db.NewCloudContext();
        Assert.Equal(RegistrationLimit.PerHour + 2, await cloud.Installs.CountAsync(Ct));
    }

    [Fact]
    public void TheBearerValueIsTheIdAndTheSecret()
    {
        var id = Guid.NewGuid();

        Assert.True(InstallSecrets.TryRead($"Bearer {id}.abc", out var readId, out var secret));
        Assert.Equal(id, readId);
        Assert.Equal("abc", secret);

        Assert.False(InstallSecrets.TryRead($"Bearer {id}", out _, out _));
        Assert.False(InstallSecrets.TryRead("Bearer not-a-guid.abc", out _, out _));
        Assert.False(InstallSecrets.TryRead($"Basic {id}.abc", out _, out _));
        Assert.False(InstallSecrets.TryRead(null, out _, out _));
    }
}
