using Modbot.Companion.Overlay;

namespace Modbot.Companion.Chat;

/// <summary>
/// Where a new pop-up chat first opens, so each sits a little off the last one instead of on top of it.
/// </summary>
/// <remarks>
/// <para>Number 1 is where the panel always opened. Each next number steps a little further, for
/// <see cref="Steps"/> numbers; the number after those starts the staircase again, shifted a little
/// so it does not land on the first.</para>
/// <para>In the headset the step is right and down, a little nearer the eyes so the newer one is in
/// front. On a monitor it is left and down: the window opens at the right-hand side or left of the
/// overlay, so left is the way that stays on the screen.</para>
/// </remarks>
public static class ChatCascade
{
    /// <summary>How many numbers make one staircase.</summary>
    public const int Steps = 8;

    /// <summary>One step in the headset, in metres, to the right and down.</summary>
    public const float HeadsetStep = 0.07f;

    /// <summary>The extra shift for each full staircase before this one, in metres, so a ninth does not stack on the first.</summary>
    public const float HeadsetCycleShift = 0.025f;

    /// <summary>One step towards the eyes, in metres, so a newer panel is drawn over an older one.</summary>
    public const float HeadsetCloser = 0.02f;

    /// <summary>One step on a monitor, in screen pixels.</summary>
    public const int DesktopStep = 32;

    /// <summary>The extra shift for each full staircase before this one, in screen pixels.</summary>
    public const int DesktopCycleShift = 12;

    /// <summary><paramref name="baseline"/> moved to where pop-up <paramref name="number"/> opens in the headset.</summary>
    public static OverlayPlacement ForHeadset(OverlayPlacement baseline, int number)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var (step, cycle) = Where(number);
        var along = (HeadsetStep * step) + (HeadsetCycleShift * cycle);

        return baseline with
        {
            Offset = baseline.Offset with
            {
                X = baseline.Offset.X + along,
                Y = baseline.Offset.Y - along,
                Z = baseline.Offset.Z + (HeadsetCloser * step),
            },
        };
    }

    /// <summary>How far pop-up <paramref name="number"/> sits from where number 1 would, on a monitor, in screen pixels: left and down.</summary>
    public static (int Left, int Down) ForDesktop(int number)
    {
        var (step, cycle) = Where(number);
        var along = (DesktopStep * step) + (DesktopCycleShift * cycle);

        return (-along, along);
    }

    private static (int Step, int Cycle) Where(int number)
    {
        var index = Math.Max(number, 1) - 1;

        return (index % Steps, index / Steps);
    }
}
