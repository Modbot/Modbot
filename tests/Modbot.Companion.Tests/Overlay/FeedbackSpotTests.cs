using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// Where the feedback panel was left: a file with no such object reads as no spot, a moved spot
/// survives a save and a load with the rest of the file kept, and it is its own spot, apart from the
/// chat panel's.
/// </summary>
public class FeedbackSpotTests : IDisposable
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
        OverlayAnchor.Head, new OverlayPose(0.2f, -0.1f, -0.8f), 0.4f, Locked: true, ClickThrough: true);

    [Fact]
    public void AFileWithNoSpotReadsAsNone()
    {
        Write("""{ "overlayOn": false }""");

        Assert.Null(CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayFeedbackSpot);
    }

    [Fact]
    public void ASavedSpotIsReadBackWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7 }""");

        Assert.True(CompanionSettings.SaveOverlayFeedbackSpot(SettingsPath, Moved));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(Moved, loaded.OverlayFeedbackSpot);
        Assert.Null(loaded.OverlayChatSpot);
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
    }
}
