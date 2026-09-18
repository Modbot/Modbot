using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Cloud.Features.Subscribers;

namespace Modbot.Cloud.Tests;

/// <summary>
/// The Modbot mailing list: who goes on it, who can put somebody on it, and how anybody leaves
/// (register details spec 4).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SubscriberTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A person ticks the box while making the first account on a brand-new Modbot, which has not
    /// registered with Cloud yet and so has no credential to send. That opt-in is the main one
    /// there is, so it is taken; a caller holding a credential is recognised all the same.
    /// </summary>
    [Fact]
    public async Task A_server_with_no_credential_yet_can_still_put_an_address_on_the_list()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var anonymous = await host.SendAsync(
            HttpMethod.Post, "/api/v1/subscribers", new { email = "someone@example.com", source = "account" });
        Assert.Equal(HttpStatusCode.Accepted, anonymous.StatusCode);

        // The key my.modbot.co holds is not a credential for this, but it is not a refusal either:
        // the address goes on the list and the caller is limited by where it came from.
        using var proxy = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/subscribers",
            new { email = "someone-else@example.com", source = "account" },
            bearer: CloudTestHost.ProxyKey);
        Assert.Equal(HttpStatusCode.Accepted, proxy.StatusCode);

        var listed = await ListAsync(host);
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task An_address_a_server_sends_goes_on_the_list_once()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        await SubscribeAsync(host, bearer, "Someone@Example.com ", "account-registration");
        host.Time.Advance(TimeSpan.FromDays(1));
        await SubscribeAsync(host, bearer, "someone@example.com", "account-registration");

        var rows = await ListAsync(host);
        var row = Assert.Single(rows);

        // Trimmed and folded to lower case, so the same address cannot be on the list twice.
        Assert.Equal("someone@example.com", row.GetProperty("email").GetString());
        Assert.Equal("account-registration", row.GetProperty("source").GetString());
        Assert.NotEqual(
            row.GetProperty("firstSeenAt").GetDateTimeOffset(), row.GetProperty("lastSeenAt").GetDateTimeOffset());
    }

    /// <summary>
    /// Two Modbots, one person. The list is the project's, not one deployment's, so it is one row.
    /// </summary>
    [Fact]
    public async Task The_same_address_from_two_servers_is_one_row()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, first) = await host.RegisterServerAsync("203.0.113.21");
        var (_, second) = await host.RegisterServerAsync("203.0.113.22");

        await SubscribeAsync(host, first, "someone@example.com", "account-registration");
        await SubscribeAsync(host, second, "someone@example.com", "account-registration");

        Assert.Single(await ListAsync(host));
    }

    [Fact]
    public async Task An_address_is_checked_loosely_and_nothing_more()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        // Things a stricter rule would wrongly turn away.
        foreach (var address in new[]
        {
            "someone+modbot@example.com",
            "a@b.co",
            "unusual!name@sub.domain.example",
            "\"quoted\"@example.com",
        })
        {
            using var accepted = await host.SendAsync(
                HttpMethod.Post, "/api/v1/subscribers", new { email = address, source = "account" }, bearer: bearer);

            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // And the few things that are not addresses at all.
        foreach (var address in new[] { "someone", "someone@", "@example.com", "someone@localhost", "a b@example.com" })
        {
            using var refused = await host.SendAsync(
                HttpMethod.Post, "/api/v1/subscribers", new { email = address, source = "account" }, bearer: bearer);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        Assert.Equal(4, (await ListAsync(host)).Count);
    }

    [Fact]
    public async Task One_server_cannot_pour_a_list_in()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        for (var i = 0; i < SubscriberLimits.PerServerPerHour; i++)
            await SubscribeAsync(host, bearer, $"person{i}@example.com", "account");

        using var refused = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/subscribers",
            new { email = "one-more@example.com", source = "account" },
            bearer: bearer);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);

        // Another server is unaffected.
        var (_, other) = await host.RegisterServerAsync("203.0.113.30");
        await SubscribeAsync(host, other, "one-more@example.com", "account");
    }

    [Fact]
    public async Task One_address_cannot_be_pushed_in_from_many_servers()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        for (var i = 0; i < SubscriberLimits.PerAddressPerHour; i++)
        {
            var (_, sender) = await host.RegisterServerAsync($"203.0.113.{40 + i}");
            await SubscribeAsync(host, sender, "someone@example.com", "account");
        }

        var (_, last) = await host.RegisterServerAsync("203.0.113.60");

        using var refused = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/subscribers",
            new { email = "someone@example.com", source = "account" },
            bearer: last);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task The_link_in_a_message_takes_somebody_off_the_list_without_an_account()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        await SubscribeAsync(host, bearer, "someone@example.com", "account");

        var row = Assert.Single(await ListAsync(host));
        var link = row.GetProperty("unsubscribeUrl").GetString();

        Assert.NotNull(link);
        Assert.StartsWith(
            $"{new Uri(CloudTestHost.PublicAddress, "/unsubscribe")}?token=", link, StringComparison.Ordinal);

        var token = Token(link);

        // No account, no key, nothing but the token in the link.
        using var gone = await host.SendAsync(HttpMethod.Post, "/api/v1/subscribers/unsubscribe", new { token });
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);

        Assert.NotNull(Assert.Single(await ListAsync(host)).GetProperty("unsubscribedAt").GetString());

        // Clicking it a second time is the same as clicking it once.
        using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/subscribers/unsubscribe", new { token });
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task A_token_nobody_was_given_takes_nobody_off_the_list()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        await SubscribeAsync(host, bearer, "someone@example.com", "account");

        foreach (var token in new object?[] { "made-up", "", null })
        {
            using var refused = await host.SendAsync(
                HttpMethod.Post, "/api/v1/subscribers/unsubscribe", new { token });

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        Assert.Null(Assert.Single(await ListAsync(host)).GetProperty("unsubscribedAt").GetString());
    }

    /// <summary>Somebody who ticks the box after leaving is asking again.</summary>
    [Fact]
    public async Task Ticking_the_box_again_puts_somebody_back_on_the_list()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        await SubscribeAsync(host, bearer, "someone@example.com", "account");
        var token = Token(Assert.Single(await ListAsync(host)).GetProperty("unsubscribeUrl").GetString()!);

        using var gone = await host.SendAsync(HttpMethod.Post, "/api/v1/subscribers/unsubscribe", new { token });
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);

        await SubscribeAsync(host, bearer, "someone@example.com", "account");

        var row = Assert.Single(await ListAsync(host));
        Assert.Null(row.GetProperty("unsubscribedAt").GetString());

        // The same link still works, so a link in an older message is not dead.
        Assert.Equal(token, Token(row.GetProperty("unsubscribeUrl").GetString()!));
    }

    [Fact]
    public async Task Only_admin_can_read_the_list()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        await SubscribeAsync(host, bearer, "someone@example.com", "account");

        foreach (var key in new string?[] { null, CloudTestHost.ProxyKey, CloudTestHost.InstancesKey, bearer })
        {
            using var refused = await host.SendAsync(HttpMethod.Get, "/api/admin/subscribers", bearer: key);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

            using var counts = await host.SendAsync(HttpMethod.Get, "/api/admin/subscribers/counts", bearer: key);
            Assert.Equal(HttpStatusCode.Unauthorized, counts.StatusCode);
        }
    }

    [Fact]
    public async Task Admin_reads_the_counts_and_where_people_came_from()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterServerAsync();

        await SubscribeAsync(host, bearer, "one@example.com", "account-registration");
        await SubscribeAsync(host, bearer, "two@example.com", "account-registration");
        await SubscribeAsync(host, bearer, "three@example.com", "invite");

        var token = Token(
            (await ListAsync(host, "one@example.com"))[0].GetProperty("unsubscribeUrl").GetString()!);
        using var gone = await host.SendAsync(HttpMethod.Post, "/api/v1/subscribers/unsubscribe", new { token });
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);

        using var response = await host.SendAsync(
            HttpMethod.Get, "/api/admin/subscribers/counts", bearer: CloudTestHost.RootKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var counts = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(3, counts.GetProperty("total").GetInt32());
        Assert.Equal(2, counts.GetProperty("subscribed").GetInt32());
        Assert.Equal(1, counts.GetProperty("unsubscribed").GetInt32());
        Assert.Equal(2, counts.GetProperty("bySource").GetProperty("account-registration").GetInt32());
        Assert.Equal(1, counts.GetProperty("bySource").GetProperty("invite").GetInt32());
    }

    [Fact]
    public async Task The_unsubscribe_page_is_served_like_any_other_page()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var page = await host.GetAsync("/unsubscribe?token=whatever");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(CloudTestHost.AppHtml, await page.Content.ReadAsStringAsync(Ct));
    }

    private static async Task SubscribeAsync(CloudTestHost host, string bearer, string email, string source)
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/subscribers", new { email, source }, bearer: bearer);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<List<JsonElement>> ListAsync(CloudTestHost host, string? search = null)
    {
        var path = search is null
            ? "/api/admin/subscribers"
            : $"/api/admin/subscribers?search={Uri.EscapeDataString(search)}";

        using var response = await host.SendAsync(HttpMethod.Get, path, bearer: CloudTestHost.RootKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private static string Token(string link) =>
        Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..]);
}
