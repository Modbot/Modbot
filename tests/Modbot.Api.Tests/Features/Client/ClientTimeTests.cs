using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Client;

/// <summary>
/// The time answer is the time and nothing else. A server tells its paired clients nothing about
/// Modbot Cloud: a client's Cloud settings live on the moderator's PC (cloud event backup spec 3.1).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ClientTimeTests(PostgresFixture db)
{
    [Fact]
    public async Task TheTimeAnswerIsOnlyTheServersTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ClientApiTestHost.StartAsync(db);

        using var response = await host.Client.GetAsync("/api/v1/client/time", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(host.Clock.UtcNow, body.GetProperty("serverTime").GetDateTimeOffset());
        Assert.False(body.TryGetProperty("cloud", out _));
        Assert.False(body.TryGetProperty("instanceId", out _));
        Assert.Equal(["serverTime"], body.EnumerateObject().Select(p => p.Name));
    }
}
