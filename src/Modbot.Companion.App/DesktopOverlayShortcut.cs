using System.Runtime.InteropServices;
using Avalonia.Threading;
using Modbot.Companion.Presentation;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// The one keyboard combination the client asks Windows for, so the desktop overlay can be brought
/// up while VRChat has the keyboard.
/// </summary>
/// <remarks>
/// <para><strong>It does not read the keyboard, and it cannot.</strong> Windows'
/// <c>RegisterHotKey</c> is a claim on exactly one combination: the client names the keys it wants,
/// and Windows sends one message when those keys are pressed and says nothing whatever about any
/// other key. There is no key list, no buffer and no callback that sees anything else. The
/// alternative — a low-level keyboard hook — would see every keystroke on the machine in every
/// program, which is the shape of a keylogger, and this client does not get one.
/// <c>CompanionSourceGuardTests</c> already fails the build if <c>GetAsyncKeyState</c> appears
/// anywhere the client ships, for the same reason.</para>
/// <para><strong>Nothing leaves the machine.</strong> Nothing here reads a file, opens a socket or
/// is told anything by a server. A press becomes one call on the client's own UI thread.</para>
/// <para><strong>Why a thread of its own.</strong> A combination registered with no window sends
/// its message to a <em>thread's</em> queue, and Avalonia's own loop throws away a message with no
/// window before the client could see it. So the registration lives on one thread that does
/// nothing else: it asks for the combination, waits for messages, and hands a press to the UI
/// thread. Closing it ends the thread and gives the combination back.</para>
/// <para><strong>Failing is ordinary.</strong> Another program may already own the combination.
/// Then this says so, the settings screen shows it, and everything else in the client — the log
/// reader, reporting, the headset panel, the voice — carries on untouched.</para>
/// </remarks>
internal sealed class DesktopOverlayShortcut : IDisposable
{
    /// <summary>Don't repeat while the keys are held: one press, one window.</summary>
    private const int NoRepeat = 0x4000;

    private const uint HotKeyMessage = 0x0312;

    private const uint QuitMessage = 0x0012;

    /// <summary>Asked for once so the thread has a message queue before anybody posts to it.</summary>
    private const uint PeekNoRemove = 0x0000;

    /// <summary>Only one combination is ever registered, so the id is a constant.</summary>
    private const int ShortcutId = 0xB07;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMessage message, IntPtr window, uint first, uint last);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out NativeMessage message, IntPtr window, uint first, uint last, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly Action _pressed;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _stopping;
    private volatile bool _granted;

    /// <param name="pressed">Run on the UI thread when the combination is pressed.</param>
    public DesktopOverlayShortcut(Action pressed)
        => _pressed = pressed ?? throw new ArgumentNullException(nameof(pressed));

    /// <summary>What the last <see cref="Ask"/> was told, for the Settings page.</summary>
    public ShortcutState State { get; private set; } = ShortcutState.Off;

    /// <summary>The combination that is registered right now, or null.</summary>
    public string? Registered { get; private set; }

    /// <summary>
    /// Asks Windows for a combination, giving back whatever was held before. A null or unreadable
    /// combination gives everything back and asks for nothing.
    /// </summary>
    public ShortcutState Ask(string? shortcut)
    {
        Release();

        if (shortcut is null)
            return State = ShortcutState.Off;

        if (!OperatingSystem.IsWindows())
            return State = ShortcutState.NotOnThisSystem;

        if (DesktopOverlayKeys.Read(shortcut) is not { } keys)
            return State = ShortcutState.NotUnderstood;

        // The thread's own answer, waited for here so the Settings page can say what happened
        // rather than "asking…".
        var answered = new ManualResetEventSlim(false);

        _stopping = false;
        _granted = false;
        _thread = new Thread(() => Run(keys, answered))
        {
            IsBackground = true,
            Name = "Modbot desktop overlay shortcut",
        };

        _thread.Start();
        answered.Wait(TimeSpan.FromSeconds(5));

        if (_granted)
        {
            Registered = shortcut;
            Log.Information("The desktop overlay's shortcut {Shortcut} is registered", KeyTokens.Describe(shortcut));
            return State = ShortcutState.Registered;
        }

        Release();
        Log.Warning(
            "The desktop overlay's shortcut {Shortcut} could not be registered; another program has it. The client carries on without it",
            KeyTokens.Describe(shortcut));

        return State = ShortcutState.Taken;
    }

    /// <summary>Gives the combination back and ends the thread. Safe to call when there is none.</summary>
    public void Release()
    {
        Registered = null;

        if (_thread is not { } thread)
            return;

        _stopping = true;

        if (OperatingSystem.IsWindows() && _threadId != 0)
            PostThreadMessageW(_threadId, QuitMessage, IntPtr.Zero, IntPtr.Zero);

        thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
        State = ShortcutState.Off;
    }

    public void Dispose() => Release();

    private void Run(ShortcutKeys keys, ManualResetEventSlim answered)
    {
        _threadId = GetCurrentThreadId();

        // Forces this thread's message queue into existence, so a stop posted from the UI thread
        // a moment later has somewhere to land.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PeekNoRemove);

        _granted = RegisterHotKey(IntPtr.Zero, ShortcutId, (uint)(keys.Modifiers | NoRepeat), (uint)keys.Key);
        answered.Set();

        if (!_granted)
            return;

        try
        {
            // GetMessage returns 0 for the quit message and -1 for an error; both end the loop.
            while (!_stopping && GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message != HotKeyMessage || (int)message.WParam != ShortcutId)
                    continue;

                // Back to the thread that owns the windows; nothing about the overlay may be
                // touched from here.
                Dispatcher.UIThread.Post(() => CrashGuard.Run("opening the desktop overlay", _pressed));
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, ShortcutId);
        }
    }
}
