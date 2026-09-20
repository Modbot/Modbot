using Modbot.Companion.Listening;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// The <c>listenForPhrase</c> object in <c>settings.json</c>: off by default, off when the object
/// is missing, off when the object is nonsense, and a save that leaves the rest of the file alone.
/// </summary>
/// <remarks>
/// Off by default is the whole of the promise here. A client updated into a version that can open a
/// microphone must not open one, and the only thing standing between those two facts is this file.
/// </remarks>
public class ListeningSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-listening-settings-tests", Guid.NewGuid().ToString("n"));

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

    private ListeningSettings Load() => CompanionSettings.Load(Path_, NoEnvironment).Listening;

    [Fact]
    public void OffWhenThereIsNoFileAtAll()
    {
        Assert.False(Load().On);
        Assert.Equal(ListeningSettings.Default, Load());
    }

    [Fact]
    public void OffWhenTheObjectIsNotThere()
    {
        // An existing client updated into a version that can listen: its settings file has
        // everything else in it and nothing about this. It must not start listening.
        Write("""{ "pairingPage": "https://cats.example/pair", "voice": { "on": true } }""");

        Assert.False(Load().On);
    }

    [Fact]
    public void OffWhenTheObjectIsEmpty()
    {
        Write("""{ "listenForPhrase": { } }""");

        Assert.False(Load().On);
    }

    [Fact]
    public void OffWhenTheObjectIsTheWrongShape()
    {
        Write("""{ "listenForPhrase": { "on": "yes please" } }""");

        Assert.False(Load().On);
    }

    [Fact]
    public void ReadsItWhenSomebodyTurnedItOn()
    {
        Write("""{ "listenForPhrase": { "on": true } }""");

        Assert.True(Load().On);
    }

    [Fact]
    public void NoMicrophoneIsPickedUntilSomebodyPicksOne()
    {
        Write("""{ "listenForPhrase": { "on": true } }""");

        Assert.Null(Load().MicrophoneId);
    }

    [Fact]
    public void ReadsTheMicrophoneSomebodyPicked()
    {
        Write("""{ "listenForPhrase": { "on": true, "microphone": "{0.0.1.00000000}.{abc}" } }""");

        Assert.Equal("{0.0.1.00000000}.{abc}", Load().MicrophoneId);
    }

    [Fact]
    public void ABlankMicrophoneMeansTheWindowsDefault()
    {
        Write("""{ "listenForPhrase": { "on": true, "microphone": "   " } }""");

        Assert.Null(Load().MicrophoneId);
    }

    [Fact]
    public void ThePickedMicrophoneSurvivesASaveAndTheDefaultIsNotWrittenDown()
    {
        Assert.True(CompanionSettings.SaveListening(
            Path_, new ListeningSettings(On: true, MicrophoneId: "{headset}")));

        Assert.Equal("{headset}", Load().MicrophoneId);
        Assert.Contains("\"microphone\": \"{headset}\"", File.ReadAllText(Path_), StringComparison.Ordinal);

        // Back to the Windows default: the file says nothing rather than pinning a device id that
        // would then stop following the machine, the same rule the clips folder follows.
        Assert.True(CompanionSettings.SaveListening(Path_, new ListeningSettings(On: true)));

        Assert.Null(Load().MicrophoneId);
        Assert.DoesNotContain("microphone", File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingKeepsTheRestOfTheFile()
    {
        Write("""{ "pairingPage": "https://cats.example/pair", "checkForUpdates": false, "clips": { "on": true } }""");

        Assert.True(CompanionSettings.SaveListening(Path_, new ListeningSettings(On: true)));

        var saved = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.Equal(new Uri("https://cats.example/pair"), saved.PairingPage);
        Assert.False(saved.CheckForUpdates);
        Assert.True(saved.Clips.On);
        Assert.True(saved.Listening.On);
    }

    [Fact]
    public void ItGoesBackOffAgainAndTheFileSaysSo()
    {
        Assert.True(CompanionSettings.SaveListening(Path_, new ListeningSettings(On: true)));
        Assert.True(Load().On);

        Assert.True(CompanionSettings.SaveListening(Path_, new ListeningSettings(On: false)));
        Assert.False(Load().On);

        // Written as false rather than removed, so a file somebody opens says plainly that it is
        // off rather than saying nothing at all.
        Assert.Contains("\"on\": false", File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingTheNotificationsLeavesTheListeningAlone()
    {
        Assert.True(CompanionSettings.SaveListening(Path_, new ListeningSettings(On: true)));
        Assert.True(CompanionSettings.SaveNotifications(Path_, new NotificationSettings(Volume: 15)));

        Assert.True(Load().On);
        Assert.Equal(15, CompanionSettings.Load(Path_, NoEnvironment).Notifications.Volume);
    }

    [Fact]
    public void ABrokenFileIsNotOverwritten()
    {
        Write("{ this is not json");

        Assert.False(CompanionSettings.SaveListening(Path_, new ListeningSettings(On: true)));
        Assert.Equal("{ this is not json", File.ReadAllText(Path_));
    }
}
