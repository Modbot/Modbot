using System.Net;
using System.Text.Json;
using Modbot.My.Features.Visits;

namespace Modbot.My.Tests;

/// <summary>
/// What the register page is told, and what my.modbot.co believes
/// (register details spec 2).
/// </summary>
/// <remarks>
/// The whole of the rule under test: the group in a register link is a hint for the page, and the
/// only thing ever saved is what the Modbot address itself answered.
/// </remarks>
public class RegisterDetailsTests
{
    private const string Visitor = "198.51.100.4";

    private const string Server = "https://modbot.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_register_link_with_a_group_serves_the_page_and_asks_the_address_itself()
    {
        await using var host = await MyTestHost.StartAsync();

        using var page = await host.GetAsync(
            "/register?url=https%3A%2F%2Fmodbot.example&groupId=grp_1&name=VRChat%20Kings"
            + "&icon=https%3A%2F%2Fapi.vrchat.cloud%2Ficon.png&banner=https%3A%2F%2Fapi.vrchat.cloud%2Fbanner.png",
            Visitor);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(MyTestHost.RegisterHtml, await page.Content.ReadAsStringAsync(Ct));

        var asked = await host.Server.NextAskAsync(Ct);
        Assert.Equal(new Uri($"{Server}/api/server"), asked);
    }

    [Fact]
    public async Task A_register_link_with_no_group_behaves_exactly_as_it_did()
    {
        await using var host = await MyTestHost.StartAsync();

        using var page = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example", Visitor);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var visit = await host.Cloud.NextCallAsync(Ct);
        Assert.Equal(new Uri(MyTestHost.CloudEndpoint, "api/v1/site/visits"), visit.Url);
        Assert.Equal(Server, JsonDocument.Parse(visit.Body).RootElement.GetProperty("url").GetString());
    }

    /// <summary>
    /// The one that matters. Anybody can write a link; only the address itself can say what it is.
    /// </summary>
    [Fact]
    public async Task A_forged_group_in_the_link_is_overridden_by_what_the_address_says()
    {
        await using var host = await MyTestHost.StartAsync();

        using var page = await host.GetAsync(
            "/register?url=https%3A%2F%2Fmodbot.example&groupId=grp_forged&name=Somebody%20Else"
            + "&icon=https%3A%2F%2Fnot-the-group.example%2Ficon.png",
            Visitor);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        await host.Server.NextAskAsync(Ct);

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var details = Details(host);
        Assert.Equal("VRChat Kings", details.GetProperty("groupName").GetString());
        Assert.Equal("grp_real", details.GetProperty("groupId").GetString());
        Assert.Equal("https://api.vrchat.cloud/real-icon.png", details.GetProperty("groupIconUrl").GetString());
        Assert.Equal("owner@example.com", details.GetProperty("ownerEmail").GetString());

        // Nothing a link could have said is anywhere in anything that was sent.
        foreach (var call in host.Cloud.Calls)
        {
            Assert.DoesNotContain("Somebody Else", call.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("grp_forged", call.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("not-the-group.example", call.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_address_that_does_not_answer_is_saved_on_its_own()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Server.Unreachable = true;

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        // The visit, and nothing about a group.
        Assert.Single(host.Cloud.Calls);
        Assert.Equal(new Uri(MyTestHost.CloudEndpoint, "api/v1/site/visits"), host.Cloud.Last.Url);
    }

    [Fact]
    public async Task An_address_that_answers_with_nonsense_is_saved_on_its_own()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Server.Body = "<html>not a Modbot</html>";

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Single(host.Cloud.Calls);

        // And a 404 from something that is not a Modbot at all.
        host.Server.Status = HttpStatusCode.NotFound;
        host.Server.Body = """{"name":"Nothing"}""";
        host.Time.Advance(ServerLookup.Remember + TimeSpan.FromMinutes(1));

        using var again = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.DoesNotContain(host.Cloud.Calls, c => c.Url.AbsolutePath.EndsWith("/servers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_answer_that_says_nothing_about_a_group_sends_nothing()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Server.Body = """{"version":"2026.9.0","publicAddress":"https://modbot.example"}""";

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Single(host.Cloud.Calls);
    }

    [Fact]
    public async Task Only_an_https_picture_is_passed_on()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Server.Body =
            """{"name":"VRChat Kings","iconUrl":"http://modbot.example/icon.png","bannerUrl":"javascript:alert(1)"}""";

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var details = Details(host);
        Assert.Equal("VRChat Kings", details.GetProperty("groupName").GetString());
        Assert.Null(details.GetProperty("groupIconUrl").GetString());
        Assert.Null(details.GetProperty("groupBannerUrl").GetString());
    }

    [Fact]
    public async Task An_address_that_says_nothing_readable_about_its_owner_sends_no_address()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Server.Body = """{"name":"VRChat Kings","ownerEmail":"not-an-address"}""";

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Null(Details(host).GetProperty("ownerEmail").GetString());
    }

    /// <summary>
    /// One page view saves twice and must count once (central services spec 2.3.1). Asking the
    /// address adds no visit to either save, and asks once.
    /// </summary>
    [Fact]
    public async Task Asking_the_address_adds_no_visit_and_happens_once_per_page_view()
    {
        await using var host = await MyTestHost.StartAsync();

        using var page = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example", Visitor);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        await host.Server.NextAskAsync(Ct);

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        // The answer is remembered, so the app's own save reads it rather than asking again.
        Assert.Single(host.Server.Asked);

        var visits = host.Cloud.Calls
            .Count(c => c.Url.AbsolutePath.EndsWith("/visits", StringComparison.Ordinal));

        // Two saves of one page view, which Cloud counts as one visit. Never three.
        Assert.Equal(2, visits);
    }

    [Fact]
    public async Task An_address_is_asked_again_once_the_answer_is_old()
    {
        await using var host = await MyTestHost.StartAsync();

        using var first = await host.SendAsync(HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Single(host.Server.Asked);

        host.Time.Advance(ServerLookup.Remember + TimeSpan.FromMinutes(1));

        using var later = await host.SendAsync(HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);
        Assert.Equal(HttpStatusCode.NoContent, later.StatusCode);
        Assert.Equal(2, host.Server.Asked.Count);
    }

    [Fact]
    public async Task A_save_still_answers_when_the_address_hangs_up_but_cloud_took_the_visit()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Server.Unreachable = true;

        using var saved = await host.SendAsync(
            HttpMethod.Post, "/api/local-register", new { url = Server }, Visitor);

        // Asking is never a reason to fail a save.
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
    }

    [Fact]
    public async Task A_page_is_served_even_when_the_address_hangs()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Server.Unreachable = true;

        using var page = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example", Visitor)
            .WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(MyTestHost.RegisterHtml, await page.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The newest call that carried what the address said about itself.</summary>
    private static JsonElement Details(MyTestHost host)
    {
        var calls = host.Cloud.Calls
            .Where(c => c.Url.AbsolutePath.EndsWith("/servers", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(calls);
        Assert.Equal(new Uri(MyTestHost.CloudEndpoint, "api/v1/site/servers"), calls[^1].Url);
        Assert.Equal($"Bearer {MyTestHost.ApiKey}", calls[^1].Authorization);

        return JsonDocument.Parse(calls[^1].Body).RootElement;
    }
}
