using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class AdminInstallTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Happened = new(2026, 9, 15, 11, 0, 0, TimeSpan.Zero);

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

        string[] paths = ["/api/admin/installs", $"/api/admin/installs/{id}", $"/api/admin/installs/{id}/events", "/api/admin/events-per-day", "/api/admin/settings"];

        foreach (var path in paths)
        {
            using var anonymous = await host.GetAsync(path);
            using var install = await host.GetAsync(path, bearer: installBearer);

            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, install.StatusCode);
        }

        using var save = await host.SendAsync(HttpMethod.Put, "/api/admin/settings", new { eventKeepDays = 1 });
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
                   events = new[] { CloudTestHost.Event("a", Happened), CloudTestHost.Event("b", Happened) },
               }))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        }

        var page = await ReadAsync(await host.GetAsync("/api/admin/installs", bearer: CloudTestHost.RootKey));
        Assert.Equal(2, page.GetProperty("total").GetInt32());

        var first = page.GetProperty("items")[0];
        Assert.Equal(id, first.GetProperty("installId").GetGuid());
        Assert.Equal(2, first.GetProperty("eventsStored").GetInt64());
        Assert.Equal(3000, first.GetProperty("clockOffsetMs").GetInt64());
        Assert.Equal("server-7", first.GetProperty("modbotServerId").GetString());

        var quiet = page.GetProperty("items")[1];
        Assert.Equal(0, quiet.GetProperty("eventsStored").GetInt64());
        Assert.Equal(JsonValueKind.Null, quiet.GetProperty("clockOffsetMs").ValueKind);

        var days = await ReadAsync(await host.GetAsync("/api/admin/events-per-day?days=7", bearer: CloudTestHost.RootKey));
        var items = days.GetProperty("items");
        Assert.Equal(7, items.GetArrayLength());
        Assert.Equal("2026-09-15", items[6].GetProperty("day").GetString());
        Assert.Equal(2, items[6].GetProperty("events").GetInt64());
        Assert.Equal(0, items[0].GetProperty("events").GetInt64());
    }

    [Fact]
    public async Task RecentEventsArePlainTextNewestFirst()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterAsync();

        using (var batch = await host.PostBatchAsync(bearer, host.Batch(
                   CloudTestHost.Event("a", Happened, instanceId: "12345~private(usr_2)", groupId: null))))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        }

        host.Time.Advance(TimeSpan.FromMinutes(1));
        using (var batch = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event("b", Happened, type: "InstanceLeft", groupId: "grp_1"))))
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);

        var body = await ReadAsync(await host.GetAsync($"/api/admin/installs/{id}/events", bearer: CloudTestHost.RootKey));
        var events = body.GetProperty("items");

        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal("b", events[0].GetProperty("clientEventId").GetString());
        Assert.Equal("vrchat.instance.leave", events[0].GetProperty("type").GetString());
        Assert.Equal("Rin", events[1].GetProperty("displayName").GetString());
        Assert.Equal("wrld_1", events[1].GetProperty("worldId").GetString());
        Assert.Equal("12345~private(usr_2)", events[1].GetProperty("instanceId").GetString());
        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("groupId").ValueKind);
        Assert.DoesNotContain("vrchat://", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionCanBeReadAndChanged()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        var defaults = await ReadAsync(await host.GetAsync("/api/admin/settings", bearer: CloudTestHost.RootKey));
        Assert.Equal(365, defaults.GetProperty("eventKeepDays").GetInt32());

        using (var saved = await host.SendAsync(HttpMethod.Put, "/api/admin/settings", new { eventKeepDays = 0 }, bearer: CloudTestHost.RootKey))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var changed = await ReadAsync(await host.GetAsync("/api/admin/settings", bearer: CloudTestHost.RootKey));
        Assert.Equal(0, changed.GetProperty("eventKeepDays").GetInt32());

        using var refused = await host.SendAsync(HttpMethod.Put, "/api/admin/settings", new { eventKeepDays = -1 }, bearer: CloudTestHost.RootKey);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
