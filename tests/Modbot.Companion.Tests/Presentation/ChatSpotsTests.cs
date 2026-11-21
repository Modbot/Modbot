using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// Where each pop-up chat was left, by its number: a chat after the first is saved in its own spot and
/// found again when a chat opens with that number, and the first chat keeps its own fields.
/// </summary>
public class ChatSpotsTests : IDisposable
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
        OverlayAnchor.Head, new OverlayPose(-0.2f, -0.1f, -0.7f), 0.3f, Locked: true, ClickThrough: true);

    private static readonly OverlayPlacement MovedAgain = new(
        OverlayAnchor.Head, new OverlayPose(0.1f, 0.05f, -0.6f), 0.4f, Locked: false, ClickThrough: true);

    private static readonly DesktopChatSpot Window = new(120, 80, 500, 600);

    private static readonly DesktopChatSpot WindowAgain = new(-300, 40, 450, 560);

    [Fact]
    public void AFileWithNoSpotsReadsAsNoneForEveryNumber()
    {
        Write("""{ "overlayOn": false }""");

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);

        foreach (var number in new[] { 1, 2, 3, 9 })
        {
            Assert.Null(loaded.OverlayChatSpotFor(number));
            Assert.Null(loaded.DesktopChatSpotFor(number));
        }
    }

    [Fact]
    public void HeadsetSpotsForLaterChatsSurviveASaveAndALoadWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7 }""");

        var spots = ChatSpots<OverlayPlacement>.None.With(2, Moved).With(5, MovedAgain);
        Assert.True(CompanionSettings.SaveOverlayChatSpots(SettingsPath, spots));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(Moved, loaded.OverlayChatSpotFor(2));
        Assert.Equal(MovedAgain, loaded.OverlayChatSpotFor(5));
        Assert.Null(loaded.OverlayChatSpotFor(3));
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
    }

    [Fact]
    public void WindowSpotsForLaterChatsSurviveASaveAndALoadWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7 }""");

        var spots = ChatSpots<DesktopChatSpot>.None.With(2, Window).With(4, WindowAgain);
        Assert.True(CompanionSettings.SaveDesktopChatSpots(SettingsPath, spots));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(Window, loaded.DesktopChatSpotFor(2));
        Assert.Equal(WindowAgain, loaded.DesktopChatSpotFor(4));
        Assert.Null(loaded.DesktopChatSpotFor(3));
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
    }

    [Fact]
    public void TheFirstChatKeepsItsOwnFieldsAndTheLaterOnesDoNotTouchThem()
    {
        Write("""{ "overlayOn": false }""");

        var settings = CompanionSettings.Load(SettingsPath, NoEnvironment)
            .WithOverlayChatSpot(1, Moved)
            .WithOverlayChatSpot(2, MovedAgain)
            .WithDesktopChatSpot(1, Window)
            .WithDesktopChatSpot(2, WindowAgain);

        Assert.Equal(Moved, settings.OverlayChatSpot);
        Assert.Equal(Window, settings.DesktopChatSpot);
        Assert.Equal(MovedAgain, settings.OverlayChatSpotFor(2));
        Assert.Equal(WindowAgain, settings.DesktopChatSpotFor(2));
        Assert.Equal(Moved, settings.OverlayChatSpotFor(1));
        Assert.Equal(Window, settings.DesktopChatSpotFor(1));
        Assert.Equal(0, ChatSpots<OverlayPlacement>.None.Count);
        Assert.Equal(1, settings.OverlayChatSpots.Count);
        Assert.Equal(1, settings.DesktopChatSpots.Count);
    }

    [Fact]
    public void AllTheSpotsTogetherSurviveASaveAndALoad()
    {
        Write("""{ "overlayOn": true }""");

        var settings = CompanionSettings.Load(SettingsPath, NoEnvironment)
            .WithOverlayChatSpot(1, Moved)
            .WithOverlayChatSpot(3, MovedAgain)
            .WithDesktopChatSpot(1, Window)
            .WithDesktopChatSpot(3, WindowAgain);

        Assert.True(CompanionSettings.SaveOverlayChatSpot(SettingsPath, settings.OverlayChatSpot!));
        Assert.True(CompanionSettings.SaveOverlayChatSpots(SettingsPath, settings.OverlayChatSpots));
        Assert.True(CompanionSettings.SaveDesktopChatSpot(SettingsPath, settings.DesktopChatSpot!));
        Assert.True(CompanionSettings.SaveDesktopChatSpots(SettingsPath, settings.DesktopChatSpots));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);

        foreach (var number in new[] { 1, 3 })
        {
            Assert.Equal(settings.OverlayChatSpotFor(number), loaded.OverlayChatSpotFor(number));
            Assert.Equal(settings.DesktopChatSpotFor(number), loaded.DesktopChatSpotFor(number));
        }

        Assert.Null(loaded.OverlayChatSpotFor(2));
        Assert.Null(loaded.DesktopChatSpotFor(2));
    }

    [Fact]
    public void ANameThatIsNotANumberFromTwoUpAndAnUnreadableSpotAreLeftOut()
    {
        Write("""
            {
              "overlayChatSpots": { "1": {}, "0": {}, "-2": {}, "two": {}, "3": "no", "4": {} },
              "desktopChatSpots": { "1": { "left": 1, "top": 2 }, "x": { "left": 1, "top": 2 }, "3": { "left": "no" }, "4": { "left": 10, "top": 20 } }
            }
            """);

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);

        Assert.Equal(1, loaded.OverlayChatSpots.Count);
        Assert.NotNull(loaded.OverlayChatSpotFor(4));
        Assert.Null(loaded.OverlayChatSpotFor(3));

        Assert.Equal(1, loaded.DesktopChatSpots.Count);
        Assert.Equal(10, loaded.DesktopChatSpotFor(4)!.Left);
        Assert.Null(loaded.DesktopChatSpotFor(3));
    }

    [Fact]
    public void ASizeOutsideItsBoundsIsBroughtInsideWhenASpotIsKept()
    {
        var settings = CompanionSettings.Default.WithDesktopChatSpot(2, new DesktopChatSpot(1, 2, 99999, 1));

        var kept = settings.DesktopChatSpotFor(2)!;
        Assert.Equal(DesktopChatSpot.ClampWidth(99999), kept.Width);
        Assert.Equal(DesktopChatSpot.ClampHeight(1), kept.Height);
    }
}
