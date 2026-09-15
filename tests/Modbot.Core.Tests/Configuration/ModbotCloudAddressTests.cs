using Modbot.Core.Configuration;

namespace Modbot.Core.Tests.Configuration;

/// <summary>
/// MODBOT_CLOUD_ENDPOINT and MODBOT_CLOUD_DISABLED on a server: where that server talks to Modbot
/// Cloud for its own purposes, and whether it does (central services spec 1.1).
/// </summary>
public class ModbotCloudAddressTests
{
    [Fact]
    public void NothingSetIsOnToModbotCloud()
    {
        var address = ModbotCloudAddress.From(ModbotEnvironment.Read(new Dictionary<string, string?>()));

        Assert.Equal(ModbotCloudAddress.Default, address);
        Assert.Equal(new Uri("https://cloud.modbot.co"), address.Endpoint);
        Assert.False(address.Disabled);
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

        Assert.True(off.Disabled);

        Assert.Equal(ModbotCloudAddress.DefaultEndpoint, mistyped.Endpoint);
    }
}
