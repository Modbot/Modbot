using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// A ray counts as on the panel only where something is drawn. The panel is one square picture
/// and most of it is clear ground; counting the whole square kept the cursor and the bar up after
/// the owner's hand had moved off everything visible.
/// </summary>
public class DrawnHereTests
{
    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    private static bool DrawnAt(Control root, double x, double y) => AvaloniaTestHost.Run(() =>
    {
        root.Measure(new Size(200, 200));
        root.Arrange(new Rect(0, 0, 200, 200));
        return OverlayTargets.Drawn(root, new Point(x, y));
    });

    [Fact]
    public void ACardCountsAndTheClearGroundAroundItDoesNot()
    {
        var root = AvaloniaTestHost.Run(() => (Control)new Border
        {
            Background = Brushes.Transparent,
            Child = new Border
            {
                Background = Brushes.DarkSlateBlue,
                Width = 100,
                Height = 50,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            },
        });

        Assert.True(DrawnAt(root, 50, 25));
        Assert.False(DrawnAt(root, 150, 150));
    }

    [Fact]
    public void SomethingFadedToNothingDoesNotCount()
    {
        // The bar is laid out at no opacity while it is hidden; it must not hold a ray.
        var root = AvaloniaTestHost.Run(() => (Control)new Border
        {
            Background = Brushes.DarkSlateBlue,
            Opacity = 0,
            Width = 200,
            Height = 200,
        });

        Assert.False(DrawnAt(root, 100, 100));
    }

    [Fact]
    public void ARayOnClearGroundIsNotAPointer()
    {
        var interaction = new OverlayInteraction(OverlayPlacement.Default) { IsDrawnAt = (_, _) => false };

        var result = interaction.Update(Hands.RightOnly(Hands.Hand(Hands.AimingAt(Vector3.Zero, Centre))), TimeSpan.Zero);

        Assert.Null(result.Pointer);
        Assert.False(result.RayOnPanel);
    }

    [Fact]
    public void ThePushSpeedSetsHowFarOnePollMovesIt()
    {
        var aim = Hands.AimingAt(Vector3.Zero, Centre);
        var interaction = new OverlayInteraction(OverlayPlacement.Default) { PushStep = 0.05f };
        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true)), TimeSpan.Zero);
        var before = Pose.From(interaction.Placement.Offset).Position.Length();

        interaction.Update(Hands.RightOnly(Hands.Hand(aim, grab: true, scroll: new Vector2(0, 1))), TimeSpan.FromMilliseconds(33));

        Assert.Equal(before + 0.05f, Pose.From(interaction.Placement.Offset).Position.Length(), 3);
    }
}
