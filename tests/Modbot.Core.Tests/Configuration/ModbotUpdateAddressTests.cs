using Modbot.Core.Configuration;

namespace Modbot.Core.Tests.Configuration;

/// <summary>
/// Where a server asks what the newest release is — and why MODBOT_CLOUD_DISABLED does not stop it
/// (update checking design §5).
/// </summary>
public class ModbotUpdateAddressTests
{
    [Fact]
    public void NothingSetAsksModbotCloud()
    {
        var address = ModbotUpdateAddress.From(ModbotEnvironment.Read(new Dictionary<string, string?>()));

        Assert.Equal(ModbotUpdateAddress.Default, address);
        Assert.Equal(new Uri("https://cloud.modbot.co"), address.Endpoint);
    }

    /// <summary>
    /// The whole point of the separate type. Turning the Cloud features off must not turn off being
    /// told that a newer Modbot exists: nothing about the deployment is sent either way, and the
    /// deployments least in touch with the project must not be the ones never told about a fix.
    /// </summary>
    [Fact]
    public void TurningCloudOffDoesNotTurnUpdateCheckingOff()
    {
        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_CLOUD_DISABLED"] = "1",
        });

        Assert.True(ModbotCloudAddress.From(environment).Disabled);
        Assert.Equal(new Uri("https://cloud.modbot.co"), ModbotUpdateAddress.From(environment).Endpoint);
    }

    [Fact]
    public void AnOperatorWhoNamedAnotherCloudAsksThatOne()
    {
        var address = ModbotUpdateAddress.From(ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_CLOUD_ENDPOINT"] = "https://cloud.example.org",
            ["MODBOT_CLOUD_DISABLED"] = "true",
        }));

        Assert.Equal(new Uri("https://cloud.example.org"), address.Endpoint);
    }

    [Fact]
    public void SomethingThatIsNotAnAddressMeansTheDefault()
    {
        var address = ModbotUpdateAddress.From(ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_CLOUD_ENDPOINT"] = "cloud.example.org",
        }));

        Assert.Equal(ModbotCloudAddress.DefaultEndpoint, address.Endpoint);
    }
}
