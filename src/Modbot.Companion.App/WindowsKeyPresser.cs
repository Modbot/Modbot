using System.Runtime.InteropServices;
using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;

namespace Modbot.Companion.App;

/// <summary>
/// Presses one key for Windows to deliver, so Discord sees the key its user recorded for it.
/// </summary>
/// <remarks>
/// <para><strong>It only presses; it never reads.</strong> Windows' <c>SendInput</c> takes a list of
/// key-down and key-up events and puts them in the keyboard's queue, as if a hand had pressed them.
/// There is no call here that asks which keys are held, no keyboard hook and no list of what was
/// typed. The keys pressed are the ones the person recorded in Settings for Discord's Toggle Mute
/// and Toggle Deafen, and only when Talk in Discord or VRChat is on and a button or hotkey asks.
/// <c>CompanionSourceGuardTests</c> fails the build if a key-reading call appears anywhere the
/// client ships.</para>
/// <para>Windows hands the press to whichever program wants it: Discord's own keybind, which it
/// listens for on every program, or, if the recorded key is an ordinary one, whichever window has
/// the keyboard. That is why Settings suggests a key nobody types, such as F13.</para>
/// <para>What leaves the machine: nothing. Nothing here reads a file or opens a socket.</para>
/// </remarks>
internal sealed class WindowsKeyPresser : IKeyPresser
{
    private const uint KeyboardInput = 1;

    private const uint KeyUp = 0x0002;

    private const int Control = 0x11;

    private const int Menu = 0x12;

    private const int ShiftKey = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public ushort Key;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public UIntPtr Extra;
    }

    /// <summary>Windows' INPUT: a type, then a union as big as its biggest member (the mouse's).</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeyboardEvent Keyboard;

        [FieldOffset(8)]
        public MouseEvent Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <inheritdoc />
    public bool Press(ShortcutKeys keys)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var down = new List<int>();
        if ((keys.Modifiers & ShortcutKeys.Ctrl) != 0)
            down.Add(Control);
        if ((keys.Modifiers & ShortcutKeys.Alt) != 0)
            down.Add(Menu);
        if ((keys.Modifiers & ShortcutKeys.Shift) != 0)
            down.Add(ShiftKey);

        var events = new List<Input>();
        foreach (var modifier in down)
            events.Add(Event(modifier, up: false));

        events.Add(Event(keys.Key, up: false));
        events.Add(Event(keys.Key, up: true));

        for (var i = down.Count - 1; i >= 0; i--)
            events.Add(Event(down[i], up: true));

        var array = events.ToArray();
        return SendInput((uint)array.Length, array, Marshal.SizeOf<Input>()) == array.Length;
    }

    private static Input Event(int key, bool up) => new()
    {
        Type = KeyboardInput,
        Keyboard = new KeyboardEvent { Key = (ushort)key, Flags = up ? KeyUp : 0 },
    };
}
