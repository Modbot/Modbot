using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The Show VR button switch, in <c>settings.json</c> as <c>overlayButtonOn</c>: on unless the file
/// says otherwise, written without losing the rest of the file.
/// </summary>
public class OverlayButtonOnSettingTests : IDisposable
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
    public void AFileFromBeforeTheSwitchReadsAsOn()
    {
        Assert.True(CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonOn);

        Write("""{ "overlayButtonPlace": "wrist", "overlayOn": true }""");
        Assert.True(CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonOn);
    }

    [Fact]
    public void TheSwitchIsSavedAndReadBackWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayButtonPlace": "wrist" }""");

        Assert.True(CompanionSettings.SaveSwitch(SettingsPath, CompanionSettings.OverlayButtonOnField, false));

        var off = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.False(off.OverlayButtonOn);
        Assert.Equal(Modbot.Companion.Overlay.ButtonPlace.Wrist, off.OverlayButtonPlace);
        Assert.Contains("\"overlayButtonOn\": false", File.ReadAllText(SettingsPath));

        Assert.True(CompanionSettings.SaveSwitch(SettingsPath, CompanionSettings.OverlayButtonOnField, true));
        Assert.True(CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonOn);
    }
}
