using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The <c>desktopOverlay</c> object in <c>settings.json</c>: read, written, and never at the cost
/// of anything else already in the file.
/// </summary>
/// <remarks>
/// The client rewrites one object at a time, and a settings file is something people hand-edit.
/// Losing somebody's voice settings because they moved an opacity slider would be a poor trade.
/// </remarks>
public class DesktopOverlayFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-desktop-overlay-tests", Guid.NewGuid().ToString("n"));

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
        Assert.Equal(DesktopOverlaySettings.Default, CompanionSettings.Load(Path_, NoEnvironment).DesktopOverlay);
    }

    [Fact]
    public void WhatTheFileSaysIsWhatIsUsed()
    {
        Write("""{ "desktopOverlay": { "on": true, "shortcut": "mod+shift+f9", "opacity": 55 } }""");

        var desktop = CompanionSettings.Load(Path_, NoEnvironment).DesktopOverlay;

        Assert.True(desktop.On);
        Assert.Equal("mod+shift+f9", desktop.Shortcut);
        Assert.Equal(55, desktop.Opacity);
    }

    [Fact]
    public void AHalfWrittenObjectFallsBackFieldByField()
    {
        Write("""{ "desktopOverlay": { "on": true } }""");

        var desktop = CompanionSettings.Load(Path_, NoEnvironment).DesktopOverlay;

        Assert.True(desktop.On);
        Assert.Equal(DesktopOverlaySettings.DefaultShortcut, desktop.Shortcut);
        Assert.Equal(DesktopOverlaySettings.DefaultOpacity, desktop.Opacity);
    }

    [Fact]
    public void AnOpacityOutsideTheRangeIsBroughtInside()
    {
        Write("""{ "desktopOverlay": { "opacity": 900 } }""");

        Assert.Equal(
            DesktopOverlaySettings.MaximumOpacity,
            CompanionSettings.Load(Path_, NoEnvironment).DesktopOverlay.Opacity);
    }

    [Fact]
    public void SavingItAndReadingItBackGivesTheSameThing()
    {
        var saved = new DesktopOverlaySettings(true, "mod+alt+k", 40);

        Assert.True(CompanionSettings.SaveDesktopOverlay(Path_, saved));
        Assert.Equal(saved, CompanionSettings.Load(Path_, NoEnvironment).DesktopOverlay);
    }

    [Fact]
    public void AShortcutNobodyCouldRegisterIsNotWrittenToTheFile()
    {
        Assert.True(CompanionSettings.SaveDesktopOverlay(Path_, new DesktopOverlaySettings(true, "wibble", 90)));

        Assert.Contains(DesktopOverlaySettings.DefaultShortcut, Read(), StringComparison.Ordinal);
        Assert.DoesNotContain("wibble", Read(), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingItKeepsEverythingElseInTheFile()
    {
        Write("""
            {
              "pairingPage": "https://modbot.example/pair",
              "checkForUpdates": false,
              "overlayOn": false,
              "voice": { "on": true, "volume": 40 },
              "eventsFilters": [ "kind:is:joined" ]
            }
            """);

        Assert.True(CompanionSettings.SaveDesktopOverlay(Path_, new DesktopOverlaySettings(true, "mod+alt+m", 70)));

        var settings = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.Equal(new Uri("https://modbot.example/pair"), settings.PairingPage);
        Assert.False(settings.CheckForUpdates);
        Assert.False(settings.OverlayOn);
        Assert.True(settings.Voice.On);
        Assert.Equal(40, settings.Voice.Volume);
        Assert.NotEmpty(settings.EventsFilters.Chips);
        Assert.True(settings.DesktopOverlay.On);
        Assert.Equal(70, settings.DesktopOverlay.Opacity);
    }

    [Fact]
    public void AFileWithATypoInItIsLeftAlone()
    {
        // A hand-edited file that is not JSON any more is not overwritten: the moderator's own
        // words are worth more than one switch.
        Write("{ not json at all");

        Assert.False(CompanionSettings.SaveDesktopOverlay(Path_, new DesktopOverlaySettings(true, "mod+alt+m", 90)));
        Assert.Equal("{ not json at all", Read());
    }

    [Fact]
    public void AFileThatIsNotThereYetIsMade()
    {
        Assert.True(CompanionSettings.SaveDesktopOverlay(Path_, new DesktopOverlaySettings(true, "mod+alt+m", 90)));
        Assert.True(File.Exists(Path_));
        Assert.True(CompanionSettings.Load(Path_, NoEnvironment).DesktopOverlay.On);
    }
}
