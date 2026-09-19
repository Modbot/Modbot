using Modbot.Companion.Overlay;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The panel lets the world through everywhere a card is not. The texture is square and the
/// cards sit at its top, so an opaque ground would hang a dark slab over half of what the
/// moderator sees; and the frame is drawn into the same bitmap each time, so a taller screen
/// followed by a shorter one must not leave the old cards' pixels behind.
/// </summary>
public class OverlayTransparencyTests
{
    private const int Size = 256;

    private static byte Alpha(byte[] pixels, int x, int y) => pixels[((y * Size) + x) * 4 + 3];

    private static OverlayScreen Tall() => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext(
                "39911",
                [.. Enumerable.Range(0, 30).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh,
        Health: "Cannot reach Cat Lounge. Showing what was last known.");

    private static OverlayScreen IdleCard() => OverlayScreen.Idle with { ShowIdleCard = true };

    [Fact]
    public void TheGroundIsClearAndTheCardsAreNot()
    {
        var pixels = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            return renderer.Render(OverlayView.Build(IdleCard())).ToArray();
        });

        // The idle card is one short card at the top; the bottom of the panel is the world.
        Assert.Equal(0, Alpha(pixels, Size - 1, Size - 1));
        Assert.Equal(0, Alpha(pixels, Size / 2, Size - 8));

        // Inside the tabs, which are the first thing on the panel with a surface of its own. The
        // group's name and icon sit above them and paint no ground.
        Assert.Equal(255, Alpha(pixels, Size / 2, 80));
    }

    /// <summary>Outside a group instance the panel says nothing at all; only the debug page asks for the card.</summary>
    [Fact]
    public void TheLiveIdleScreenDrawsNothing()
    {
        var pixels = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            return renderer.Render(OverlayView.Build(OverlayScreen.Idle)).ToArray();
        });

        Assert.True(OverlayScreen.Idle.IsIdle);
        Assert.All(Enumerable.Range(0, Size * Size), i => Assert.Equal(0, pixels[(i * 4) + 3]));
    }

    [Fact]
    public void AShorterScreenLeavesNothingOfTheTallerOneBehind()
    {
        var (tall, idle) = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            var first = renderer.Render(OverlayView.Build(Tall())).ToArray();
            var second = renderer.Render(OverlayView.Build(IdleCard())).ToArray();
            return (first, second);
        });

        // Thirty rows and a banner reach the bottom of a 256-pixel frame; the idle card does not.
        Assert.Equal(255, Alpha(tall, Size / 2, Size - 8));
        Assert.Equal(0, Alpha(idle, Size / 2, Size - 8));
    }
}
