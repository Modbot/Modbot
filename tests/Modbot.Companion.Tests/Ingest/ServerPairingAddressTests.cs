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
    public void APicturePathOfTheServersOwnRouteIsMadeWholeOnThatServer()
    {
        Assert.Equal(
            "https://modbot.example/api/v1/companion/picture/usr_a?v=0a1b2c3d",
            Pairing.PictureAddress("/api/v1/companion/picture/usr_a?v=0a1b2c3d"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://elsewhere.example/api/v1/companion/picture/usr_a")]
    [InlineData("//elsewhere.example/api/v1/companion/picture/usr_a")]
    [InlineData("/api/v1/companion/context?instanceId=Main")]
    [InlineData("/api/v2/companion/picture/usr_a")]
    [InlineData("/api/v1/companion/picture/../user/usr_a")]
    [InlineData("/api/v1/companion/picture/usr_a/..%5c..")]
    [InlineData("/api/files/vrchat?url=https://api.vrchat.cloud/api/1/file/file_x/1/file")]
    public void AnythingElseInTheRosterIsNotTakenAsAPictureAddress(string? path)
    {
        Assert.Null(Pairing.PictureAddress(path));
    }

    [Fact]
    public void OnlyTheServersOwnPictureRouteIsOneThatTheTokenMayGoTo()
    {
        Assert.True(Pairing.IsPictureAddress(new Uri("https://modbot.example/api/v1/companion/picture/usr_a?v=1")));
        Assert.False(Pairing.IsPictureAddress(new Uri("https://elsewhere.example/api/v1/companion/picture/usr_a")));
        Assert.False(Pairing.IsPictureAddress(new Uri("http://modbot.example/api/v1/companion/picture/usr_a")));
        Assert.False(Pairing.IsPictureAddress(new Uri("https://modbot.example/api/v1/companion/context?instanceId=Main")));
        Assert.False(Pairing.IsPictureAddress(new Uri("https://api.vrchat.cloud/api/1/file/file_x/1/file")));
    }

    [Fact]
    public void WithNoWorldKnownTheNumberIsSentAlone()
    {
        Assert.Equal("?instanceId=Main", Pairing.ContextEndpoint("Main").Query);
        Assert.Equal("?instanceId=Main", Pairing.ContextEndpoint("Main", "").Query);
        Assert.Equal("?instanceId=Main&wait=20", Pairing.LivePollEndpoint("Main", null, 20).Query);
    }
}
