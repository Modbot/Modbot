using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Ingest;

/// <summary>
/// An instance is its world and its number, and a number alone is only unique inside one world.
/// The addresses the overlay reads with carry both.
/// </summary>
public class ServerPairingAddressTests
{
    private static readonly ServerPairing Pairing =
        new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public void TheRosterReadNamesTheWorldAndTheNumber()
    {
        var uri = Pairing.ContextEndpoint("Main", "wrld_a");

        Assert.Equal("/api/v1/companion/context", uri.AbsolutePath);
        Assert.Equal("?instanceId=Main&worldId=wrld_a", uri.Query);
    }

    [Fact]
    public void TheLivePollAndTheSocketNameThemToo()
    {
        Assert.Equal(
            "?instanceId=Main&worldId=wrld_a&after=12&wait=20",
            Pairing.LivePollEndpoint("Main", "12", 20, "wrld_a").Query);

        var socket = Pairing.LiveSocketEndpoint("Main", "12", "wrld_a");
        Assert.Equal("wss", socket.Scheme);
        Assert.Equal("?instanceId=Main&worldId=wrld_a&after=12", socket.Query);
    }

    [Fact]
    public void AWorldWithSpecialCharactersIsEscaped()
    {
        Assert.Equal("?instanceId=a%26b&worldId=w%20x", Pairing.ContextEndpoint("a&b", "w x").Query);
    }

    [Fact]
    public void WithNoWorldKnownTheNumberIsSentAlone()
    {
        Assert.Equal("?instanceId=Main", Pairing.ContextEndpoint("Main").Query);
        Assert.Equal("?instanceId=Main", Pairing.ContextEndpoint("Main", "").Query);
        Assert.Equal("?instanceId=Main&wait=20", Pairing.LivePollEndpoint("Main", null, 20).Query);
    }
}
