using System.Net;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class AdminAndHealthTests(PostgresFixture db)
{
    [Fact]
    public async Task AdminNeedsTheRootKey()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using (var anonymous = await host.GetAsync("/api/admin/session"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using (var wrong = await host.GetAsync("/api/admin/session", bearer: "not-the-key"))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using (var bearer = await host.GetAsync("/api/admin/session", bearer: CloudTestHost.RootKey))
            Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);

        using var login = await host.SendAsync(HttpMethod.Post, "/api/admin/login", new { key = CloudTestHost.RootKey }, ip: "198.51.100.1");
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];

        using (var session = await host.SendAsync(HttpMethod.Get, "/api/admin/session", cookie: cookie))
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        // An install's credential opens nothing in admin.
        var (_, installBearer) = await host.RegisterAsync();
        using (var install = await host.GetAsync("/api/admin/session", bearer: installBearer))
            Assert.Equal(HttpStatusCode.Unauthorized, install.StatusCode);
    }

    [Fact]
    public async Task AdminIsClosedWithNoRootKeySet()
    {
        await using var host = await CloudTestHost.StartAsync(db, rootApiKey: null);

        using var login = await host.SendAsync(HttpMethod.Post, "/api/admin/login", new { key = "" }, ip: "198.51.100.2");
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task ReadyChecksBothDatabases()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var live = await host.GetAsync("/health/live");
        using var ready = await host.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task PagesServeTheAppAndEverythingElseIsNotFound()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var admin = await host.GetAsync("/admin/installs");
        using var missing = await host.GetAsync("/anything");
        using var missingApi = await host.GetAsync("/api/nothing");

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
    }
}
