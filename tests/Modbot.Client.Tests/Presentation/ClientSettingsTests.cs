using Modbot.Client.Presentation;

namespace Modbot.Client.Tests.Presentation;

/// <summary>
/// The one setting the client has: where "Pair with a server" sends the browser. A bad settings
/// file must never stop the client, and must never point the button at an insecure page.
/// </summary>
public class ClientSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-settings-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private void Write(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, json);
    }

    [Fact]
    public void TheDefaultIsTheProjectsPairingPage()
    {
        Assert.Equal(new Uri("https://my.modbot.co/go?redir=/pair"), ClientSettings.Load(Path_).PairingPage);
        Assert.Equal(ClientSettings.Default, ClientSettings.Load(Path_));
    }

    [Fact]
    public void AGroupCanPointTheButtonAtItsOwnServer()
    {
        Write("""{ "pairingPage": "https://modbot.example/pair" }""");

        Assert.Equal(new Uri("https://modbot.example/pair"), ClientSettings.Load(Path_).PairingPage);
    }

    [Fact]
    public void ATesterCanPointItAtThisMachine()
    {
        Write("""{ "pairingPage": "http://localhost:5173/pair" }""");

        Assert.Equal(new Uri("http://localhost:5173/pair"), ClientSettings.Load(Path_).PairingPage);
    }

    [Theory]
    [InlineData("""{ "pairingPage": "http://modbot.example/pair" }""")]
    [InlineData("""{ "pairingPage": "not an address" }""")]
    [InlineData("""{ "pairingPage": "" }""")]
    [InlineData("""{ "somethingElse": 1 }""")]
    [InlineData("""{ this is not json""")]
    [InlineData("")]
    public void AnythingElseFallsBackToTheDefaultRatherThanFailing(string json)
    {
        // An insecure page would hand the moderator's sign-in to whoever is on the network, so it
        // is treated the same as a typo: ignored, and the default used.
        Write(json);

        Assert.Equal(ClientSettings.Default, ClientSettings.Load(Path_));
    }

    [Fact]
    public void UpdateChecksAreOnUnlessTheFileSaysOtherwise()
    {
        // M3 9.2: a tool that is genuinely self-hostable must let a group pin a version and never
        // have the client call out on its own. Off is a deliberate word in the file, never a
        // default and never the result of a typo.
        Assert.True(ClientSettings.Load(Path_).CheckForUpdates);

        Write("""{ "checkForUpdates": false }""");
        Assert.False(ClientSettings.Load(Path_).CheckForUpdates);

        Write("""{ "checkForUpdates": "no" }""");
        Assert.True(ClientSettings.Load(Path_).CheckForUpdates);
    }

    [Fact]
    public void TurningUpdatesOffDoesNotDisturbThePairingPage()
    {
        Write("""{ "pairingPage": "https://modbot.example/pair", "checkForUpdates": false }""");

        var settings = ClientSettings.Load(Path_);

        Assert.Equal(new Uri("https://modbot.example/pair"), settings.PairingPage);
        Assert.False(settings.CheckForUpdates);
    }
}
