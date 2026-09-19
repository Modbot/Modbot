using System.Text.Json.Nodes;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The notification overlay's own settings, in their own object: read back as written, brought
/// inside their bounds when somebody hand-edits the file, and — above all — written without
/// disturbing anything else in it.
/// </summary>
public class NotifyOverlaySettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-notify-settings-tests", Guid.NewGuid().ToString("n"));

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

    [Fact]
    public void AFileWithNoNotifyOverlayGetsTheDefaults()
    {
        Write("""{ "checkForUpdates": false }""");

        var settings = CompanionSettings.Load(Path_, NoEnvironment).NotifyOverlay;

        Assert.Equal(NotifyOverlaySettings.Default, settings);
        Assert.True(settings.On);
        Assert.Equal(ScreenSpot.TopRight, settings.Spot);
    }

    [Fact]
    public void WhatIsWrittenIsWhatIsReadBack()
    {
        var chosen = new NotifyOverlaySettings(
            On: false,
            Spot: ScreenSpot.BottomLeft,
            Across: 0.05f,
            Down: -0.02f,
            Distance: 1.4f,
            Width: 0.5f,
            Opacity: 0.6f,
            Seconds: 12f);

        Assert.True(CompanionSettings.SaveNotifyOverlay(Path_, chosen));

        Assert.Equal(chosen, CompanionSettings.Load(Path_, NoEnvironment).NotifyOverlay);
    }

    [Fact]
    public void WritingItLeavesEveryOtherFieldAlone()
    {
        // Three other agents' settings live in this file. A writer that rewrote the whole document
        // would quietly drop whichever fields it did not know about.
        Write("""
        {
          "pairingPage": "https://modbot.example/pair",
          "overlayOn": false,
          "overlay": { "anchor": "lefthand", "width": 0.6 },
          "voice": { "on": true, "volume": 40 }
        }
        """);

        Assert.True(CompanionSettings.SaveNotifyOverlay(Path_, NotifyOverlaySettings.Default with { Spot = ScreenSpot.TopLeft }));

        var settings = CompanionSettings.Load(Path_, NoEnvironment);
        Assert.Equal(new Uri("https://modbot.example/pair"), settings.PairingPage);
        Assert.False(settings.OverlayOn);
        Assert.Equal(OverlayAnchor.LeftHand, settings.Overlay.Anchor);
        Assert.Equal(0.6f, settings.Overlay.Width, 3);
        Assert.True(settings.Voice.On);
        Assert.Equal(ScreenSpot.TopLeft, settings.NotifyOverlay.Spot);
    }

    [Fact]
    public void AHandEditedFileCannotPutThePanelWhereNobodyCanFindIt()
    {
        Write("""
        {
          "notifyOverlay": {
            "spot": "nowhere",
            "distance": 400,
            "width": -3,
            "opacity": 0,
            "seconds": 9000,
            "across": 50
          }
        }
        """);

        var settings = CompanionSettings.Load(Path_, NoEnvironment).NotifyOverlay;

        Assert.Equal(NotifyOverlaySettings.Default.Spot, settings.Spot);
        Assert.Equal(NotifyOverlaySettings.MaxDistance, settings.Distance);
        Assert.Equal(NotifyOverlaySettings.MinWidth, settings.Width);
        Assert.Equal(NotifyOverlaySettings.MinOpacity, settings.Opacity);
        Assert.Equal(NotifyOverlaySettings.MaxSeconds, settings.Seconds);
        Assert.Equal(NotifyOverlaySettings.MaxFine, settings.Across);
    }

    [Fact]
    public void ATypoInTheFileIsNotOverwritten()
    {
        Write("{ not json");

        Assert.False(CompanionSettings.SaveNotifyOverlay(Path_, NotifyOverlaySettings.Default));
        Assert.Equal("{ not json", File.ReadAllText(Path_));
    }

    [Theory]
    [InlineData(ScreenSpot.TopLeft, -1, 1)]
    [InlineData(ScreenSpot.TopMiddle, 0, 1)]
    [InlineData(ScreenSpot.TopRight, 1, 1)]
    [InlineData(ScreenSpot.BottomLeft, -1, -1)]
    [InlineData(ScreenSpot.BottomMiddle, 0, -1)]
    [InlineData(ScreenSpot.BottomRight, 1, -1)]
    public void EachSpotPutsThePanelInItsOwnCornerOfTheView(ScreenSpot spot, int across, int down)
    {
        var placement = (NotifyOverlaySettings.Default with { Spot = spot }).ToPlacement();

        Assert.Equal(OverlayAnchor.Head, placement.Anchor);
        Assert.Equal(across, Math.Sign(placement.Offset.X));
        Assert.Equal(down, Math.Sign(placement.Offset.Y));

        // Forward is -Z, so the panel is always in front of the moderator.
        Assert.True(placement.Offset.Z < 0f);
    }

    [Fact]
    public void MovingThePanelFurtherAwayKeepsItInTheSamePlaceInTheView()
    {
        // The spot is an angle, not a length. A panel pushed out to arm's length and beyond must
        // stay in the corner of the eye rather than sliding towards the middle of the view.
        var near = (NotifyOverlaySettings.Default with { Distance = 1f }).ToPlacement();
        var far = (NotifyOverlaySettings.Default with { Distance = 2f }).ToPlacement();

        Assert.Equal(near.Offset.X / near.Offset.Z, far.Offset.X / far.Offset.Z, 4);
        Assert.Equal(near.Offset.Y / near.Offset.Z, far.Offset.Y / far.Offset.Z, 4);
    }

    [Fact]
    public void TheFineOffsetMovesItOnTopOfTheSpot()
    {
        var plain = NotifyOverlaySettings.Default.ToPlacement();
        var nudged = (NotifyOverlaySettings.Default with { Across = 0.1f, Down = 0.05f }).ToPlacement();

        Assert.Equal(plain.Offset.X + 0.1f, nudged.Offset.X, 4);
        Assert.Equal(plain.Offset.Y - 0.05f, nudged.Offset.Y, 4);
    }

    [Fact]
    public void AnEmptyObjectIsTheDefaults()
    {
        Assert.Equal(NotifyOverlaySettings.Default, NotifyOverlaySettings.FromJson(new JsonObject()));
        Assert.Equal(NotifyOverlaySettings.Default, NotifyOverlaySettings.FromJson(null));
    }
}
