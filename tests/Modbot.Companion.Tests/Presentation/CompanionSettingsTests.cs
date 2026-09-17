using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The one setting the client has: where "Pair with a server" sends the browser. A bad settings
/// file must never stop the client, and must never point the button at an insecure page.
/// </summary>
public class CompanionSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-settings-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "settings.json");

    /// <summary>So a Modbot Cloud variable set on the machine running the tests changes nothing here.</summary>
    private static string? NoEnvironment(string name) => null;

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
        Assert.Equal(new Uri("https://my.modbot.co/go?redir=/pair"), CompanionSettings.Load(Path_, NoEnvironment).PairingPage);
        Assert.Equal(CompanionSettings.Default, CompanionSettings.Load(Path_, NoEnvironment));
    }

    [Fact]
    public void AGroupCanPointTheButtonAtItsOwnServer()
    {
        Write("""{ "pairingPage": "https://modbot.example/pair" }""");

        Assert.Equal(new Uri("https://modbot.example/pair"), CompanionSettings.Load(Path_, NoEnvironment).PairingPage);
    }

    [Fact]
    public void ATesterCanPointItAtThisMachine()
    {
        Write("""{ "pairingPage": "http://localhost:5173/pair" }""");

        Assert.Equal(new Uri("http://localhost:5173/pair"), CompanionSettings.Load(Path_, NoEnvironment).PairingPage);
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

        Assert.Equal(CompanionSettings.Default, CompanionSettings.Load(Path_, NoEnvironment));
    }

    [Fact]
    public void TheLogFolderIsUnsetUntilSomebodyNamesOne()
    {
        Assert.Null(CompanionSettings.Load(Path_, NoEnvironment).VRChatLogFolder);

        Write("""{ "vrchatLogFolder": "  /games/vrchat/logs  " }""");
        Assert.Equal("/games/vrchat/logs", CompanionSettings.Load(Path_, NoEnvironment).VRChatLogFolder);

        Write("""{ "vrchatLogFolder": "   " }""");
        Assert.Null(CompanionSettings.Load(Path_, NoEnvironment).VRChatLogFolder);
    }

    /// <summary>Saving the folder keeps every other field, and clearing it removes the field rather than writing "".</summary>
    [Fact]
    public void SavingTheLogFolderKeepsTheRestOfTheFileAndClearingRemovesIt()
    {
        Write("""{ "pairingPage": "https://cats.example/pair", "checkForUpdates": false }""");

        Assert.True(CompanionSettings.SaveText(Path_, CompanionSettings.VRChatLogFolderField, "/games/vrchat/logs"));

        var saved = CompanionSettings.Load(Path_, NoEnvironment);
        Assert.Equal("/games/vrchat/logs", saved.VRChatLogFolder);
        Assert.Equal(new Uri("https://cats.example/pair"), saved.PairingPage);
        Assert.False(saved.CheckForUpdates);

        Assert.True(CompanionSettings.SaveText(Path_, CompanionSettings.VRChatLogFolderField, " "));
        Assert.Null(CompanionSettings.Load(Path_, NoEnvironment).VRChatLogFolder);
        Assert.DoesNotContain("vrchatLogFolder", File.ReadAllText(Path_));
    }

    [Fact]
    public void UpdateChecksAreOnUnlessTheFileSaysOtherwise()
    {
        // M3 9.2: a tool that is genuinely self-hostable must let a group pin a version and never
        // have the client call out on its own. Off is a deliberate word in the file, never a
        // default and never the result of a typo.
        Assert.True(CompanionSettings.Load(Path_, NoEnvironment).CheckForUpdates);

        Write("""{ "checkForUpdates": false }""");
        Assert.False(CompanionSettings.Load(Path_, NoEnvironment).CheckForUpdates);

        Write("""{ "checkForUpdates": "no" }""");
        Assert.True(CompanionSettings.Load(Path_, NoEnvironment).CheckForUpdates);
    }

    [Fact]
    public void TurningUpdatesOffDoesNotDisturbThePairingPage()
    {
        Write("""{ "pairingPage": "https://modbot.example/pair", "checkForUpdates": false }""");

        var settings = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.Equal(new Uri("https://modbot.example/pair"), settings.PairingPage);
        Assert.False(settings.CheckForUpdates);
    }

    [Fact]
    public void ThePanelIsWhereItWasLeftAndNeverOutOfBounds()
    {
        Write("""{ "pairingPage": "https://modbot.example/pair", "overlay": { "anchor": "world", "offset": { "x": 1, "y": 2, "z": -3 }, "width": 9, "opacity": "0.5" } }""");

        var placement = CompanionSettings.Load(Path_, NoEnvironment).Overlay;

        Assert.Equal(Modbot.Companion.Overlay.OverlayAnchor.World, placement.Anchor);
        Assert.Equal(new Modbot.Companion.Overlay.OverlayPose(1, 2, -3), placement.Offset);
        Assert.Equal(Modbot.Companion.Overlay.OverlayPlacement.MaxWidth, placement.Width);
        Assert.Equal(0.5f, placement.Opacity);
        Assert.Equal(0f, placement.Curve);
    }

    [Fact]
    public void AMissingOrBrokenOverlayObjectIsTheDefaultPlacement()
    {
        Write("""{ "overlay": "sideways" }""");

        Assert.Equal(Modbot.Companion.Overlay.OverlayPlacement.Default, CompanionSettings.Load(Path_, NoEnvironment).Overlay);
        Assert.Equal(Modbot.Companion.Overlay.OverlayPlacement.Default, CompanionSettings.Default.Overlay);
    }

    [Fact]
    public void SavingThePlacementKeepsEveryOtherField()
    {
        Write("""{ "pairingPage": "https://modbot.example/pair", "checkForUpdates": false }""");
        var placement = new Modbot.Companion.Overlay.OverlayPlacement(
            Modbot.Companion.Overlay.OverlayAnchor.LeftHand, new Modbot.Companion.Overlay.OverlayPose(0.1f, 0.2f, -0.3f, 0, 0.7071068f, 0, 0.7071068f), 0.6f, 0.8f, 0.2f);

        Assert.True(CompanionSettings.SaveOverlay(Path_, placement));

        var loaded = CompanionSettings.Load(Path_, NoEnvironment);
        Assert.Equal(new Uri("https://modbot.example/pair"), loaded.PairingPage);
        Assert.False(loaded.CheckForUpdates);
        Assert.Equal(placement, loaded.Overlay);
    }

    [Fact]
    public void TheEventsFiltersAreReadFromTheFile()
    {
        Write("""{ "eventsFilters": ["kind:is:joined,left", "not a chip", "text:contains:rin"] }""");

        var loaded = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.Equal(["kind:is:joined,left", "text:contains:rin"], loaded.EventsFilters.Encode());
    }

    [Theory]
    [InlineData("""{ "eventsFilters": "kind:is:joined" }""")]
    [InlineData("""{ "eventsFilters": 3 }""")]
    [InlineData("""{ }""")]
    public void MissingOrBrokenEventsFiltersMeanNone(string json)
    {
        Write(json);

        Assert.Equal(EventFilterSet.Empty, CompanionSettings.Load(Path_, NoEnvironment).EventsFilters);
    }

    [Fact]
    public void SavingTheEventsFiltersKeepsEveryOtherFieldAndNoneRemovesTheField()
    {
        Write("""{ "pairingPage": "https://modbot.example/pair", "checkForUpdates": false }""");
        var filters = EventFilterSet.Parse(["kind:is:joined", "group:is:Cat%20Caf%C3%A9"]);

        Assert.True(CompanionSettings.SaveEventsFilters(Path_, filters));

        var loaded = CompanionSettings.Load(Path_, NoEnvironment);
        Assert.Equal(new Uri("https://modbot.example/pair"), loaded.PairingPage);
        Assert.False(loaded.CheckForUpdates);
        Assert.Equal(filters, loaded.EventsFilters);
        Assert.Contains("\"eventsFilters\"", File.ReadAllText(Path_), StringComparison.Ordinal);

        Assert.True(CompanionSettings.SaveEventsFilters(Path_, EventFilterSet.Empty));
        Assert.DoesNotContain("eventsFilters", File.ReadAllText(Path_), StringComparison.Ordinal);
        Assert.False(CompanionSettings.Load(Path_, NoEnvironment).CheckForUpdates);
    }
}
