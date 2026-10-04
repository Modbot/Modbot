using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The SteamVR dashboard tab's page: every control it promises is drawn, each where the laser can
/// find it, and nothing runs off the bottom of the texture.
/// </summary>
public class DashboardViewTests
{
    private static (Control Root, byte[] Pixels) Render(DashboardScreen screen) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(DashboardHost.PageWidth, DashboardHost.PageHeight);
        var root = DashboardView.Build(screen, DashboardHost.PageWidth, DashboardHost.PageHeight);
        var pixels = renderer.Render(root).ToArray();
        return (root, pixels);
    });

    private static IReadOnlyList<PlacedDashboardTarget> Targets(DashboardScreen screen)
    {
        var (root, _) = Render(screen);
        return AvaloniaTestHost.Run(() => DashboardTargets.Find(root));
    }

    [Fact]
    public void ThePageDrawsSomething()
    {
        var (_, pixels) = Render(DashboardScreen.Default);

        Assert.Equal(DashboardHost.PageWidth * DashboardHost.PageHeight * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void EveryControlThePlanPromisesIsOnThePage()
    {
        var targets = Targets(DashboardScreen.Default).Select(p => p.Target).ToList();

        Assert.Contains(new DashboardTarget.Toggle(DashboardSwitch.Overlay), targets);
        Assert.Contains(new DashboardTarget.Toggle(DashboardSwitch.Notifications), targets);
        Assert.Contains(new DashboardTarget.PutBack(), targets);

        foreach (var anchor in Enum.GetValues<OverlayAnchor>())
            Assert.Contains(new DashboardTarget.FixTo(anchor), targets);

        foreach (var spot in Enum.GetValues<ScreenSpot>())
            Assert.Contains(new DashboardTarget.Spot(spot), targets);

        foreach (var kind in NotificationFilters.Kinds)
            Assert.Contains(new DashboardTarget.PopUp(kind), targets);

        foreach (var slider in Enum.GetValues<DashboardSlider>())
        {
            Assert.Contains(new DashboardTarget.Track(slider), targets);
            Assert.Contains(new DashboardTarget.Step(slider, -1), targets);
            Assert.Contains(new DashboardTarget.Step(slider, 1), targets);
        }
    }

    [Fact]
    public void EveryControlIsWholeOnTheTextureAndBigEnoughForALaser()
    {
        foreach (var placed in Targets(DashboardScreen.Default))
        {
            Assert.True(placed.Bounds.Bottom <= DashboardHost.PageHeight, $"{placed.Target} runs off the bottom at {placed.Bounds}.");
            Assert.True(placed.Bounds.Right <= DashboardHost.PageWidth, $"{placed.Target} runs off the right at {placed.Bounds}.");
            Assert.True(placed.Bounds.Height >= 52, $"{placed.Target} is only {placed.Bounds.Height} pixels tall.");
        }
    }

    [Fact]
    public void NoTwoControlsOverlap()
    {
        var targets = Targets(DashboardScreen.Default);

        for (var i = 0; i < targets.Count; i++)
        {
            for (var j = i + 1; j < targets.Count; j++)
                Assert.False(targets[i].Bounds.Intersects(targets[j].Bounds), $"{targets[i].Target} overlaps {targets[j].Target}.");
        }
    }

    [Fact]
    public void ADifferentSettingDrawsADifferentPicture()
    {
        var (_, before) = Render(DashboardScreen.Default);
        var (_, after) = Render(DashboardScreen.Default with { OverlayOn = false });

        Assert.NotEqual(before, after);
    }
}
