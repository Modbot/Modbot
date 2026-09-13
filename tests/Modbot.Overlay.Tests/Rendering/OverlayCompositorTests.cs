using Avalonia.Controls;
using Modbot.Overlay.Rendering;

namespace Modbot.Overlay.Tests.Rendering;

/// <summary>
/// The compositor's only job is to not do work. It shares a machine with a game that wants every
/// core, and an overlay texture is re-projected by the headset compositor without the application
/// being involved — so drawing anything the moderator would not see a difference in is pure cost.
/// </summary>
public class OverlayCompositorTests
{
    private sealed class CountingRenderer(int width, int height) : IFrameRenderer
    {
        private readonly byte[] _pixels = new byte[width * height * 4];

        public int Width => width;

        public int Height => height;

        public int Renders { get; private set; }

        public ReadOnlySpan<byte> Render(Control root)
        {
            Renders++;
            return _pixels;
        }

        public void Dispose() { }
    }

    private sealed class CountingSurface(int width, int height) : IOverlaySurface
    {
        public int Width => width;

        public int Height => height;

        public nint TextureHandle => 1;

        public int Uploads { get; private set; }

        public void Upload(ReadOnlySpan<byte> bgra) => Uploads++;

        public void Dispose() { }
    }

    // A Border is an AvaloniaObject, so even an empty one has to be built on Avalonia's thread.
    private static Control Root() => AvaloniaTestHost.Run(() => (Control)new Border());

    [Fact]
    public void DrawsOnceWhenFirstAsked()
    {
        var renderer = new CountingRenderer(64, 64);
        var surface = new CountingSurface(64, 64);
        using var compositor = new OverlayCompositor(renderer, surface);

        Assert.True(compositor.DrawIfChanged(Root()));
        Assert.Equal(1, renderer.Renders);
        Assert.Equal(1, surface.Uploads);
    }

    [Fact]
    public void DoesNothingAtAllWhenNothingHasChanged()
    {
        var renderer = new CountingRenderer(64, 64);
        var surface = new CountingSurface(64, 64);
        using var compositor = new OverlayCompositor(renderer, surface);

        compositor.DrawIfChanged(Root());

        for (var i = 0; i < 500; i++)
            Assert.False(compositor.DrawIfChanged(Root()));

        Assert.Equal(1, renderer.Renders);
        Assert.Equal(1, surface.Uploads);
        Assert.Equal(1, compositor.FramesDrawn);
    }

    [Fact]
    public void DrawsAgainOnceInvalidated()
    {
        var renderer = new CountingRenderer(64, 64);
        var surface = new CountingSurface(64, 64);
        using var compositor = new OverlayCompositor(renderer, surface);

        compositor.DrawIfChanged(Root());
        compositor.Invalidate();

        Assert.True(compositor.IsDirty);
        Assert.True(compositor.DrawIfChanged(Root()));
        Assert.Equal(2, renderer.Renders);
    }

    [Fact]
    public void RefusesARendererAndASurfaceOfDifferentSizes()
    {
        // A mismatch would upload a torn frame rather than fail, and a torn frame in a headset is
        // the kind of thing people report as "the overlay flickers sometimes".
        Assert.Throws<ArgumentException>(() =>
            new OverlayCompositor(new CountingRenderer(64, 64), new CountingSurface(128, 128)));
    }
}
