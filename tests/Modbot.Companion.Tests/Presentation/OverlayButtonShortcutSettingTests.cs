using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The SteamVR page's Shortcut and Hold time, in <c>settings.json</c> as <c>overlayButtonStick</c>,
/// <c>overlayButtonDirection</c> and <c>overlayButtonHold</c>: the right stick pulled back for five
/// seconds unless the file says otherwise, written without losing the rest of the file.
/// </summary>
public class OverlayButtonShortcutSettingTests : IDisposable
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

    private ButtonShortcut Load() => CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayButtonShortcut;

    [Fact]
    public void WithNothingSaidItIsTheRightStickBackForFiveSeconds()
    {
        Assert.Equal(ButtonShortcut.Default, Load());

        Write("""{ "overlayOn": true }""");
        Assert.Equal(ButtonShortcut.Default, Load());

        Assert.Equal(ButtonShortcut.Default, CompanionSettings.Default.OverlayButtonShortcut);
    }

    [Fact]
    public void TheFileCanChooseTheStickTheWayAndTheTime()
    {
        Write("""{ "overlayButtonStick": "left", "overlayButtonDirection": "forward", "overlayButtonHold": 8 }""");

        Assert.Equal(new ButtonShortcut(ShortcutStick.Left, StickDirection.Forward, 8), Load());
    }

    [Fact]
    public void TheStickCanBeTurnedOffAndTheWayIsKept()
    {
        Write("""{ "overlayButtonStick": "off", "overlayButtonDirection": "left" }""");

        var shortcut = Load();
        Assert.False(shortcut.IsOn);
        Assert.Equal(StickDirection.Left, shortcut.Direction);
    }

    [Fact]
    public void NamesAndNumbersThatMeanNothingAreTheDefaults()
    {
        Write("""{ "overlayButtonStick": "elbow", "overlayButtonDirection": "up", "overlayButtonHold": 0 }""");

        var shortcut = Load();
        Assert.Equal(ShortcutStick.Right, shortcut.Stick);
        Assert.Equal(StickDirection.Back, shortcut.Direction);
        Assert.Equal(ButtonShortcut.MinSeconds, shortcut.Seconds);
    }

    [Fact]
    public void EachChoiceIsSavedAndReadBackWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7, "overlayButtonPlace": "wrist" }""");

        Assert.True(CompanionSettings.SaveText(
            SettingsPath, CompanionSettings.OverlayButtonStickField, ButtonShortcut.Written(ShortcutStick.Left)));
        Assert.True(CompanionSettings.SaveText(
            SettingsPath, CompanionSettings.OverlayButtonDirectionField, ButtonShortcut.Written(StickDirection.Right)));
        Assert.True(CompanionSettings.SaveNumber(SettingsPath, CompanionSettings.OverlayButtonHoldField, 3));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(new ButtonShortcut(ShortcutStick.Left, StickDirection.Right, 3), loaded.OverlayButtonShortcut);
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
        Assert.Equal(ButtonPlace.Wrist, loaded.OverlayButtonPlace);

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"overlayButtonStick\"", text);
        Assert.Contains("\"overlayButtonDirection\"", text);
        Assert.Contains("\"overlayButtonHold\"", text);
    }
}
