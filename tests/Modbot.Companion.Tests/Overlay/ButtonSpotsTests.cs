using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// Where the show and hide button was left in each place: a place nobody moved it in starts from its
/// own default, an older settings file reads as that, and a moved spot survives a save and a load
/// with the rest of the file kept.
/// </summary>
public class ButtonSpotsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-settings-tests", Guid.NewGuid().ToString("n"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

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
        File.WriteAllText(SettingsPath, json);
    }

    private static readonly OverlayPlacement Moved = new(
        OverlayAnchor.Head, new OverlayPose(-0.2f, -0.1f, -0.7f), 0.3f, Locked: true, ClickThrough: true);

    [Theory]
    [InlineData(ButtonPlace.Corner)]
    [InlineData(ButtonPlace.Wrist)]
    public void APlaceNobodyMovedItInStartsFromItsOwnDefault(ButtonPlace place)
        => Assert.Equal(OverlayButton.ToPlacement(place), ButtonSpots.None.For(place));

    [Fact]
    public void AMovedSpotBelongsToItsPlaceOnly()
    {
        var spots = ButtonSpots.None.With(ButtonPlace.Corner, Moved);

        Assert.Equal(Moved, spots.For(ButtonPlace.Corner));
        Assert.Equal(OverlayButton.ToPlacement(ButtonPlace.Wrist), spots.For(ButtonPlace.Wrist));
    }

    [Fact]
    public void ASpotKeepsTheLockAndTheHandThroughTheFile()
    {
        var spots = ButtonSpots.None.With(ButtonPlace.Wrist, Moved);

        var read = ButtonSpots.FromJson(spots.ToJson());

        Assert.Equal(Moved, read.For(ButtonPlace.Wrist));
        Assert.Null(read.Corner);
    }

    [Fact]
    public void OnlyThePlacesWithASpotAreWritten()
    {
        var json = ButtonSpots.None.With(ButtonPlace.Corner, Moved).ToJson();

        Assert.True(json.ContainsKey("corner"));
        Assert.False(json.ContainsKey("wrist"));
        Assert.Empty(ButtonSpots.None.ToJson());
    }

    [Fact]
    public void AnOlderFileWithNoSpotsReadsAsTheDefaults()
    {
        Write("""{ "overlayOn": true, "overlayButtonPlace": "wrist" }""");

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);

        Assert.Equal(ButtonSpots.None, loaded.OverlayButtonSpots);
        Assert.Equal(OverlayButton.ToPlacement(ButtonPlace.Wrist), loaded.OverlayButtonSpots.For(loaded.OverlayButtonPlace));
        Assert.False(loaded.OverlayButtonSpots.For(ButtonPlace.Corner).Locked);
    }

    [Fact]
    public void ASpotThatIsNotAnObjectIsIgnored()
    {
        Write("""{ "overlayButtonSpots": { "corner": 5, "wrist": "up" } }""");

        Assert.Equal(ButtonSpots.None, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonSpots);
    }

    [Fact]
    public void ASavedSpotIsReadBackWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7 }""");

        Assert.True(CompanionSettings.SaveOverlayButtonSpots(SettingsPath, ButtonSpots.None.With(ButtonPlace.Corner, Moved)));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(Moved, loaded.OverlayButtonSpots.For(ButtonPlace.Corner));
        Assert.Equal(OverlayButton.ToPlacement(ButtonPlace.Wrist), loaded.OverlayButtonSpots.For(ButtonPlace.Wrist));
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
    }

    [Fact]
    public void ASpotOutsideItsBoundsIsBroughtInside()
    {
        Write("""{ "overlayButtonSpots": { "corner": { "anchor": "head", "width": 99, "opacity": 0 } } }""");

        var corner = CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonSpots.For(ButtonPlace.Corner);

        Assert.Equal(OverlayPlacement.MaxWidth, corner.Width);
        Assert.Equal(OverlayPlacement.MinOpacity, corner.Opacity);
    }

    [Fact]
    public void TheDefaultSettingsHaveNoSpots()
        => Assert.Equal(ButtonSpots.None, CompanionSettings.Default.OverlayButtonSpots);
}
