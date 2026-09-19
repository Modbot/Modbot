using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The <c>desktopNotifyOverlay</c> object in <c>settings.json</c>: read, written, and never at the
/// cost of anything else already in the file.
/// </summary>
/// <remarks>
/// The client rewrites one object at a time, and a settings file is something people hand-edit.
/// Turning on the notification overlay must not cost somebody their voice settings.
/// </remarks>
public class DesktopNotifyFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-desktop-notify-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "settings.json");

    /// <summary>So a Modbot Cloud variable set on the machine running the tests changes nothing here.</summary>
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

    private string Read() => File.ReadAllText(Path_);

    [Fact]
    public void AFileThatSaysNothingLeavesItOff()
    {
        Assert.Equal(
            DesktopNotifySettings.Default,
            CompanionSettings.Load(Path_, NoEnvironment).DesktopNotifyOverlay);
    }

    [Fact]
    public void WhatTheFileSaysIsWhatIsUsed()
    {
        Write("""{ "desktopNotifyOverlay": { "on": true, "spot": "topleft", "seconds": 12 } }""");

        var settings = CompanionSettings.Load(Path_, NoEnvironment).DesktopNotifyOverlay;

        Assert.True(settings.On);
        Assert.Equal(ScreenSpot.TopLeft, settings.Spot);
        Assert.Equal(12f, settings.Seconds);
    }

    [Fact]
    public void AHalfWrittenObjectFallsBackFieldByField()
    {
        Write("""{ "desktopNotifyOverlay": { "on": true } }""");

        var settings = CompanionSettings.Load(Path_, NoEnvironment).DesktopNotifyOverlay;

        Assert.True(settings.On);
        Assert.Equal(DesktopNotifySettings.Default.Spot, settings.Spot);
        Assert.Equal(DesktopNotifySettings.DefaultSeconds, settings.Seconds);
    }

    [Fact]
    public void ASecondsOutsideTheRangeIsBroughtInside()
    {
        Write("""{ "desktopNotifyOverlay": { "on": true, "seconds": 9000 } }""");

        Assert.Equal(
            DesktopNotifySettings.MaxSeconds,
            CompanionSettings.Load(Path_, NoEnvironment).DesktopNotifyOverlay.Seconds);
    }

    [Fact]
    public void WritingItLeavesEverythingElseAlone()
    {
        Write("""
        {
          "pairingPage": "https://cats.example/pair",
          "overlayOn": false,
          "notifyOverlay": { "on": true, "spot": "topright" },
          "desktopOverlay": { "on": true, "shortcut": "mod+alt+m", "opacity": 70 },
          "voice": { "on": true, "name": "Bella" }
        }
        """);

        Assert.True(CompanionSettings.SaveDesktopNotifyOverlay(
            Path_,
            new DesktopNotifySettings(On: true, ScreenSpot.BottomLeft, 9)));

        var settings = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.True(settings.DesktopNotifyOverlay.On);
        Assert.Equal(ScreenSpot.BottomLeft, settings.DesktopNotifyOverlay.Spot);
        Assert.Equal(9f, settings.DesktopNotifyOverlay.Seconds);

        // And nothing else moved.
        Assert.Equal("https://cats.example/pair", settings.PairingPage.ToString());
        Assert.False(settings.OverlayOn);
        Assert.True(settings.NotifyOverlay.On);
        Assert.Equal(ScreenSpot.TopRight, settings.NotifyOverlay.Spot);
        Assert.True(settings.DesktopOverlay.On);
        Assert.Equal(70, settings.DesktopOverlay.Opacity);
        Assert.True(settings.Voice.On);
    }

    [Fact]
    public void AFileThatIsNotJsonIsLeftExactlyAsItWas()
    {
        Write("not json at all");

        Assert.False(CompanionSettings.SaveDesktopNotifyOverlay(Path_, DesktopNotifySettings.Default));
        Assert.Equal("not json at all", Read());
    }
}
