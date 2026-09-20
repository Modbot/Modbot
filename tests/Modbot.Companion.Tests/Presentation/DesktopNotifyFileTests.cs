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
    public void AMachineWithNoSettingsFileGetsTheNotificationOverlayOn()
    {
        // A first run takes the default, and the default became on on 2026-09-19: being told is
        // the reason somebody installs this, and the headset's notification panel has been on by
        // default since it was built.
        var settings = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.True(settings.DesktopNotifyOverlay.On);
        Assert.Equal(DesktopNotifySettings.Default, settings.DesktopNotifyOverlay);

        // And the headset's, which did not move.
        Assert.True(settings.NotifyOverlay.On);
    }

    [Fact]
    public void AFileWrittenBeforeThisWindowExistedLeavesItOff()
    {
        // The other half of the same decision. Somebody who already has a settings file has set
        // this machine up; a window appearing over whatever is on their monitor at the next update
        // would be the client changing something while their back was turned, not a default.
        Write("""{ "voice": { "on": true } }""");

        var settings = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.False(settings.DesktopNotifyOverlay.On);
        Assert.Equal(DesktopNotifySettings.NotAskedFor, settings.DesktopNotifyOverlay);

        // The headset's panel is not treated the same way, and that asymmetry is the point: a
        // panel somebody has to be wearing a headset to see interrupts nothing.
        Assert.True(settings.NotifyOverlay.On);
    }

    [Fact]
    public void AFileThatSaysItIsOffKeepsItOff()
    {
        Write("""{ "desktopNotifyOverlay": { "on": false } }""");

        Assert.False(CompanionSettings.Load(Path_, NoEnvironment).DesktopNotifyOverlay.On);
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
