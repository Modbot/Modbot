using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The SteamVR page's Grip sensitivity, in <c>settings.json</c> as <c>overlayGripSensitivity</c>:
/// 10 (a squeeze of 0.3, like XSOverlay) unless the file says otherwise, kept inside 1 to 14,
/// written without losing the rest of the file.
/// </summary>
public class OverlayGripSensitivitySettingTests : IDisposable
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
    public void WithNothingSaidItIsTheDefaultThatGrabsAtThreeTenths()
    {
        Assert.Equal(CompanionSettings.DefaultGripSensitivity, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayGripSensitivity);

        Write("""{ "overlayOn": true }""");
        Assert.Equal(CompanionSettings.DefaultGripSensitivity, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayGripSensitivity);

        Assert.Equal(0.3f, CompanionSettings.GripTake(CompanionSettings.DefaultGripSensitivity));
        Assert.Equal(CompanionSettings.DefaultGripSensitivity, CompanionSettings.Default.OverlayGripSensitivity);
    }

    [Theory]
    [InlineData(1, 0.75f)]
    [InlineData(10, 0.3f)]
    [InlineData(14, 0.1f)]
    public void TheFileCanSetItAndTheSqueezeFollows(int level, float take)
    {
        Write($$"""{ "overlayGripSensitivity": {{level}} }""");

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);

        Assert.Equal(level, loaded.OverlayGripSensitivity);
        Assert.Equal(take, CompanionSettings.GripTake(loaded.OverlayGripSensitivity));
    }

    [Theory]
    [InlineData(0, CompanionSettings.MinGripSensitivity)]
    [InlineData(-5, CompanionSettings.MinGripSensitivity)]
    [InlineData(99, CompanionSettings.MaxGripSensitivity)]
    public void ANumberOutsideTheSliderIsKeptInsideIt(int written, int expected)
    {
        Write($$"""{ "overlayGripSensitivity": {{written}} }""");

        Assert.Equal(expected, CompanionSettings.Load(SettingsPath, NoEnvironment).OverlayGripSensitivity);
    }

    [Fact]
    public void AChosenSensitivityIsSavedAndReadBackWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7 }""");

        Assert.True(CompanionSettings.SaveNumber(SettingsPath, CompanionSettings.OverlayGripSensitivityField, 12));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(12, loaded.OverlayGripSensitivity);
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
        Assert.Contains("\"overlayGripSensitivity\"", File.ReadAllText(SettingsPath));
    }
}
