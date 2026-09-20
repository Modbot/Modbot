namespace Modbot.Companion.Clips;

/// <summary>Where VRChat's window is, as the recorder last found it.</summary>
/// <param name="Found">Whether Windows knows of a VRChat window at all.</param>
/// <param name="InFront">Whether VRChat is the window the moderator is working in.</param>
/// <param name="Minimised">Whether it has been minimised, which leaves nothing to record.</param>
/// <param name="Width">How wide its picture is, in screen pixels.</param>
/// <param name="Height">How tall its picture is, in screen pixels.</param>
public readonly record struct GameWindow(bool Found, bool InFront, bool Minimised, int Width, int Height)
{
    /// <summary>Windows knows of no VRChat window.</summary>
    public static GameWindow Missing { get; } = new(false, false, false, 0, 0);

    /// <summary>Whether there is a picture to copy at all.</summary>
    public bool HasPicture => Found && !Minimised && Width >= 2 && Height >= 2;
}

/// <summary>What the recorder does with one turn.</summary>
public enum ClipFrame
{
    /// <summary>Copy what VRChat is showing.</summary>
    Copy,

    /// <summary>Keep the last picture of VRChat, and write that again.</summary>
    HoldLastPicture,
}

/// <summary>
/// Where VRChat's picture goes inside one frame of a clip: how much of it the graphics card can
/// halve for free, how big it is drawn, and where in the frame it starts.
/// </summary>
/// <param name="Level">
/// Which of the graphics card's ready-made smaller copies to read. Level 0 is the picture at full
/// size, level 1 is half as wide and half as tall, and so on. The one chosen is the smallest that
/// is still at least as big as the picture is drawn, so the card does as much of the shrinking as
/// it can and the processor is left with a step of less than half.
/// </param>
/// <param name="SourceWidth">How wide that copy is.</param>
/// <param name="SourceHeight">How tall that copy is.</param>
/// <param name="Width">How wide VRChat's picture is drawn in the frame.</param>
/// <param name="Height">How tall it is drawn.</param>
/// <param name="Left">How far in from the frame's left edge it starts.</param>
/// <param name="Top">How far down from the frame's top edge it starts.</param>
public readonly record struct ClipFit(
    int Level,
    int SourceWidth,
    int SourceHeight,
    int Width,
    int Height,
    int Left,
    int Top)
{
    /// <summary>Whether the copy is already exactly the size it is drawn, so nothing has to be scaled.</summary>
    public bool Exact => SourceWidth == Width && SourceHeight == Height;

    /// <summary>How much of the frame is left over, in pixels. Zero when the shapes match.</summary>
    public int LeftOver(int frameWidth, int frameHeight)
        => Math.Max(0, (frameWidth * frameHeight) - (Width * Height));
}

/// <summary>
/// The rules that keep a clip to VRChat's window: which part of the screen is copied, how it is
/// scaled to fill the frame, and what happens when the window is somewhere else, minimised,
/// resized or gone.
/// </summary>
/// <remarks>
/// <para><strong>It reads nothing and writes nothing.</strong> It is arithmetic over numbers the
/// recorder already has. The one file allowed to ask Windows where VRChat's window is, and the one
/// file allowed to copy a picture of a screen, is <c>ScreenRecording.cs</c>; this is the half of
/// that file's reasoning that can be checked without a screen.</para>
/// <para><strong>Why hold rather than stop.</strong> A moderator alt-tabs to Discord constantly,
/// and a recorder that wrote whatever was in front of VRChat would put their private messages in
/// the clip. A recorder that stopped instead would end the file at the first alt-tab and lose the
/// minutes that follow. So the last picture of VRChat is written again until VRChat is back: the
/// clip runs at a steady rate, holds only VRChat, and is never half a file.</para>
/// <para><strong>Why the frame's size never changes, and why the picture is scaled into it.</strong>
/// The encoder is told a frame size once, when the clip's first file is opened, and a video file
/// cannot change size part way through. VRChat's window can — alt-tab, a resolution change, a
/// window dragged bigger. So the frame is fixed from the window as it was when recording started,
/// and the window as it is now is <em>scaled</em> to fill that frame rather than drawn at whatever
/// size it happens to come out at. A window that changes size changes the scale; it does not
/// change how much of the frame is black.</para>
/// <para><strong>What used to happen, and why it was wrong.</strong> The picture used to be drawn
/// at whichever of the graphics card's ready-made smaller copies fitted inside the frame, and the
/// rest of the frame painted black. Halving is all those copies can do, so the picture only ever
/// filled the frame when the window happened to be an exact number of halvings bigger than it —
/// and one odd pixel was enough to force a halving too many and drop the picture to a quarter of
/// the frame in the top-left corner. That is what a moderator reported on 2026-09-19 and it is
/// what <see cref="Fit"/> replaces.</para>
/// <para><strong>Shape is kept.</strong> Scaling is by the same amount across and down, so nobody
/// in a clip is stretched. When the window's shape stops matching the frame's — a window dragged
/// from wide to tall — what is left over is the smallest it can be and is split evenly on both
/// sides, so the picture stays in the middle instead of sliding into a corner.</para>
/// </remarks>
public static class ClipWindowRule
{
    /// <summary>The widest a recorded picture may be. Anything wider is halved until it fits.</summary>
    public const int MaxWidth = 1280;

    /// <summary>How many times a picture may be halved before it is not worth keeping.</summary>
    private const int MostHalvings = 6;

    /// <summary>Whether this turn copies VRChat's picture or writes the last one again.</summary>
    public static ClipFrame Decide(GameWindow window)
        => window.HasPicture && window.InFront ? ClipFrame.Copy : ClipFrame.HoldLastPicture;

    /// <summary>
    /// The size a clip is recorded at: VRChat's window, halved until it is no wider than
    /// <see cref="MaxWidth"/>, then trimmed to even numbers because H.264 will not take odd ones.
    /// </summary>
    public static (int Width, int Height) RecordedSize(int windowWidth, int windowHeight)
    {
        var level = 0;
        while (level < MostHalvings && (windowWidth >> level) > MaxWidth)
            level++;

        return (Even(windowWidth >> level), Even(windowHeight >> level));
    }

    /// <summary>
    /// Where VRChat's window goes inside one frame: scaled to fill as much of it as its shape
    /// allows, centred, and read from the ready-made smaller copy closest above the size it is
    /// drawn at.
    /// </summary>
    /// <remarks>
    /// <para>The scale is the same across and down — whichever of the two is the tighter fit — so
    /// the picture is never stretched. One of the two dimensions therefore lands exactly on the
    /// frame's, and the other lands on it too whenever the shapes match, which is every frame of
    /// an ordinary clip.</para>
    /// <para>The level is the last one that is still no smaller than the size being drawn, so the
    /// graphics card does every halving it can and the processor is left with a step of less than
    /// half — and with no step at all when the scale happens to be an exact halving, which is the
    /// ordinary case of a window that has not changed.</para>
    /// </remarks>
    public static ClipFit Fit(int windowWidth, int windowHeight, int frameWidth, int frameHeight)
    {
        var width = Math.Max(2, windowWidth);
        var height = Math.Max(2, windowHeight);
        var frameAcross = Even(frameWidth);
        var frameDown = Even(frameHeight);

        // Whichever edge runs out first decides the scale, and the other is worked out from it so
        // the shape is kept. Multiplied out rather than divided, so the comparison is exact.
        int drawWidth;
        int drawHeight;

        if ((long)width * frameDown >= (long)height * frameAcross)
        {
            drawWidth = frameAcross;
            drawHeight = Even(Share(height, frameAcross, width));
        }
        else
        {
            drawHeight = frameDown;
            drawWidth = Even(Share(width, frameDown, height));
        }

        drawWidth = Math.Min(frameAcross, drawWidth);
        drawHeight = Math.Min(frameDown, drawHeight);

        var level = 0;
        while (level < MostHalvings
            && (width >> (level + 1)) >= drawWidth
            && (height >> (level + 1)) >= drawHeight)
        {
            level++;
        }

        return new ClipFit(
            level,
            Math.Max(1, width >> level),
            Math.Max(1, height >> level),
            drawWidth,
            drawHeight,

            // Split evenly, then taken down to an even number so a left-over strip never lands
            // half way through a pair of pixels the encoder treats as one.
            Middle(frameAcross - drawWidth),
            Middle(frameDown - drawHeight));
    }

    /// <summary>What <paramref name="value"/> becomes when <paramref name="of"/> becomes <paramref name="into"/>.</summary>
    private static int Share(int value, int into, int of)
        => (int)(((((long)value * into) * 2) + of) / (of * 2L));

    /// <summary>Half of what is left over, on an even pixel.</summary>
    private static int Middle(int over) => Math.Max(0, over / 2) & ~1;

    /// <summary>
    /// VRChat's window as a box inside one monitor, kept inside that monitor however far off the
    /// edge the window has been dragged. An empty answer means none of it is on this monitor.
    /// </summary>
    /// <remarks>
    /// Clamped rather than trusted. The numbers come from Windows about another program's window,
    /// and a box that ran past the edge of the monitor's own picture would be a copy off the end of
    /// a texture — which is a crash on somebody's PC rather than a wrong pixel.
    /// </remarks>
    public static (int X, int Y, int Width, int Height) BoxOnMonitor(
        int windowLeft,
        int windowTop,
        int windowWidth,
        int windowHeight,
        int monitorLeft,
        int monitorTop,
        int monitorWidth,
        int monitorHeight)
    {
        var left = Math.Clamp(windowLeft - monitorLeft, 0, Math.Max(0, monitorWidth));
        var top = Math.Clamp(windowTop - monitorTop, 0, Math.Max(0, monitorHeight));
        var right = Math.Clamp(windowLeft - monitorLeft + windowWidth, 0, Math.Max(0, monitorWidth));
        var bottom = Math.Clamp(windowTop - monitorTop + windowHeight, 0, Math.Max(0, monitorHeight));

        var width = right - left;
        var height = bottom - top;

        return width < 2 || height < 2
            ? (0, 0, 0, 0)
            : (left, top, width & ~1, height & ~1);
    }

    /// <summary>An even number, at least two: H.264 will not take odd sizes.</summary>
    private static int Even(int value) => Math.Max(2, value & ~1);
}
