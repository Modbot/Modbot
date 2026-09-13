using System.Text;
using Modbot.Client.Pairing;

namespace Modbot.Client.Tests.Pairing;

/// <summary>
/// The pairing token is untrusted input from wherever the browser got it, so the interesting cases
/// are the ones that must be refused — and refused with a sentence a moderator can act on, before
/// anything is sent anywhere.
/// </summary>
public class PairingTokenTests
{
    private static string Blob(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void RoundTripsThroughTheTokenAndTheLink()
    {
        var token = new PairingToken(new Uri("https://modbot.example/"), "AB12-CD34");

        Assert.True(PairingToken.TryParse(token.Encode(), out var fromToken, out _));
        Assert.True(PairingToken.TryParse(token.ToLink(), out var fromLink, out _));

        Assert.Equal(token, fromToken);
        Assert.Equal(token, fromLink);
        Assert.StartsWith("modbot-client://pair?token=", token.ToLink());
    }

    [Fact]
    public void ReadsWhatThePairingPageProduces()
    {
        // The web page builds the JSON by hand; this is the contract with it, spelled out. Key
        // names, base64url without padding, and the origin as the browser reports it.
        var blob = Blob("""{"server":"https://modbot.example","code":"AB12-CD34"}""");

        Assert.True(PairingToken.TryParse(blob, out var token, out _));
        Assert.Equal(new Uri("https://modbot.example/"), token!.Server);
        Assert.Equal("AB12-CD34", token.Code);
    }

    [Theory]
    [InlineData("  {token}  ")]
    [InlineData("{token}==")]
    [InlineData("\n{token}\n")]
    public void ToleratesWhatAPasteBoxDoesToText(string shape)
    {
        // Surrounding whitespace and base64 padding are what copying does to a token, not what an
        // attacker does. A moderator who pasted with a trailing newline has not made a mistake.
        var token = new PairingToken(new Uri("https://modbot.example/"), "AB12-CD34");

        Assert.True(PairingToken.TryParse(shape.Replace("{token}", token.Encode()), out var parsed, out _));
        Assert.Equal(token, parsed);
    }

    [Fact]
    public void ALinkWithAnEscapedTokenIsReadTheSameAsABareOne()
    {
        var token = new PairingToken(new Uri("https://modbot.example/"), "AB12-CD34");
        var link = "MODBOT-CLIENT://PAIR?token=" + Uri.EscapeDataString(token.Encode());

        Assert.True(PairingToken.TryParse(link, out var parsed, out _));
        Assert.Equal(token, parsed);
    }

    [Fact]
    public void ThePortAndTheOriginSurviveAndNothingElseDoes()
    {
        var blob = Blob("""{"server":"https://modbot.example:8443","code":"AB12-CD34"}""");

        Assert.True(PairingToken.TryParse(blob, out var token, out _));
        Assert.Equal("https://modbot.example:8443/", token!.Server.ToString());
    }

    [Fact]
    public void PlainHttpToThisMachineIsAllowedForTesting()
    {
        // Loopback never crosses a network, and a tester running Modbot on their own PC should be
        // able to pair against it.
        Assert.True(PairingToken.TryParse(Blob("""{"server":"http://localhost:8080","code":"AB12-CD34"}"""), out var token, out _));
        Assert.Equal("http://localhost:8080/", token!.Server.ToString());
    }

    [Theory]
    [InlineData("http://modbot.example")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("ftp://modbot.example")]
    public void AnInsecureOrForeignSchemeIsRefusedAndNamed(string server)
    {
        // The sentence names the address so the moderator can see what the link was trying to do
        // -- and, for a LAN address, that "on my network" is not the same as "on my machine".
        var blob = Blob($$"""{"server":"{{server}}","code":"AB12-CD34"}""");

        Assert.False(PairingToken.TryParse(blob, out var token, out var problem));
        Assert.Null(token);
        Assert.Contains("https", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://modbot.example/somewhere")]
    [InlineData("https://modbot.example/?x=1")]
    [InlineData("https://modbot.example/#top")]
    [InlineData("https://user:pw@modbot.example/")]
    public void AnAddressWithAnythingBeyondTheOriginIsRefused(string server)
    {
        // A token names a server, full stop. A path, a query or a user name in it has no honest
        // purpose and would let a token steer the client somewhere its author did not intend.
        var blob = Blob($$"""{"server":"{{server}}","code":"AB12-CD34"}""");

        Assert.False(PairingToken.TryParse(blob, out _, out var problem));
        Assert.Contains("extra parts", problem);
    }

    [Theory]
    [InlineData("""{"server":"https://modbot.example"}""", "missing")]
    [InlineData("""{"code":"AB12-CD34"}""", "missing")]
    [InlineData("""{"server":"https://modbot.example","code":""}""", "missing")]
    [InlineData("""{"server":"https://modbot.example","code":"has spaces in it"}""", "pairing code")]
    [InlineData("""{"server":"not an address","code":"AB12-CD34"}""", "server address")]
    [InlineData("""["server","code"]""", "not a Modbot pairing token")]
    [InlineData("""not json at all""", "not a Modbot pairing token")]
    public void AWrongShapeSaysWhatIsWrongAndToCopyItAgain(string json, string expectedWords)
    {
        Assert.False(PairingToken.TryParse(Blob(json), out var token, out var problem));

        Assert.Null(token);
        Assert.Contains(expectedWords, problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pairing page", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("!!!!")]
    [InlineData("modbot-client://pair")]
    [InlineData("modbot-client://pair?nothing=here")]
    [InlineData("modbot-client://elsewhere?token=abc")]
    [InlineData("https://modbot.example/pair")]
    public void TextThatIsNotATokenIsRefusedWithoutBeingDecoded(string text)
    {
        Assert.False(PairingToken.TryParse(text, out var token, out var problem));

        Assert.Null(token);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void SomethingFarTooLongIsRefusedInOneLook()
    {
        var novel = new string('A', PairingToken.MaxLength + 1);

        Assert.False(PairingToken.TryParse(novel, out _, out var problem));
        Assert.Contains("too long", problem);
    }

    [Fact]
    public void ExtraFieldsFromANewerPairingPageDoNotBreakAnOlderClient()
    {
        // Strict about what must be there and what it must look like; not brittle about a field
        // added later. A client that refused to pair because the page grew a hint would be the
        // wrong kind of strict.
        var blob = Blob("""{"server":"https://modbot.example","code":"AB12-CD34","hint":"later"}""");

        Assert.True(PairingToken.TryParse(blob, out _, out _));
    }

    [Theory]
    [InlineData("modbot-client://pair?token=x", true)]
    [InlineData("  MODBOT-CLIENT:anything", true)]
    [InlineData("eyJzZXJ2ZXIiOiJ4In0", false)]
    [InlineData("https://modbot.example/pair", false)]
    [InlineData(null, false)]
    public void KnowsALinkFromABareToken(string? text, bool isLink)
    {
        Assert.Equal(isLink, PairingToken.LooksLikeLink(text));
    }
}
