using System.Runtime.InteropServices;
using Modbot.Companion.Clips;

namespace Modbot.Companion.App;

/// <summary>VRChat's window as Windows last described it, and where it is on the desktop.</summary>
/// <param name="Handle">What Windows calls that window, or zero when there is none.</param>
/// <param name="Window">Whether it is there, in front, minimised, and how big its picture is.</param>
/// <param name="Left">Where its picture starts across the whole desktop.</param>
/// <param name="Top">Where its picture starts down the whole desktop.</param>
/// <param name="Owner">
/// Which process drew that window, or zero when Windows would not say. It is asked of the one
/// window that was already found by name, never of a list, and it is the whole of how the sound
/// in a clip can be VRChat's rather than the machine's.
/// </param>
internal readonly record struct GameWindowLook(
    nint Handle,
    GameWindow Window,
    int Left,
    int Top,
    uint Owner = 0)
{
    public int CentreX => Left + (Window.Width / 2);

    public int CentreY => Top + (Window.Height / 2);
}

/// <summary>
/// The one place in Modbot's client that asks Windows about another program's window.
/// </summary>
/// <remarks>
/// <para>It asks for <em>one named window</em> — VRChat's — and never for a list. The client
/// does not enumerate windows and does not enumerate processes, and
/// <c>CompanionSourceGuardTests</c> fails the build if any other file the client ships learns
/// any of these calls.</para>
/// <para>What comes back is where VRChat is drawn, how big its picture is, whether it is the
/// window in front and whether it is minimised. The clip recorder uses that to keep a clip to
/// VRChat: it copies that rectangle and nothing else, and writes the last picture of VRChat again
/// while the moderator is working somewhere else. The desktop overlay uses the same answer to sit
/// beside VRChat's own menu and to shrink and grow with VRChat's window (Escape Menu design §2.2
/// and §3.3). Two users, one question, asked in one place.</para>
/// <para>It sends VRChat nothing: it does not move, resize, focus or press anything in its window,
/// and it reads no key.</para>
/// </remarks>
internal static class VRChatWindow
{
    /// <summary>The window class every Unity game's main window has.</summary>
    private const string UnityWindowClass = "UnityWndClass";

    /// <summary>VRChat's own window title.</summary>
    private const string Title = "VRChat";

    /// <summary>Where VRChat's window is right now. A handle of zero asks Windows for it afresh.</summary>
    public static GameWindowLook Look(nint known)
    {
        if (!OperatingSystem.IsWindows())
            return new GameWindowLook(0, GameWindow.Missing, 0, 0);

        var handle = known != 0 && IsWindow(known) && IsUnityWindow(known) ? known : Find();

        if (handle == 0)
            return new GameWindowLook(0, GameWindow.Missing, 0, 0);

        if (!GetClientRect(handle, out var client))
            return new GameWindowLook(handle, GameWindow.Missing with { Found = true }, 0, 0);

        var corner = new Point { X = client.Left, Y = client.Top };
        if (!ClientToScreen(handle, ref corner))
            return new GameWindowLook(handle, GameWindow.Missing with { Found = true }, 0, 0);

        // Which process drew this one window. One more named ask about the window already
        // found by name, and never a walk over what else is running: it is what lets the sound
        // in a clip be VRChat's own rather than everything the speakers are playing.
        _ = GetWindowThreadProcessId(handle, out var owner);

        return new GameWindowLook(
            handle,
            new GameWindow(
                Found: true,
                InFront: GetForegroundWindow() == handle,
                Minimised: IsIconic(handle),
                Width: client.Right - client.Left,
                Height: client.Bottom - client.Top),
            corner.X,
            corner.Y,
            owner);
    }

    /// <summary>
    /// VRChat's window, by class and title together. One named ask, never a walk over what else is
    /// open. The title alone is not enough: Steam's launch-options dialog for VRChat is also
    /// titled "VRChat", and the bubble and the overlay panel attached to it. Only a Unity game's
    /// window has that class, and that is how VRChat's is told from a dialog that borrows its name.
    /// </summary>
    private static nint Find() => FindWindowW(UnityWindowClass, Title);

    /// <summary>
    /// Whether a window already held is still a Unity game's window, so a handle that once
    /// belonged to some other window is not kept for as long as it lives.
    /// </summary>
    private static bool IsUnityWindow(nint handle)
    {
        var name = new char[UnityWindowClass.Length + 1];
        var length = GetClassNameW(handle, name, name.Length);
        return length == UnityWindowClass.Length
            && string.Equals(new string(name, 0, length), UnityWindowClass, StringComparison.Ordinal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hWnd, ref Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
