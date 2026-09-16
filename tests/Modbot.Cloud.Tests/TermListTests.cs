using System.Net;
using System.Text.Json;

namespace Modbot.Cloud.Tests;

/// <summary>
/// The curated term lists, now served by Cloud with the shapes they had on my.modbot.co
/// (Cloud accounts and registry spec 4).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TermListTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_index_and_a_list_are_served_to_anyone()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var index = await host.GetAsync("/termlists/index.json");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("application/json", index.Content.Headers.ContentType?.MediaType);

        using var list = await host.GetAsync("/termlists/modbot_profanity_mild.json");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using var schema = await host.GetAsync("/termlists/_schema.json");
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
    }

    [Fact]
    public async Task Every_list_on_the_index_is_served()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var index = await host.GetAsync("/termlists/index.json");
        var body = await index.Content.ReadAsStringAsync(Ct);

        foreach (var entry in JsonDocument.Parse(body).RootElement.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString();
            using var list = await host.GetAsync($"/termlists/{id}.json");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }
    }

    [Fact]
    public async Task An_id_nobody_published_is_a_plain_not_found()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/termlists/not_a_list.json")).StatusCode);
    }
}
