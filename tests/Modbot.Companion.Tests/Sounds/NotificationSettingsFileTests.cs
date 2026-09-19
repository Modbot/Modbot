using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Sounds;

/// <summary>
/// The <c>notifications</c> object in <c>settings.json</c>: the sound on by default, every field
/// optional, the tray notice counted, and a save that leaves the rest of the file alone.
/// </summary>
public class NotificationSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-notification-settings-tests", Guid.NewGuid().ToString("n"));

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

    private NotificationSettings Load() => CompanionSettings.Load(Path_, NoEnvironment).Notifications;

    [Fact]
    public void OnByDefaultWithNoTrayNoticesShownYet()
    {
        var notifications = Load();

        Assert.True(notifications.Bleep);
        Assert.Equal(NotificationSettings.DefaultVolume, notifications.Volume);
        Assert.Equal(0, notifications.TrayNoticesShown);
        Assert.Equal(NotificationSettings.Default, notifications);
    }

    [Fact]
    public void ReadsTheNotificationsObject()
    {
        Write("""
            {
              "notifications": { "bleep": false, "volume": 35, "trayNoticesShown": 2 }
            }
            """);

        var notifications = Load();

        Assert.False(notifications.Bleep);
        Assert.Equal(35, notifications.Volume);
        Assert.Equal(2, notifications.TrayNoticesShown);
    }

    [Fact]
    public void MissingFieldsInsideTheObjectTakeTheirDefaults()
    {
        Write("""{ "notifications": { "bleep": false } }""");

        var notifications = Load();

        Assert.False(notifications.Bleep);
        Assert.Equal(NotificationSettings.DefaultVolume, notifications.Volume);
        Assert.Equal(0, notifications.TrayNoticesShown);
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    public void VolumeIsKeptInsideTheSlider(int written, int read)
    {
        Write($$"""{ "notifications": { "volume": {{written}} } }""");

        Assert.Equal(read, Load().Volume);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(99, NotificationSettings.TrayNoticesToShow)]
    public void TheTrayNoticeCountIsKeptSensible(int written, int read)
    {
        Write($$"""{ "notifications": { "trayNoticesShown": {{written}} } }""");

        Assert.Equal(read, Load().TrayNoticesShown);
    }

    [Fact]
    public void AWrongShapeFallsBackToTheDefaultsRatherThanFailing()
    {
        Write("""{ "notifications": { "bleep": "yes", "volume": "loud" } }""");

        Assert.Equal(NotificationSettings.Default, Load());
    }

    [Fact]
    public void SavingKeepsTheRestOfTheFile()
    {
        Write("""{ "pairingPage": "https://cats.example/pair", "checkForUpdates": false, "voice": { "on": true } }""");

        Assert.True(CompanionSettings.SaveNotifications(
            Path_, new NotificationSettings(Bleep: false, Volume: 20, TrayNoticesShown: 1)));

        var saved = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.Equal(new Uri("https://cats.example/pair"), saved.PairingPage);
        Assert.False(saved.CheckForUpdates);
        Assert.True(saved.Voice.On);
        Assert.Equal(new NotificationSettings(Bleep: false, Volume: 20, TrayNoticesShown: 1), saved.Notifications);
    }

    [Fact]
    public void SavingTheVoiceLeavesTheNotificationsAlone()
    {
        Assert.True(CompanionSettings.SaveNotifications(Path_, new NotificationSettings(Volume: 15)));
        Assert.True(CompanionSettings.SaveVoice(Path_, new VoiceSettings(On: true)));

        Assert.Equal(15, Load().Volume);
    }

    [Fact]
    public void ABrokenFileIsNotOverwritten()
    {
        Write("{ this is not json");

        Assert.False(CompanionSettings.SaveNotifications(Path_, new NotificationSettings(Bleep: false)));
        Assert.Equal("{ this is not json", File.ReadAllText(Path_));
    }

    [Fact]
    public void TheTrayNoticeIsShownAFewTimesAndThenNeverAgain()
    {
        var notifications = NotificationSettings.Default;

        for (var shown = 0; shown < NotificationSettings.TrayNoticesToShow; shown++)
        {
            Assert.True(notifications.ShowTrayNotice);
            notifications = notifications.WithTrayNoticeShown();
        }

        Assert.False(notifications.ShowTrayNotice);
        Assert.Equal(NotificationSettings.TrayNoticesToShow, notifications.TrayNoticesShown);

        // And it stays where it is however many times the window is closed after that.
        Assert.Equal(notifications, notifications.WithTrayNoticeShown());
    }

    [Fact]
    public void GainIsTheVolumeAsAFraction()
    {
        Assert.Equal(0.7f, NotificationSettings.Default.Gain);
        Assert.Equal(0f, new NotificationSettings(Volume: 0).Gain);
        Assert.Equal(1f, new NotificationSettings(Volume: 100).Gain);
        Assert.Equal(1f, new NotificationSettings(Volume: 400).Gain);
    }
}
