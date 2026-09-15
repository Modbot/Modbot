using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Api.Features.Client;
using Modbot.Core.Configuration;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Client;

/// <summary>
/// The time answer also tells paired clients where their log backup goes (cloud log backup spec 3.1).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ClientTimeTests(PostgresFixture db)
{
    [Fact]
    public async Task TheTimeAnswerNamesTheDefaultCloudWhenNothingIsSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ClientApiTestHost.StartAsync(db);

        using var response = await host.Client.GetAsync("/api/v1/client/time", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(host.Clock.UtcNow, body.GetProperty("serverTime").GetDateTimeOffset());
        Assert.Equal("https://cloud.modbot.co/", body.GetProperty("cloud").GetProperty("endpoint").GetString());
        Assert.False(body.GetProperty("cloud").GetProperty("disabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("instanceId").ValueKind);
    }

    [Fact]
    public void AnOperatorCanNameAnotherCloudOrTurnItOff()
    {
        var named = ModbotCloudAddress.From(ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_CLOUD_ENDPOINT"] = "https://cloud.example.org",
        }));

        var off = ModbotCloudAddress.From(ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_CLOUD_DISABLED"] = "1",
        }));

        var mistyped = ModbotCloudAddress.From(ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_CLOUD_ENDPOINT"] = "cloud.example.org",
        }));

        Assert.Equal(new Uri("https://cloud.example.org"), named.Endpoint);
        Assert.False(named.Disabled);

        var disabled = ServerTimeResponse.Create(DateTimeOffset.UnixEpoch, off);
        Assert.True(disabled.Cloud.Disabled);
        Assert.Null(disabled.Cloud.Endpoint);

        Assert.Equal(ModbotCloudAddress.DefaultEndpoint, mistyped.Endpoint);
    }
}
