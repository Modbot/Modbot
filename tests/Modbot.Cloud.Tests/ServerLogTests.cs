using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.ServerLogs;
using Modbot.Cloud.Features.Registry;

namespace Modbot.Cloud.Tests;

/// <summary>
/// The server log feed: a Modbot deployment sending Cloud its own log.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ServerLogTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Written = CloudTestHost.Start.AddMinutes(-2);

    [Fact]
    public async Task LinesAreStoredWithBothTimesAndTheDeploymentTheyCameFrom()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        using var response = await host.PostLogsAsync(bearer, host.LogBatch(
            CloudTestHost.LogLine(Written, "Stored 42 facts"),
            CloudTestHost.LogLine(Written.AddSeconds(1), "Sync failed", level: "Error", exception: "System.TimeoutException")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var engine = db.NewEngineContext();
        var stored = await engine.ServerLogs.OrderBy(l => l.At).ToListAsync(Ct);

        Assert.Equal(2, stored.Count);
        Assert.Equal(id, stored[0].ServerId);
        Assert.Equal(Written, stored[0].At);
        Assert.Equal(CloudTestHost.Start, stored[0].ReceivedAt);
        Assert.Equal("Information", stored[0].Level);
        Assert.Equal("Stored 42 facts", stored[0].Message);
        Assert.Equal("Modbot", stored[0].Service);
        Assert.Equal("2026.9.0", stored[0].Version);

        using var properties = JsonDocument.Parse(stored[0].Properties);
        Assert.Equal(42, properties.RootElement.GetProperty("Count").GetInt32());

        Assert.Equal("Error", stored[1].Level);
        Assert.Equal("System.TimeoutException", stored[1].Exception);
    }

    [Fact]
    public async Task ABatchWithoutAKnownServerIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        using var none = await host.PostLogsAsync(null, host.LogBatch(CloudTestHost.LogLine(Written)));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        using var wrongSecret = await host.PostLogsAsync($"{id}.not-the-secret", host.LogBatch(CloudTestHost.LogLine(Written)));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);

        using var unknownId = await host.PostLogsAsync(
            $"{Guid.NewGuid()}.{bearer.Split('.')[1]}", host.LogBatch(CloudTestHost.LogLine(Written)));
        Assert.Equal(HttpStatusCode.Unauthorized, unknownId.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal(0, await engine.ServerLogs.CountAsync(Ct));
    }

    [Fact]
    public async Task ALevelCloudDoesNotKnowIsStoredAsInformationRatherThanLost()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        using var response = await host.PostLogsAsync(bearer, host.LogBatch(
            CloudTestHost.LogLine(Written, level: "Screaming")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal("Information", (await engine.ServerLogs.SingleAsync(Ct)).Level);
    }

    [Fact]
    public async Task APropertyDocumentThatIsNotAnObjectBecomesAnEmptyOne()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        using var response = await host.PostLogsAsync(bearer, host.LogBatch(
            CloudTestHost.LogLine(Written, properties: new[] { 1, 2, 3 })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal("{}", (await engine.ServerLogs.SingleAsync(Ct)).Properties);
    }

    [Fact]
    public async Task ABatchWithNoLinesIsRefusedAndStoresNothing()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        using var response = await host.PostLogsAsync(bearer, host.LogBatch());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TooManyLinesInOneBatchIsRefusedRatherThanCut()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        var lines = Enumerable.Range(0, ServerLogLimits.MaxLinesPerBatch + 1)
            .Select(i => CloudTestHost.LogLine(Written.AddSeconds(i), $"line {i}"))
            .ToArray<object>();

        using var response = await host.PostLogsAsync(bearer, host.LogBatch(lines));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal(0, await engine.ServerLogs.CountAsync(Ct));
    }

    [Fact]
    public async Task SendingLogsKeepsTheRegistrysLastSeenCurrent()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        host.Time.Advance(TimeSpan.FromMinutes(5));

        using var response = await host.PostLogsAsync(bearer, host.LogBatch(CloudTestHost.LogLine(Written)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var cloud = db.NewCloudContext();
        var server = await cloud.RegisteredServers.SingleAsync(s => s.Id == id, Ct);

        // A log batch is a better sign of life than a report every six hours.
        Assert.Equal(CloudTestHost.Start.AddMinutes(5), server.LastSeenAt);
    }

    [Fact]
    public async Task TheAdminViewerReadsTheLinesAndFiltersThem()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        using (var sent = await host.PostLogsAsync(bearer, host.LogBatch(
            CloudTestHost.LogLine(Written, "a quiet pass"),
            CloudTestHost.LogLine(Written.AddSeconds(1), "something broke", level: "Error"))))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using var all = await host.SendAsync(HttpMethod.Get, "/api/admin/logs", bearer: CloudTestHost.RootKey);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);

        var page = await all.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());

        using var errors = await host.SendAsync(HttpMethod.Get, "/api/admin/logs?level=Error", bearer: CloudTestHost.RootKey);
        var errorPage = await errors.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, errorPage.GetProperty("items").GetArrayLength());
        Assert.Equal("something broke", errorPage.GetProperty("items")[0].GetProperty("message").GetString());

        using var searched = await host.SendAsync(HttpMethod.Get, "/api/admin/logs?text=quiet", bearer: CloudTestHost.RootKey);
        var searchPage = await searched.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("a quiet pass", searchPage.GetProperty("items")[0].GetProperty("message").GetString());

        using var senders = await host.SendAsync(HttpMethod.Get, "/api/admin/logs/senders", bearer: CloudTestHost.RootKey);
        var senderPage = await senders.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(id, senderPage.GetProperty("items")[0].GetProperty("serverId").GetGuid());
    }

    [Fact]
    public async Task ReadingEverybodysLogsNeedsTheAdminSignIn()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        using var anonymous = await host.GetAsync("/api/admin/logs");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // A server's own secret opens the ingest endpoint and nothing else.
        using var asServer = await host.GetAsync("/api/admin/logs", bearer);
        Assert.Equal(HttpStatusCode.Unauthorized, asServer.StatusCode);
    }

    [Fact]
    public async Task TheAccountThatClaimedAServerReadsItsOwnLogs()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        using (var sent = await host.PostLogsAsync(bearer, host.LogBatch(CloudTestHost.LogLine(Written, "mine"))))
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        var cookie = await SignUpAsync(host, "owner@example.com");
        await ClaimAsync(host, cookie, bearer);

        using var response = await host.SendAsync(
            HttpMethod.Get, $"/api/admin/logs?serverId={id}", cookie: cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("mine", page.GetProperty("items")[0].GetProperty("message").GetString());

        // And their picker lists only what they claimed.
        using var senders = await host.SendAsync(HttpMethod.Get, "/api/admin/logs/senders", cookie: cookie);
        var senderPage = await senders.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(id, Assert.Single(senderPage.GetProperty("items").EnumerateArray()).GetProperty("serverId").GetGuid());
    }

    [Fact]
    public async Task AnAccountCannotReadAServerItHasNotClaimed()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterServerAsync();

        using (var sent = await host.PostLogsAsync(bearer, host.LogBatch(CloudTestHost.LogLine(Written))))
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        var cookie = await SignUpAsync(host, "stranger@example.com");

        using var named = await host.SendAsync(
            HttpMethod.Get, $"/api/admin/logs?serverId={id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, named.StatusCode);

        // And there is no "everybody's logs" for an account, only for an administrator.
        using var everything = await host.SendAsync(HttpMethod.Get, "/api/admin/logs", cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, everything.StatusCode);
    }

    [Fact]
    public async Task RetentionDropsWholeMonthsPastTheWindowAndLeavesTheRest()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        using (var sent = await host.PostLogsAsync(bearer, host.LogBatch(CloudTestHost.LogLine(Written))))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var saved = await host.SendAsync(
            HttpMethod.Put, "/api/admin/settings", new { eventKeepDays = 365, logKeepDays = 30 }, bearer: CloudTestHost.RootKey))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        // Two months on, the month those lines are in is entirely past a thirty-day window.
        host.Time.Advance(TimeSpan.FromDays(70));

        using var scope = host.Services.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<LogRetention>();
        var dropped = await retention.RunAsync(Ct);

        Assert.Contains("server_log_2026_09", dropped);

        await using var engine = db.NewEngineContext();
        Assert.Equal(0, await engine.ServerLogs.CountAsync(Ct));
    }

    [Fact]
    public async Task RetentionStillDropsAMonthLeftUnderTheOldName()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        // A month the rename missed: still called instance_log_…, but a partition of server_log.
        // May is outside the months the maintainer makes, so it does not clash with them.
        await using (var engine = db.NewEngineContext())
        {
            await engine.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS instance_log_2026_05", Ct);
            await engine.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE instance_log_2026_05 PARTITION OF server_log
                    FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00')
                """,
                Ct);
        }

        using (var saved = await host.SendAsync(
            HttpMethod.Put, "/api/admin/settings", new { eventKeepDays = 365, logKeepDays = 30 }, bearer: CloudTestHost.RootKey))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using var scope = host.Services.CreateScope();
        var dropped = await scope.ServiceProvider.GetRequiredService<LogRetention>().RunAsync(Ct);

        Assert.Contains("instance_log_2026_05", dropped);

        await using var after = db.NewEngineContext();
        var left = await after.Database
            .SqlQuery<bool>($"SELECT to_regclass('instance_log_2026_05') IS NOT NULL AS \"Value\"")
            .SingleAsync(Ct);
        Assert.False(left);
    }

    [Fact]
    public async Task KeepingForeverDropsNothing()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        using (var sent = await host.PostLogsAsync(bearer, host.LogBatch(CloudTestHost.LogLine(Written))))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var saved = await host.SendAsync(
            HttpMethod.Put, "/api/admin/settings", new { eventKeepDays = 365, logKeepDays = 0 }, bearer: CloudTestHost.RootKey))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        host.Time.Advance(TimeSpan.FromDays(3650));

        using var scope = host.Services.CreateScope();
        var dropped = await scope.ServiceProvider.GetRequiredService<LogRetention>().RunAsync(Ct);

        Assert.Empty(dropped);

        await using var engine = db.NewEngineContext();
        Assert.Equal(1, await engine.ServerLogs.CountAsync(Ct));
    }

    /// <summary>Signs up, verifies and signs in, and returns the session cookie.</summary>
    private static async Task<string> SignUpAsync(CloudTestHost host, string email)
    {
        const string password = "a-long-enough-password";

        await host.SendAsync(HttpMethod.Post, "/api/v1/accounts", new { email, password });
        await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/verify", new { token = host.Mail.LastToken() });

        using var signedIn = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/session", new { email, password });

        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);

        var setCookie = signedIn.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith(AccountSessions.CookieName, StringComparison.Ordinal));

        return setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>Claims <paramref name="bearer"/>'s server for the account behind the cookie.</summary>
    private static async Task ClaimAsync(CloudTestHost host, string cookie, string bearer)
    {
        var code = ServerSecrets.NewLinkCode();

        using (var shown = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/link-code", new { codeHash = ServerSecrets.Hash(code) }, bearer: bearer))
        {
            Assert.Equal(HttpStatusCode.NoContent, shown.StatusCode);
        }

        using var claimed = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/claim", new { code }, cookie: cookie);

        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
    }
}
