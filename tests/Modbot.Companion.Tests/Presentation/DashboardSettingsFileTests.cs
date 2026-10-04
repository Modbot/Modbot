using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The SteamVR dashboard tab saves through the same three writes the window uses: the
/// <c>overlayOn</c> switch, the whole <c>notifyOverlay</c> object and the whole
/// <c>notificationFilters</c> object. Made one after another, as a moderator going down the tab
/// makes them, each must keep what the others wrote.
/// </summary>
public class DashboardSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-dashboard-settings-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "settings.json");

    private static string? NoEnvironment(string name) => null;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EveryChangeTheTabMakesComesBackAfterARestart()
    {
        var notify = NotifyOverlaySettings.Default with
        {
            On = false,
            Spot = ScreenSpot.BottomLeft,
            Across = 0.1f,
            Down = -0.2f,
            Distance = 1.5f,
            Width = 0.4f,
            Opacity = 0.6f,
            Seconds = 9f,
        };
        var filters = NotificationFilters.Default
            .With(NotificationWay.PopUp, NotificationKind.Joined, true)
            .With(NotificationWay.PopUp, NotificationKind.Problem, false);

        Assert.True(CompanionSettings.SaveSwitch(Path_, CompanionSettings.OverlayOnField, false));
        Assert.True(CompanionSettings.SaveNotifyOverlay(Path_, notify));
        Assert.True(CompanionSettings.SaveNotificationFilters(Path_, filters));
        Assert.True(CompanionSettings.SaveOverlay(Path_, OverlayPlacement.Default with { Anchor = OverlayAnchor.World }));

        var loaded = CompanionSettings.Load(Path_, NoEnvironment);

        Assert.False(loaded.OverlayOn);
        Assert.Equal(notify.Clamped(), loaded.NotifyOverlay);
        Assert.Equal(filters, loaded.NotificationFilters);
        Assert.Equal(OverlayAnchor.World, loaded.Overlay.Anchor);
    }
}
