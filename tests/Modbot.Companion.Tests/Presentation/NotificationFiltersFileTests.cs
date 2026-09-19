using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The <c>notificationFilters</c> object in <c>settings.json</c>: the defaults for a file that
/// predates it, a save that leaves every other field alone, and a hand-edited file with a typo
/// that is never overwritten.
/// </summary>
public class NotificationFiltersFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-notification-filters-tests", Guid.NewGuid().ToString("n"));

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

    private NotificationFilters Load() => CompanionSettings.Load(Path_, NoEnvironment).NotificationFilters;

    [Fact]
    public void NoFileAtAllIsTheDefaults()
    {
        Assert.Equal(NotificationFilters.Default, Load());
    }

    [Fact]
    public void AFileWrittenBeforeThisFeatureKeepsBeingToldExactlyWhatItWasToldBefore()
    {
        // The settings a moderator who has never seen this card would have: the voice on, its
        // three switches as they came, and no filters object anywhere.
        Write("""
            {
              "voice": { "on": true, "volume": 80 },
              "notifications": { "bleep": true, "volume": 70 }
            }
            """);

        Assert.Equal(NotificationFilters.Default, Load());
    }

    [Fact]
    public void AFileWithTheVoiceSwitchedDownKeepsItSwitchedDown()
    {
        Write("""
            {
              "voice": { "on": true, "joins": false, "leaves": false, "flaggedJoins": true }
            }
            """);

        var filters = Load();

        Assert.False(filters.VoiceSays(NotificationKind.Joined));
        Assert.False(filters.VoiceSays(NotificationKind.Left));
        Assert.True(filters.VoiceSays(NotificationKind.FlaggedJoin));
    }

    [Fact]
    public void ReadsTheFiltersObject()
    {
        Write("""
            {
              "notificationFilters": {
                "popUp": ["joined", "left"],
                "sound": ["flagged join"],
                "voice": ["changed avatar"]
              }
            }
            """);

        var filters = Load();

        Assert.True(filters.PopUpShows(NotificationKind.Joined));
        Assert.True(filters.PopUpShows(NotificationKind.Left));
        Assert.False(filters.PopUpShows(NotificationKind.FlaggedJoin));
        Assert.True(filters.SoundPlays(NotificationKind.FlaggedJoin));
        Assert.False(filters.SoundPlays(NotificationKind.Joined));
        Assert.True(filters.VoiceSays(NotificationKind.ChangedAvatar));
        Assert.False(filters.VoiceSays(NotificationKind.Joined));
    }

    [Fact]
    public void TheFiltersWinOverTheVoicesOwnThreeSwitches()
    {
        // Once the object is in the file it is what the voice reads, whatever the older fields say.
        Write("""
            {
              "voice": { "on": true, "joins": false },
              "notificationFilters": { "voice": ["joined"] }
            }
            """);

        Assert.True(Load().VoiceSays(NotificationKind.Joined));
    }

    [Fact]
    public void SavingRoundTripsThroughTheFile()
    {
        var filters = NotificationFilters.Nothing
            .With(NotificationWay.PopUp, NotificationKind.AlreadyThere, true)
            .With(NotificationWay.Sound, NotificationKind.LogStopped, true)
            .With(NotificationWay.Voice, NotificationKind.Problem, true);

        Assert.True(CompanionSettings.SaveNotificationFilters(Path_, filters));

        Assert.Equal(filters, Load());
    }

    [Fact]
    public void SavingKeepsEveryOtherFieldInTheFile()
    {
        Write("""
            {
              "pairingPage": "https://my.modbot.co/go?redir=/pair",
              "checkForUpdates": false,
              "voice": { "on": true, "volume": 35 },
              "notifications": { "bleep": false, "volume": 20 }
            }
            """);

        Assert.True(CompanionSettings.SaveNotificationFilters(
            Path_,
            NotificationFilters.Nothing.With(NotificationWay.Sound, NotificationKind.Joined, true)));

        var settings = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.False(settings.CheckForUpdates);
        Assert.True(settings.Voice.On);
        Assert.Equal(35, settings.Voice.Volume);
        Assert.False(settings.Notifications.Bleep);
        Assert.Equal(20, settings.Notifications.Volume);
        Assert.True(settings.NotificationFilters.SoundPlays(NotificationKind.Joined));
    }

    [Fact]
    public void ABrokenFileIsNotOverwritten()
    {
        // Somebody hand-edited their settings and left a trailing comma. They keep their file.
        const string Broken = """
            {
              "checkForUpdates": false,
            }
            """;

        Write(Broken);

        Assert.False(CompanionSettings.SaveNotificationFilters(Path_, NotificationFilters.Everything));

        Assert.Equal(Broken, File.ReadAllText(Path_));
    }

    [Fact]
    public void AnUnreadableFileStillLoadsAsTheDefaults()
    {
        Write("this is not JSON at all");

        Assert.Equal(NotificationFilters.Default, Load());
    }

    [Fact]
    public void AFiltersObjectThatIsNotAnObjectIsIgnored()
    {
        Write("""{ "notificationFilters": "everything please" }""");

        Assert.Equal(NotificationFilters.Default, Load());
    }

    [Fact]
    public void TheFileIsWrittenInTheWordsAModeratorCanRead()
    {
        Assert.True(CompanionSettings.SaveNotificationFilters(
            Path_,
            NotificationFilters.Nothing.With(NotificationWay.PopUp, NotificationKind.FlaggedJoin, true)));

        var written = File.ReadAllText(Path_);

        Assert.Contains("notificationFilters", written, StringComparison.Ordinal);
        Assert.Contains("flagged join", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVoiceObjectIsNotTouchedBySavingTheFilters()
    {
        // Keeping the two in step is the application's job, not this writer's: this one rewrites
        // its own object and nothing else, the same as every other Save on this file.
        Write("""{ "voice": { "on": true, "joins": true } }""");

        Assert.True(CompanionSettings.SaveNotificationFilters(Path_, NotificationFilters.Nothing));

        Assert.True(CompanionSettings.Load(Path_, NoEnvironment).Voice.Joins);
    }
}
