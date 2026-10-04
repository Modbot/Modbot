using System.Runtime.InteropServices;

namespace Modbot.Companion.App;

/// <summary>
/// Says why this copy will not start, where the person who started it will see it.
/// </summary>
/// <remarks>
/// <para>Runs before the log and before the window, because the reason it will not start is the
/// folder the log would have gone into. Started from a terminal, the words go to that terminal;
/// started any other way on Windows, a plain message box says them, because a windowed program
/// has nowhere else to put them.</para>
/// <para>Nothing is read, written or sent.</para>
/// </remarks>
internal static class RefusedStart
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const uint MessageBoxOk = 0x0;
    private const uint MessageBoxError = 0x10;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    public static void Tell(string message)
    {
        if (!OperatingSystem.IsWindows() || Console.IsErrorRedirected || AttachConsole(AttachParentProcess))
        {
            Console.Error.WriteLine(message);
            return;
        }

        MessageBoxW(IntPtr.Zero, message, "Modbot", MessageBoxOk | MessageBoxError);
    }
}
