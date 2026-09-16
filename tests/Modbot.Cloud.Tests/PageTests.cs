using System.Net;

namespace Modbot.Cloud.Tests;

/// <summary>
/// Only the app's own routes serve it. Everything else is a 404, and under /api a JSON one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PageTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/admin")]
    [InlineData("/admin/installs")]
    [InlineData("/register")]
    [InlineData("/sign-in")]
    [InlineData("/account")]
    [InlineData("/forgot-password")]
    public async Task EachAppRouteServesTheAppOnFirstLoad(string path)
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(CloudTestHost.AppHtml, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The two addresses the links in Cloud's mail land on. They have to serve the app on a first
    /// load, from a mail client, with no session and nothing cached.
    /// </summary>
    [Theory]
    [InlineData("/verify?token=abc")]
    [InlineData("/verify-email-change?token=abc")]
    [InlineData("/reset-password?token=abc")]
    public async Task EveryLinkInCloudMailServesTheApp(string path)
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/register/extra")]
    [InlineData("/nothing-here")]
    [InlineData("/missing.js")]
    public async Task EveryOtherPathIsNotFound(string path)
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
