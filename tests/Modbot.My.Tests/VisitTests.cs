using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Modbot.My.Tests;

/// <summary>
/// Pages record the instance URL twice, once as served and once after rendering, and count it once.
/// Each IP address reads back only its own instances.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VisitTests(PostgresFixture db)
{
    private const string Alice = "203.0.113.10";
    private const string Bob = "198.51.100.20";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> LocalRegisterAsync(MyTestHost host, string url, string ip) =>
        host.PostJsonAsync("/api/local-register", new { url }, ip);

    private static async Task<string[]> MyInstancesAsync(MyTestHost host, string forwardedFor, string? cloudflare = null)
    {
        var headers = new Dictionary<string, string> { ["X-Forwarded-For"] = forwardedFor };
        if (cloudflare is not null)
            headers["CF-Connecting-IP"] = cloudflare;

        using var response = await host.SendAsync(HttpMethod.Get, "/api/my-instances", headers: headers);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("instanceUrl").GetString()!).ToArray();
    }

    [Fact]
    public async Task OneRegisterPageViewCountsOneVisitWhileBothSavesReachTheDatabase()
    {
        await using var host = await MyTestHost.StartAsync(db);
        var served = host.Time.GetUtcNow();

        var page = await host.GetAsync("/register?url=https%3A%2F%2Fa.example%2Fsetup", Alice);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        await using (var afterServe = db.NewContext())
        {
            var first = Assert.Single(await afterServe.VisitorInstances.ToListAsync(Ct));
            Assert.Equal(served, first.LastSeenAt);
        }

        host.Time.Advance(TimeSpan.FromSeconds(2));
        var rendered = await LocalRegisterAsync(host, "https://a.example", Alice);
        Assert.Equal(HttpStatusCode.NoContent, rendered.StatusCode);

        await using var context = db.NewContext();

        var visitor = Assert.Single(await context.VisitorInstances.ToListAsync(Ct));
        Assert.Equal(Alice, visitor.IpAddress);
        Assert.Equal("https://a.example", visitor.InstanceUrl);
        Assert.Equal(1, visitor.Visits);
        Assert.Equal(served, visitor.FirstSeenAt);
        Assert.Equal(served.AddSeconds(2), visitor.LastSeenAt);

        var noted = Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct));
        Assert.Equal(1, noted.Visits);
        Assert.Equal(served.AddSeconds(2), noted.LastSeenAt);
    }

    [Fact]
    public async Task TheSavesArrivingAtTheSameMomentStillCountOnce()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var responses = await Task.WhenAll(
            host.GetAsync("/register?url=https://a.example", Alice),
            LocalRegisterAsync(host, "https://a.example", Alice),
            LocalRegisterAsync(host, "https://a.example", Alice));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));

        await using var context = db.NewContext();
        Assert.Equal(1, Assert.Single(await context.VisitorInstances.ToListAsync(Ct)).Visits);
        Assert.Equal(1, Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct)).Visits);
    }

    [Fact]
    public async Task TheRepeatWindowRunsFromTheCountedVisitNotTheLatestSave()
    {
        await using var host = await MyTestHost.StartAsync(db);

        await LocalRegisterAsync(host, "https://a.example", Alice);   // 0:00 counted
        host.Time.Advance(TimeSpan.FromMinutes(4));
        await LocalRegisterAsync(host, "https://a.example", Alice);   // 0:04 same visit
        host.Time.Advance(TimeSpan.FromMinutes(2));
        await LocalRegisterAsync(host, "https://a.example", Alice);   // 0:06 counted
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await LocalRegisterAsync(host, "https://a.example", Alice);   // 0:07 same visit

        await using var context = db.NewContext();
        Assert.Equal(2, Assert.Single(await context.VisitorInstances.ToListAsync(Ct)).Visits);
        Assert.Equal(2, Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct)).Visits);
    }

    [Fact]
    public async Task TwoAddressesOpeningTheSameUrlAreTwoVisits()
    {
        await using var host = await MyTestHost.StartAsync(db);

        await host.GetAsync("/register?url=https://a.example", Alice);
        await host.GetAsync("/register?url=https://a.example", Bob);

        await using var context = db.NewContext();
        Assert.Equal(2, (await context.VisitorInstances.ToListAsync(Ct)).Count);
        Assert.Equal(2, Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct)).Visits);
    }

    [Theory]
    [InlineData("/?url=https://a.example")]
    [InlineData("/go?redir=/pair&url=https%3A%2F%2Fa.example")]
    [InlineData("/register?url=https://a.example/setup")]
    public async Task EachPageRecordsTheUrlInItsQueryAsItIsServed(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.GetAsync(path, Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        await using var context = db.NewContext();
        var visitor = Assert.Single(await context.VisitorInstances.ToListAsync(Ct));
        Assert.Equal((Alice, "https://a.example"), (visitor.IpAddress, visitor.InstanceUrl));
        Assert.Equal("https://a.example", Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct)).InstanceUrl);
    }

    [Theory]
    [InlineData("/go?redir=/pair")]
    [InlineData("/?url=http://a.example")]
    [InlineData("/register?url=https://user:pw@a.example")]
    [InlineData("/admin?url=https://a.example")]
    public async Task APageWithoutAUsableUrlRecordsNothing(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(path, Alice)).StatusCode);

        await using var context = db.NewContext();
        Assert.Empty(await context.VisitorInstances.ToListAsync(Ct));
        Assert.Empty(await context.RegisterPageInstances.ToListAsync(Ct));
    }

    [Fact]
    public async Task TheAppsSaveRefusesAUrlThatIsNotAPlainHttpsAddress()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await LocalRegisterAsync(host, "http://a.example", Alice);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var context = db.NewContext();
        Assert.Empty(await context.VisitorInstances.ToListAsync(Ct));
    }

    [Fact]
    public async Task EachAddressReadsOnlyItsOwnInstances()
    {
        await using var host = await MyTestHost.StartAsync(db);

        await host.GetAsync("/register?url=https://alice.example", Alice);
        await host.GetAsync("/go?redir=/pair&url=https://shared.example", Alice);
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await LocalRegisterAsync(host, "https://bob.example", Bob);
        await LocalRegisterAsync(host, "https://shared.example", Bob);
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await LocalRegisterAsync(host, "https://shared.example", Alice);

        Assert.Equal(["https://shared.example", "https://alice.example"], await MyInstancesAsync(host, Alice));
        Assert.Equal(["https://bob.example", "https://shared.example"], await MyInstancesAsync(host, Bob));
        Assert.Empty(await MyInstancesAsync(host, "192.0.2.99"));
    }

    [Fact]
    public async Task NoHeaderLetsACallerReadAnotherAddressesInstances()
    {
        await using var host = await MyTestHost.StartAsync(db);
        await LocalRegisterAsync(host, "https://bob.example", Bob);

        // Alice calls the Railway address directly and claims to be Bob, both ways a client can.
        Assert.Empty(await MyInstancesAsync(host, forwardedFor: Alice, cloudflare: Bob));
        Assert.Empty(await MyInstancesAsync(host, forwardedFor: $"{Bob}, {Alice}"));

        // Bob, really arriving through Cloudflare.
        Assert.Equal(["https://bob.example"], await MyInstancesAsync(host, forwardedFor: $"{Bob}, 162.158.10.20", cloudflare: Bob));
    }

    [Fact]
    public async Task InstancesNotSeenFor90DaysAreLeftOut()
    {
        await using var host = await MyTestHost.StartAsync(db);
        await LocalRegisterAsync(host, "https://a.example", Alice);

        host.Time.Advance(TimeSpan.FromDays(91));

        Assert.Empty(await MyInstancesAsync(host, Alice));
    }
}
