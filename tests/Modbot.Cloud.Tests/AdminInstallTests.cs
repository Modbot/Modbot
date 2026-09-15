using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Cloud.Features.AdminInstalls;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class AdminInstallTests(PostgresFixture db)
{
    private const string File = "output_log_2026-09-15_10-00-00.txt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task EveryAdminReadNeedsTheAdminSignIn()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, installBearer) = await host.RegisterAsync();

        string[] paths = ["/api/admin/installs", $"/api/admin/installs/{id}", $"/api/admin/installs/{id}/lines", "/api/admin/lines-per-day", "/api/admin/settings"];

        foreach (var path in paths)
        {
            using var anonymous = await host.GetAsync(path);
            using var install = await host.GetAsync(path, bearer: installBearer);

            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, install.StatusCode);
        }

        using var save = await host.SendAsync(HttpMethod.Put, "/api/admin/settings", new { logLineKeepDays = 1, logEventKeepDays = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, save.StatusCode);
    }

    [Fact]
    public async Task InstallsShowWhatTheyHaveSentAndTheirClock()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterAsync();
        await host.RegisterAsync();

        host.Time.Advance(TimeSpan.FromSeconds(3));
        using (var batch = await host.PostBatchAsync(bearer, new
               {
                   clientVersion = "2026.9.0",
                   sentAt = CloudTestHost.Start,
                   modbotServerId = "server-7",
                   lines = new[] { CloudTestHost.Line(File, 0, "a"), CloudTestHost.Line(File, 5, "b") },
               }))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        }

        var page = await ReadAsync(await host.GetAsync("/api/admin/installs", bearer: CloudTestHost.RootKey));
        Assert.Equal(2, page.GetProperty("total").GetInt32());

        var first = page.GetProperty("items")[0];
        Assert.Equal(id, first.GetProperty("installId").GetGuid());
        Assert.Equal(2, first.GetProperty("linesStored").GetInt64());
        Assert.Equal(3000, first.GetProperty("clockOffsetMs").GetInt64());
        Assert.Equal("server-7", first.GetProperty("modbotServerId").GetString());
        Assert.Equal(CloudTestHost.Start, first.GetProperty("firstSeenAt").GetDateTimeOffset());

        var quiet = page.GetProperty("items")[1];
        Assert.Equal(0, quiet.GetProperty("linesStored").GetInt64());
        Assert.Equal(JsonValueKind.Null, quiet.GetProperty("clockOffsetMs").ValueKind);

        var days = await ReadAsync(await host.GetAsync("/api/admin/lines-per-day?days=7", bearer: CloudTestHost.RootKey));
        var items = days.GetProperty("items");
        Assert.Equal(7, items.GetArrayLength());
        Assert.Equal("2026-09-15", items[6].GetProperty("day").GetString());
        Assert.Equal(2, items[6].GetProperty("lines").GetInt64());
        Assert.Equal(0, items[0].GetProperty("lines").GetInt64());
    }

    [Fact]
    public async Task RecentLinesHideInstanceNoncesAndAreNewestFirst()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterAsync();

        const string location = "wrld_1:12345~private(usr_2)~nonce(abcdef-0123)~region(eu)";
        using (var batch = await host.PostBatchAsync(bearer, host.Batch(
                   CloudTestHost.Line(File, 0, "first"),
                   CloudTestHost.Line(File, 10, $"[Behaviour] Joining {location}"))))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        }

        var body = await ReadAsync(await host.GetAsync($"/api/admin/installs/{id}/lines", bearer: CloudTestHost.RootKey));
        var lines = body.GetProperty("items");

        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("[Behaviour] Joining wrld_1:12345~private(usr_2)~nonce(hidden)~region(eu)", lines[0].GetProperty("text").GetString());
        Assert.Equal(File, lines[0].GetProperty("file").GetString());
        Assert.DoesNotContain("abcdef", body.GetRawText(), StringComparison.Ordinal);

        // The stored line itself is untouched.
        await using var engine = db.NewEngineContext();
        Assert.Contains(engine.LogLines, l => l.Text.Contains("abcdef-0123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetentionCanBeReadAndChanged()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        var defaults = await ReadAsync(await host.GetAsync("/api/admin/settings", bearer: CloudTestHost.RootKey));
        Assert.Equal(90, defaults.GetProperty("logLineKeepDays").GetInt32());
        Assert.Equal(365, defaults.GetProperty("logEventKeepDays").GetInt32());

        using (var saved = await host.SendAsync(HttpMethod.Put, "/api/admin/settings", new { logLineKeepDays = 0, logEventKeepDays = 30 }, bearer: CloudTestHost.RootKey))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var changed = await ReadAsync(await host.GetAsync("/api/admin/settings", bearer: CloudTestHost.RootKey));
        Assert.Equal(0, changed.GetProperty("logLineKeepDays").GetInt32());
        Assert.Equal(30, changed.GetProperty("logEventKeepDays").GetInt32());

        using var refused = await host.SendAsync(HttpMethod.Put, "/api/admin/settings", new { logLineKeepDays = -1, logEventKeepDays = 30 }, bearer: CloudTestHost.RootKey);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public void NoncesAreHiddenWhereverTheyAppear()
    {
        Assert.Equal("a~nonce(hidden) b~nonce(hidden)", AdminInstallEndpoints.HideNonces("a~nonce(x1) b~NONCE(y)"));
        Assert.Equal("nothing here", AdminInstallEndpoints.HideNonces("nothing here"));
    }
}
