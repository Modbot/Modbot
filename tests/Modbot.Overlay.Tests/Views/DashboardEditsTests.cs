using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// What a press on the dashboard tab's sliders does to the notification overlay's settings: the
/// window's bounds and steps, never outside them.
/// </summary>
public class DashboardEditsTests
{
    [Fact]
    public void APlusMovesOneStepAndAMinusMovesItBack()
    {
        var start = NotifyOverlaySettings.Default;

        var up = DashboardEdits.Stepped(start, DashboardSlider.Distance, 1);
        Assert.Equal(start.Distance + 0.05f, up.Distance, 3);

        var back = DashboardEdits.Stepped(up, DashboardSlider.Distance, -1);
        Assert.Equal(start.Distance, back.Distance, 3);
    }

    [Theory]
    [InlineData(DashboardSlider.Across)]
    [InlineData(DashboardSlider.Down)]
    [InlineData(DashboardSlider.Distance)]
    [InlineData(DashboardSlider.Width)]
    [InlineData(DashboardSlider.Opacity)]
    [InlineData(DashboardSlider.Seconds)]
    public void NoSliderGoesPastEitherEnd(DashboardSlider slider)
    {
        var range = DashboardEdits.RangeOf(slider);

        var top = DashboardEdits.With(NotifyOverlaySettings.Default, slider, range.Maximum);
        Assert.Equal(range.Maximum, DashboardEdits.ValueOf(DashboardEdits.Stepped(top, slider, 1), slider), 3);

        var bottom = DashboardEdits.With(NotifyOverlaySettings.Default, slider, range.Minimum);
        Assert.Equal(range.Minimum, DashboardEdits.ValueOf(DashboardEdits.Stepped(bottom, slider, -1), slider), 3);

        Assert.Equal(range.Maximum, range.At(5), 3);
        Assert.Equal(range.Minimum, range.At(-5), 3);
    }

    [Fact]
    public void TheTrackSnapsToTheWindowsSteps()
    {
        var range = DashboardEdits.RangeOf(DashboardSlider.Seconds);

        Assert.Equal(16, range.At(0.5), 3);
        Assert.Equal(range.Minimum, range.Snap(double.NaN), 3);
    }

    [Fact]
    public void ASliderChangesItsOwnFieldAndNoOther()
    {
        var start = NotifyOverlaySettings.Default;
        var moved = DashboardEdits.With(start, DashboardSlider.Opacity, 0.5);

        Assert.Equal(0.5f, moved.Opacity, 3);
        Assert.Equal(start with { Opacity = moved.Opacity }, moved);
    }

    [Fact]
    public void TheLabelsReadTheWayTheWindowWritesThem()
    {
        var settings = NotifyOverlaySettings.Default;

        Assert.StartsWith("Pop-up stays ", DashboardEdits.Label(settings, DashboardSlider.Seconds));
        Assert.StartsWith("Distance ", DashboardEdits.Label(settings, DashboardSlider.Distance));
        Assert.EndsWith(" m", DashboardEdits.Label(settings, DashboardSlider.Width));
    }

    [Fact]
    public void TheGripSliderIsTheWindowsOneToFourteenAndNeverGoesPastEitherEnd()
    {
        var range = DashboardEdits.RangeOf(DashboardSlider.Grip);

        Assert.Equal(CompanionSettings.MinGripSensitivity, range.Minimum);
        Assert.Equal(CompanionSettings.MaxGripSensitivity, range.Maximum);
        Assert.Equal(CompanionSettings.MaxGripSensitivity, DashboardEdits.SteppedGrip(CompanionSettings.MaxGripSensitivity, 1));
        Assert.Equal(CompanionSettings.MinGripSensitivity, DashboardEdits.SteppedGrip(CompanionSettings.MinGripSensitivity, -1));
        Assert.Equal(CompanionSettings.DefaultGripSensitivity + 1, DashboardEdits.SteppedGrip(CompanionSettings.DefaultGripSensitivity, 1));
    }

    [Fact]
    public void TheGripLabelReadsTheWayTheWindowWritesIt()
    {
        var screen = DashboardScreen.Default with { GripSensitivity = 12 };

        Assert.Equal("Grip sensitivity 12", DashboardEdits.Label(screen, DashboardSlider.Grip));
        Assert.Equal(12, DashboardEdits.ValueOf(screen, DashboardSlider.Grip));
        Assert.StartsWith("Pop-up stays ", DashboardEdits.Label(screen, DashboardSlider.Seconds));
    }
}
