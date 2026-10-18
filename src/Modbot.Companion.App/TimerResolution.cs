using System.Runtime.InteropServices;

namespace Modbot.Companion.App;

/// <summary>
/// Asks Windows for a one-millisecond clock tick while a list is scrolling in the headset, and gives it back after.
/// </summary>
/// <remarks>
/// Windows ticks timers about every 15 milliseconds unless something asks for finer, which is as slow as 64 reads
/// of the controllers a second however short the timer is set. VRChat usually has asked already; this does not rely
/// on it. Held only while a list is moving, because a finer tick costs power the rest of the time for nothing.
/// </remarks>
internal sealed class TimerResolution
{
    private const uint Milliseconds = 1;

    private bool _raised;

    /// <summary>Whether the finer tick is being asked for.</summary>
    public bool Raised => _raised;

    /// <summary>Asks for the finer tick, or lets it go. Does nothing if it is already so, or off Windows.</summary>
    public void Set(bool fine)
    {
        if (fine == _raised || !OperatingSystem.IsWindows())
            return;

        _raised = fine;

        if (fine)
            _ = TimeBeginPeriod(Milliseconds);
        else
            _ = TimeEndPeriod(Milliseconds);
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
