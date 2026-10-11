using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The SteamVR page's Button place, in <c>settings.json</c> as <c>overlayButtonPlace</c>: the
/// corner unless the file says otherwise, written without losing the rest of the file.
/// </summary>
public class OverlayButtonPlaceSettingTests : IDisposable
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

    [Fact]
    public void WithNothingSaidTheButtonIsInTheCorner()
    {
        Assert.Equal(ButtonPlace.Corner, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonPlace);

        Write("""{ "overlayOn": true }""");
        Assert.Equal(ButtonPlace.Corner, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonPlace);
    }

    [Fact]
    public void TheFileCanPutItOnTheWrist()
    {
        Write("""{ "overlayButtonPlace": "wrist" }""");

        Assert.Equal(ButtonPlace.Wrist, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonPlace);
    }

    [Fact]
    public void ANameThatMeansNothingIsTheCorner()
    {
        Write("""{ "overlayButtonPlace": "behind me" }""");

        Assert.Equal(ButtonPlace.Corner, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonPlace);
    }

    [Fact]
    public void AChosenPlaceIsSavedAndReadBackWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7 }""");

        Assert.True(CompanionSettings.SaveText(
            SettingsPath,
            CompanionSettings.OverlayButtonPlaceField,
            OverlayButton.Written(ButtonPlace.Wrist)));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(ButtonPlace.Wrist, loaded.OverlayButtonPlace);
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
        Assert.Contains("\"overlayButtonPlace\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void TheDefaultSettingsHaveTheCorner()
        => Assert.Equal(ButtonPlace.Corner, CompanionSettings.Default.OverlayButtonPlace);
}
