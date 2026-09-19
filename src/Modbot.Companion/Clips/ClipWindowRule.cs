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
/// The rules that keep a clip to VRChat's window: which part of the screen is copied, how much it
/// is shrunk, and what happens when the window is somewhere else, minimised, resized or gone.
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
/// <para><strong>Why the recorded size never changes.</strong> The encoder is told a frame size
/// once, when the clip's first file is opened, and a video file cannot change size part way
/// through. VRChat's window can — alt-tab, a resolution change, a window dragged bigger. So the
/// size is fixed from the window as it was when recording started, and a window that later differs
/// is shrunk until it fits inside that, with anything left over black. A resize costs a smaller
/// picture in the same frame; it never costs the file.</para>
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
    /// How many times to halve VRChat's window as it is now so it fits inside the size the clip is
    /// being recorded at. A window that has not changed gives the level it started at.
    /// </summary>
    public static int FitLevel(int windowWidth, int windowHeight, int recordedWidth, int recordedHeight)
    {
        var level = 0;
        while (level < MostHalvings
            && ((windowWidth >> level) > recordedWidth || (windowHeight >> level) > recordedHeight))
        {
            level++;
        }

        return level;
    }

    /// <summary>
    /// How much of the recorded frame VRChat's window fills at that level. Never larger than the
    /// frame; a window smaller than the frame leaves the rest black rather than stretching.
    /// </summary>
    public static (int Width, int Height) FittedSize(
        int windowWidth,
        int windowHeight,
        int recordedWidth,
        int recordedHeight,
        int level)
    {
        return (
            Math.Min(recordedWidth, Even(windowWidth >> level)),
            Math.Min(recordedHeight, Even(windowHeight >> level)));
    }

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
