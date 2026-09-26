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

        // The save also asks the address what group it is (register details spec 2.2), so
        // "visits" is not necessarily the last call any more -- found by its own URL instead.
        var call = Assert.Single(
            host.Cloud.Calls, c => c.Url == new Uri(MyTestHost.CloudEndpoint, "api/v1/site/visits"));
        Assert.Equal(HttpMethod.Post, call.Method);

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

        // The page also asks the address what group it is, in the background alongside the visit
        // note (register details spec 2.2) -- both awaited here since neither blocks the page.
        await host.Cloud.NextCallAsync(Ct);
        await host.Cloud.NextCallAsync(Ct);

        var call = Assert.Single(
            host.Cloud.Calls, c => c.Url == new Uri(MyTestHost.CloudEndpoint, "api/v1/site/visits"));
        Assert.Equal(Visitor, JsonDocument.Parse(call.Body).RootElement.GetProperty("address").GetString());
        Assert.Equal(2, host.Cloud.Calls.Count);
    }

    /// <summary>
    /// The note is sent after the page, not before it. A Cloud that hangs would otherwise hold every
    /// page load for as long as the call takes to give up.
    /// </summary>
    [Fact]
    public async Task A_page_does_not_wait_for_cloud_to_take_the_note()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Hold = new TaskCompletionSource();

        using var page = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example", Visitor)
            .WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(MyTestHost.RegisterHtml, await page.Content.ReadAsStringAsync(Ct));

        await host.Cloud.NextCallAsync(Ct);
        host.Cloud.Hold.SetResult();
    }

    /// <summary>
    /// A save is only a save once Cloud has it. Anything else is a 503, so the app keeps the address
    /// and sends it again later instead of telling somebody it went through.
    /// </summary>
    [Fact]
    public async Task A_save_cloud_did_not_take_is_a_503()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Unreachable = true;

        using var unreachable = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = "https://modbot.example" }, Visitor);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unreachable.StatusCode);
        Assert.NotNull(unreachable.Headers.RetryAfter);
        Assert.Equal("application/json", unreachable.Content.Headers.ContentType?.MediaType);

        host.Cloud.Unreachable = false;
        host.Cloud.Status = HttpStatusCode.InternalServerError;

        using var refused = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = "https://modbot.example" }, Visitor);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
    }

    [Fact]
    public async Task The_key_goes_to_cloud_and_never_to_the_browser()
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.GetAsync("/api/my-servers", Visitor);
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
            {"items":[{"serverUrl":"https://modbot.example","firstSeenAt":"2026-09-01T00:00:00+00:00",
            "lastSeenAt":"2026-09-16T00:00:00+00:00","visits":3,"groupName":"VRChat Kings",
            "groupIconUrl":"https://api.vrchat.cloud/icon.png"}]}
            """;

        using var response = await host.GetAsync("/api/my-servers", Visitor);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal("https://modbot.example", items[0].GetProperty("serverUrl").GetString());
        Assert.Equal(3, items[0].GetProperty("visits").GetInt32());
        Assert.Equal("VRChat Kings", items[0].GetProperty("groupName").GetString());
    }

    /// <summary>
    /// Compatibility, added 2026-09-26, when "instance" became "server". Cloud named the address
    /// <c>instanceUrl</c> until then and is deployed on its own, so an older Cloud must still be read.
    /// Remove with the fallback in <c>CloudClient</c> once every Cloud sends <c>serverUrl</c>.
    /// </summary>
    [Fact]
    public async Task A_list_from_a_cloud_that_still_says_instanceUrl_is_read()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Body = """
            {"items":[{"instanceUrl":"https://old.example","firstSeenAt":"2026-09-01T00:00:00+00:00",
            "lastSeenAt":"2026-09-16T00:00:00+00:00","visits":2},
            {"serverUrl":"https://new.example","instanceUrl":"https://new.example",
            "firstSeenAt":"2026-09-01T00:00:00+00:00","lastSeenAt":"2026-09-16T00:00:00+00:00","visits":1}]}
            """;

        using var response = await host.GetAsync("/api/my-servers", Visitor);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(
            new string?[] { "https://old.example", "https://new.example" },
            body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("serverUrl").GetString()));
    }

    /// <summary>
    /// Compatibility, added 2026-09-26. A browser still running a page built before then calls the
    /// old route and reads <c>instanceUrl</c>. Remove with the old route and the old field once no
    /// browser can still be running such a page.
    /// </summary>
    [Fact]
    public async Task The_old_route_and_the_old_field_still_answer()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Body = """
            {"items":[{"serverUrl":"https://modbot.example","firstSeenAt":"2026-09-01T00:00:00+00:00",
            "lastSeenAt":"2026-09-16T00:00:00+00:00","visits":3}]}
            """;

        using var response = await host.GetAsync("/api/my-instances", Visitor);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var item = Assert.Single(
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray());
        Assert.Equal("https://modbot.example", item.GetProperty("instanceUrl").GetString());
        Assert.Equal("https://modbot.example", item.GetProperty("serverUrl").GetString());
    }

    [Fact]
    public async Task A_list_with_no_items_in_it_is_a_503()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Body = "{}";

        using var response = await host.GetAsync("/api/my-servers", Visitor);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// An empty list is only the truth when Cloud said so. A Cloud that cannot be asked is a 503, so
    /// the app keeps the list it last had rather than replacing it with nothing.
    /// </summary>
    [Fact]
    public async Task An_unreachable_cloud_is_a_503_not_an_empty_list()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Unreachable = true;

        using var response = await host.GetAsync("/api/my-servers", Visitor);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(body.TryGetProperty("items", out _));
        Assert.DoesNotContain(MyTestHost.ApiKey, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cloud_that_answers_badly_is_a_503_too()
    {
        await using var host = await MyTestHost.StartAsync();

        host.Cloud.Status = HttpStatusCode.BadGateway;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.GetAsync("/api/my-servers", Visitor)).StatusCode);

        host.Cloud.Status = HttpStatusCode.OK;
        host.Cloud.Body = "not json";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.GetAsync("/api/my-servers", Visitor)).StatusCode);
    }

    [Fact]
    public async Task A_page_still_serves_when_cloud_is_unreachable()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Unreachable = true;

        using var page = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example", Visitor);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(MyTestHost.RegisterHtml, await page.Content.ReadAsStringAsync(Ct));
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
            using var accepted = await host.GetAsync("/api/my-servers", Visitor);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var refused = await host.GetAsync("/api/my-servers", Visitor);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task A_limit_lets_go_once_the_hour_is_up()
    {
        await using var host = await MyTestHost.StartAsync();

        for (var i = 0; i < SiteLimits.ReadsPerHour; i++)
            (await host.GetAsync("/api/my-servers", Visitor)).Dispose();

        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.GetAsync("/api/my-servers", Visitor)).StatusCode);

        host.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/my-servers", Visitor)).StatusCode);
    }

    [Fact]
    public async Task With_no_key_configured_nothing_is_asked_of_cloud()
    {
        await using var host = await MyTestHost.StartAsync(apiKey: null);

        using var response = await host.GetAsync("/api/my-servers", Visitor);

        // Cloud was not asked, so there is no list to give; the page's own copy stands.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(host.Cloud.Calls);
    }
}
