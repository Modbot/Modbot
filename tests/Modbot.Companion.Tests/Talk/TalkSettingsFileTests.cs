using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// The <c>talk</c> object in <c>settings.json</c>: off by default, off when the object is missing or
/// nonsense, a key that cannot be pressed read as not recorded, and a save that leaves the rest of
/// the file alone.
/// </summary>
/// <remarks>
/// Off by default is the whole of the promise here. A client updated into a version that can press
/// keys and announce itself to VRChat must do neither until somebody turns it on.
/// </remarks>
public class TalkSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-talk-settings-tests", Guid.NewGuid().ToString("n"));

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

    private TalkSettings Load() => CompanionSettings.Load(Path_, NoEnvironment).Talk;

    [Fact]
    public void OffWhenThereIsNoFileAtAll()
    {
        Assert.Equal(TalkSettings.Default, Load());
        Assert.False(Load().On);
    }

    [Fact]
    public void OffWhenTheObjectIsNotThere()
    {
        Write("""{ "pairingPage": "https://cats.example/pair", "voice": { "on": true } }""");

        Assert.False(Load().On);
    }

    [Fact]
    public void OffWhenTheObjectIsEmptyOrTheWrongShape()
    {
        Write("""{ "talk": { } }""");
        Assert.False(Load().On);

        Write("""{ "talk": { "on": "yes please" } }""");
        Assert.False(Load().On);
    }

    [Fact]
    public void ReadsTheSwitchAndTheTwoKeys()
    {
        Write("""{ "talk": { "on": true, "discordMuteKey": "f13", "discordDeafenKey": "mod+shift+d" } }""");

        Assert.Equal(new TalkSettings(true, "f13", "mod+shift+d"), Load());
    }

    [Fact]
    public void AKeyThatCannotBePressedReadsAsNotRecorded()
    {
        Write("""{ "talk": { "on": true, "discordMuteKey": "mod", "discordDeafenKey": "g e" } }""");

        Assert.Null(Load().DiscordMuteKey);
        Assert.Null(Load().DiscordDeafenKey);
    }

    [Fact]
    public void ASaveSurvivesAndAKeyNotRecordedIsLeftOutOfTheFile()
    {
        Assert.True(CompanionSettings.SaveTalk(Path_, new TalkSettings(true, "f13", null)));

        Assert.Equal(new TalkSettings(true, "f13", null), Load());

        var text = File.ReadAllText(Path_);
        Assert.Contains("\"discordMuteKey\": \"f13\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("discordDeafenKey", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingKeepsTheRestOfTheFile()
    {
        Write("""{ "pairingPage": "https://cats.example/pair", "checkForUpdates": false, "listenForPhrase": { "on": true } }""");

        Assert.True(CompanionSettings.SaveTalk(Path_, new TalkSettings(true, "f13", "f14")));

        var saved = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.Equal(new Uri("https://cats.example/pair"), saved.PairingPage);
        Assert.False(saved.CheckForUpdates);
        Assert.True(saved.Listening.On);
        Assert.Equal(new TalkSettings(true, "f13", "f14"), saved.Talk);
    }

    [Fact]
    public void ItGoesBackOffAgainAndTheFileSaysSo()
    {
        Assert.True(CompanionSettings.SaveTalk(Path_, new TalkSettings(true, "f13", "f14")));
        Assert.True(CompanionSettings.SaveTalk(Path_, new TalkSettings(false, "f13", "f14")));

        Assert.False(Load().On);
        Assert.Contains("\"on\": false", File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingTheListeningLeavesTheTalkAlone()
    {
        Assert.True(CompanionSettings.SaveTalk(Path_, new TalkSettings(true, "f13", "f14")));
        Assert.True(CompanionSettings.SaveListening(Path_, new Modbot.Companion.Listening.ListeningSettings(On: true)));

        Assert.Equal(new TalkSettings(true, "f13", "f14"), Load());
    }

    [Fact]
    public void ABrokenFileIsNotOverwritten()
    {
        Write("{ this is not json");

        Assert.False(CompanionSettings.SaveTalk(Path_, new TalkSettings(true, "f13", null)));
        Assert.Equal("{ this is not json", File.ReadAllText(Path_));
    }
}
