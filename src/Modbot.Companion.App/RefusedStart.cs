using System.Runtime.InteropServices;

namespace Modbot.Companion.App;

/// <summary>
/// Says why this copy will not start, where the person who started it will see it.
/// </summary>
/// <remarks>
/// <para>Runs before the log and before the window, because the reason it will not start is the
/// folder the log would have gone into. Started from a terminal, or with its error output sent to
/// a file or a pipe, the words go there; started any other way on Windows, a plain message box
/// says them, because a windowed program has nowhere else to put them.</para>
/// <para>Nothing is read, written or sent.</para>
/// </remarks>
internal static class RefusedStart
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StandardError = -12;
    private const uint FileTypeDisk = 0x0001;
    private const uint FileTypePipe = 0x0003;
    private const uint MessageBoxOk = 0x0;
    private const uint MessageBoxError = 0x10;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    public static void Tell(string message)
    {
        if (!OperatingSystem.IsWindows() || AttachConsole(AttachParentProcess) || ErrorGoesSomewhere())
        {
            Console.Error.WriteLine(message);
            return;
        }

        MessageBoxW(IntPtr.Zero, message, "Modbot", MessageBoxOk | MessageBoxError);
    }

    /// <summary>Whether whoever started this copy sent its error output to a file or a pipe.</summary>
    private static bool ErrorGoesSomewhere()
    {
        var handle = GetStdHandle(StandardError);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return false;

        return GetFileType(handle) is FileTypeDisk or FileTypePipe;
    }
}
