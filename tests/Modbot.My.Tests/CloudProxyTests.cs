using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.My.Common;

namespace Modbot.My.Tests;

/// <summary>
/// my.modbot.co keeps nothing and reads everything from Modbot Cloud
/// (central services spec 2.1.1).
/// </summary>
public class CloudProxyTests
{
    private const string Visitor = "198.51.100.4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saving_an_address_sends_it_to_cloud_with_the_visitor_address()
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = "https://modbot.example/settings" }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var call = host.Cloud.Last;
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(new Uri(MyTestHost.CloudEndpoint, "api/v1/site/visits"), call.Url);

        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal(Visitor, body.GetProperty("address").GetString());
        Assert.Equal("https://modbot.example", body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Serving_a_page_with_an_address_notes_it_through_cloud()
    {
        await using var host = await MyTestHost.StartAsync();

        using var page = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example", Visitor);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        Assert.Single(host.Cloud.Calls);
        Assert.Equal(new Uri(MyTestHost.CloudEndpoint, "api/v1/site/visits"), host.Cloud.Last.Url);
    }

    [Fact]
    public async Task The_key_goes_to_cloud_and_never_to_the_browser()
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.GetAsync("/api/my-instances", Visitor);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal($"Bearer {MyTestHost.ApiKey}", host.Cloud.Last.Authorization);
        Assert.DoesNotContain(MyTestHost.ApiKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", response.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_list_comes_back_as_cloud_sent_it()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Body = """
            {"items":[{"instanceUrl":"https://modbot.example","firstSeenAt":"2026-09-01T00:00:00+00:00",
            "lastSeenAt":"2026-09-16T00:00:00+00:00","visits":3,"groupName":"VRChat Kings",
            "groupIconUrl":"https://api.vrchat.cloud/icon.png"}]}
            """;

        using var response = await host.GetAsync("/api/my-instances", Visitor);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal("https://modbot.example", items[0].GetProperty("instanceUrl").GetString());
        Assert.Equal("VRChat Kings", items[0].GetProperty("groupName").GetString());
    }

    [Fact]
    public async Task An_unreachable_cloud_is_an_empty_list_not_an_error()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Unreachable = true;

        using var response = await host.GetAsync("/api/my-instances", Visitor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_page_still_serves_when_cloud_is_unreachable()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Unreachable = true;

        using var page = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example", Visitor);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(MyTestHost.AppHtml, await page.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_http_address_is_refused_before_cloud_is_called()
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = "http://modbot.example" }, Visitor);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Cloud.Calls);
    }

    [Fact]
    public async Task Too_many_saves_from_one_address_are_refused()
    {
        await using var host = await MyTestHost.StartAsync();

        for (var i = 0; i < SiteLimits.SavesPerHour; i++)
        {
            using var accepted = await host.SendAsync(
                HttpMethod.Post, "/api/local-register", new { url = $"https://modbot{i}.example" }, Visitor);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        using var refused = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = "https://one-more.example" }, Visitor);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);

        // Another address is unaffected.
        using var other = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = "https://modbot.example" }, "203.0.113.7");
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    [Fact]
    public async Task Too_many_reads_from_one_address_are_refused()
    {
        await using var host = await MyTestHost.StartAsync();

        for (var i = 0; i < SiteLimits.ReadsPerHour; i++)
        {
            using var accepted = await host.GetAsync("/api/my-instances", Visitor);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var refused = await host.GetAsync("/api/my-instances", Visitor);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task A_limit_lets_go_once_the_hour_is_up()
    {
        await using var host = await MyTestHost.StartAsync();

        for (var i = 0; i < SiteLimits.ReadsPerHour; i++)
            (await host.GetAsync("/api/my-instances", Visitor)).Dispose();

        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.GetAsync("/api/my-instances", Visitor)).StatusCode);

        host.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/my-instances", Visitor)).StatusCode);
    }

    [Fact]
    public async Task With_no_key_configured_nothing_is_asked_of_cloud()
    {
        await using var host = await MyTestHost.StartAsync(apiKey: null);

        using var response = await host.GetAsync("/api/my-instances", Visitor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(host.Cloud.Calls);
    }
}
