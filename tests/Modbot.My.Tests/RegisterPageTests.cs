using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Modbot.My.Tests;

/// <summary>/register notes instance URLs in their own table, apart from self-registered ones.</summary>
[Collection(nameof(PostgresCollection))]
public class RegisterPageTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpeningRegisterNotesTheUrlInItsOwnTableAndServesThePage()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.GetAsync("/register?url=https%3A%2F%2Fmodbot.example%2Fsetup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        await using var context = db.NewContext();
        var noted = Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct));
        Assert.Equal("https://modbot.example", noted.InstanceUrl);
        Assert.Equal(1, noted.Visits);
        Assert.Empty(await context.RegisteredInstances.ToListAsync(Ct));
    }

    [Fact]
    public async Task OpeningItAgainCountsTheVisitAndKeepsTheFirstSighting()
    {
        await using var host = await MyTestHost.StartAsync(db);

        await host.GetAsync("/register?url=https://modbot.example");
        var first = host.Time.GetUtcNow();
        host.Time.Advance(TimeSpan.FromDays(2));
        await host.GetAsync("/register?url=https://modbot.example");

        await using var context = db.NewContext();
        var noted = Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct));
        Assert.Equal(2, noted.Visits);
        Assert.Equal(first, noted.FirstSeenAt);
        Assert.Equal(first.AddDays(2), noted.LastSeenAt);
    }

    [Theory]
    [InlineData("/register")]
    [InlineData("/register?url=http://modbot.example")]
    [InlineData("/register?url=not-a-url")]
    public async Task AMissingOrUnsafeUrlNotesNothingButStillServesThePage(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = db.NewContext();
        Assert.Empty(await context.RegisterPageInstances.ToListAsync(Ct));
    }

    [Fact]
    public async Task TheListAndStatsSayWhichNotedUrlsAlsoRegistered()
    {
        await using var host = await MyTestHost.StartAsync(db);

        await host.GetAsync("/register?url=https://both.example");
        await host.GetAsync("/register?url=https://page-only.example");
        await host.PostJsonAsync("/api/instances/register", new
        {
            instanceId = "both",
            instanceUrl = "https://both.example",
            version = "2026.9.0",
        });

        var list = await host.ReadWithKeyAsync("/api/register-page-instances");
        var alsoRegistered = list.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("instanceUrl").GetString()!, i => i.GetProperty("alsoRegistered").GetBoolean());

        Assert.True(alsoRegistered["https://both.example"]);
        Assert.False(alsoRegistered["https://page-only.example"]);

        var stats = await host.ReadWithKeyAsync("/api/stats");

        Assert.Equal(1, stats.GetProperty("registeredInstances").GetInt32());
        Assert.Equal(2, stats.GetProperty("registerPageInstances").GetInt32());
        Assert.Equal(1, stats.GetProperty("registerPageOnly").GetInt32());
    }
}
