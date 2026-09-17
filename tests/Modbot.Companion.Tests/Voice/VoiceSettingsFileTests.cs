using Modbot.Companion.Presentation;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// The <c>voice</c> object in <c>settings.json</c>: off until somebody says otherwise, every
/// field optional, and a save that leaves the rest of the file alone.
/// </summary>
public class VoiceSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-voice-settings-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "settings.json");

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

    private VoiceSettings Load() => CompanionSettings.Load(Path_, NoEnvironment).Voice;

    [Fact]
    public void OffByDefaultWithEveryKindOnAndTheSystemDefaultDevice()
    {
        var voice = Load();

        Assert.False(voice.On);
        Assert.True(voice.Joins);
        Assert.True(voice.Leaves);
        Assert.True(voice.FlaggedJoins);
        Assert.Equal(VoiceSettings.DefaultVolume, voice.Volume);
        Assert.Null(voice.OutputDeviceId);
        Assert.Equal(VoiceSettings.Default, voice);
    }

    [Fact]
    public void ReadsTheVoiceObject()
    {
        Write("""
            {
              "voice": { "on": true, "joins": false, "leaves": true, "flaggedJoins": false, "volume": 35, "outputDevice": " {hmd} " }
            }
            """);

        var voice = Load();

        Assert.True(voice.On);
        Assert.False(voice.Joins);
        Assert.True(voice.Leaves);
        Assert.False(voice.FlaggedJoins);
        Assert.Equal(35, voice.Volume);
        Assert.Equal("{hmd}", voice.OutputDeviceId);
    }

    [Fact]
    public void MissingFieldsInsideTheObjectTakeTheirDefaults()
    {
        Write("""{ "voice": { "on": true } }""");

        var voice = Load();

        Assert.True(voice.On);
        Assert.True(voice.Joins);
        Assert.Equal(VoiceSettings.DefaultVolume, voice.Volume);
        Assert.Null(voice.OutputDeviceId);
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    public void VolumeIsKeptInsideTheSlider(int written, int read)
    {
        Write($$"""{ "voice": { "volume": {{written}} } }""");

        Assert.Equal(read, Load().Volume);
    }

    [Fact]
    public void ABlankDeviceIsNoDevice()
    {
        Write("""{ "voice": { "outputDevice": "   " } }""");

        Assert.Null(Load().OutputDeviceId);
    }

    [Fact]
    public void AWrongShapeFallsBackToTheDefaultsRatherThanFailing()
    {
        Write("""{ "voice": { "on": "yes", "volume": "loud" } }""");

        Assert.Equal(VoiceSettings.Default, Load());
    }

    [Fact]
    public void SavingKeepsTheRestOfTheFile()
    {
        Write("""{ "pairingPage": "https://cats.example/pair", "checkForUpdates": false, "cloud": { "disabled": true } }""");

        Assert.True(CompanionSettings.SaveVoice(Path_, new VoiceSettings(On: true, Leaves: false, Volume: 60, OutputDeviceId: "{hmd}")));

        var saved = CompanionSettings.Load(Path_, NoEnvironment);
        Assert.Equal(new Uri("https://cats.example/pair"), saved.PairingPage);
        Assert.False(saved.CheckForUpdates);
        Assert.True(saved.Cloud.Disabled);
        Assert.Equal(new VoiceSettings(On: true, Leaves: false, Volume: 60, OutputDeviceId: "{hmd}"), saved.Voice);
    }

    [Fact]
    public void SavingWithNoDeviceLeavesTheDeviceFieldOut()
    {
        Assert.True(CompanionSettings.SaveVoice(Path_, new VoiceSettings(On: true, OutputDeviceId: "{hmd}")));
        Assert.Contains("outputDevice", File.ReadAllText(Path_), StringComparison.Ordinal);

        Assert.True(CompanionSettings.SaveVoice(Path_, new VoiceSettings(On: true)));

        Assert.DoesNotContain("outputDevice", File.ReadAllText(Path_), StringComparison.Ordinal);
        Assert.Null(Load().OutputDeviceId);
    }

    [Fact]
    public void ABrokenFileIsNotOverwritten()
    {
        Write("{ this is not json");

        Assert.False(CompanionSettings.SaveVoice(Path_, new VoiceSettings(On: true)));
        Assert.Equal("{ this is not json", File.ReadAllText(Path_));
    }

    [Fact]
    public void GainIsTheVolumeAsAFraction()
    {
        Assert.Equal(0.8f, VoiceSettings.Default.Gain);
        Assert.Equal(0f, new VoiceSettings(Volume: 0).Gain);
        Assert.Equal(1f, new VoiceSettings(Volume: 100).Gain);
        Assert.Equal(1f, new VoiceSettings(Volume: 400).Gain);
    }
}
