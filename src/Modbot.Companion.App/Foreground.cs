using System.Runtime.InteropServices;

namespace Modbot.Companion.App;

/// <summary>
/// Lets the copy of the companion that is already running come to the front.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this reads: nothing.</strong> <strong>What leaves the machine: nothing.</strong>
/// It asks Windows one thing and asks it about this program, not about anybody else's.
/// </para>
/// <para>
/// <strong>Why it has to exist.</strong> Windows does not let a program raise its own window
/// whenever it feels like it. <c>SetForegroundWindow</c> — which is what Avalonia's
/// <c>Activate()</c> ends up calling — is refused unless the program asking already owns the
/// foreground, because the alternative is every background program in the world stealing the
/// screen. So the running copy asks politely, Windows says no, and what the moderator sees is the
/// window coming back somewhere behind whatever they were looking at, or nothing but a taskbar
/// button flashing. The message arrived and was acted on; the raise is what was declined.
/// </para>
/// <para>
/// <strong>Who can give the right away.</strong> Only whoever holds it, and that is this copy:
/// the moderator just started it from the Start menu or the taskbar, so Windows counts it as the
/// foreground program for a moment. It hands that moment to the copy that owns the window and
/// then exits.
/// </para>
/// <para>
/// <strong>Why any program rather than a named one.</strong> The call takes a process id, and the
/// id of the copy already running could be had by asking Windows who owns the other end of the
/// pairing pipe. That question is deliberately held to one file in this client — it is how the
/// clip recorder names VRChat, and the source guards keep it there so the capability cannot
/// spread. Spending it here, to raise our own window, would be a poor trade. <c>ASFW_ANY</c>
/// needs no such question: the permission lasts until the next foreground change, and this copy
/// is gone milliseconds later. The broad-looking constant is the narrower capability.
/// </para>
/// </remarks>
internal static class Foreground
{
    /// <summary>
    /// <c>ASFW_ANY</c>: any program may take the foreground, rather than one named by its id.
    /// </summary>
    private const uint AnyProgram = 0xFFFFFFFF;

    /// <summary>
    /// Gives up this copy's claim on the foreground so the copy that owns the window can take it.
    /// </summary>
    /// <remarks>
    /// Failure is silent and harmless. The worst of it is the behaviour this exists to fix: the
    /// window comes back where it was rather than in front, which is still better than a second
    /// copy of the client.
    /// </remarks>
    public static void LetTheRunningCopyComeForward()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            AllowSetForegroundWindow(AnyProgram);
        }
        catch (DllNotFoundException)
        {
            // No user32: not a Windows this client can raise a window on anyway.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
