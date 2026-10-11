namespace Modbot.Companion.Overlay;

/// <summary>Which thumbstick the shortcut that shows and hides the headset panel listens to.</summary>
public enum ShortcutStick
{
    /// <summary>No shortcut: the button is the only way to show and hide the panel.</summary>
    Off,

    /// <summary>The right controller's stick. The default.</summary>
    Right,

    /// <summary>The left controller's stick.</summary>
    Left,
}

/// <summary>Which way that stick has to be held.</summary>
public enum StickDirection
{
    /// <summary>Pulled towards you. The default.</summary>
    Back,

    /// <summary>Pushed away from you.</summary>
    Forward,

    Left,

    Right,
}

/// <summary>
/// The stick shortcut for the show and hide button: which stick, which way, and for how many
/// seconds it has to be held before the headset panel is shown or hidden.
/// </summary>
/// <remarks>
/// <para><strong>Three choices, kept apart.</strong> The stick can be turned off without losing the
/// direction it was set to, so turning the shortcut back on finds the settings as they were.</para>
/// <para><strong>Held, not tapped.</strong> A stick brushed against in passing, or pushed to scroll
/// a list or move a carried panel, must not hide the panel, so the shortcut counts only a stick
/// that stays where it is for the whole time.</para>
/// </remarks>
public readonly record struct ButtonShortcut(ShortcutStick Stick, StickDirection Direction, int Seconds)
{
    /// <summary>How long the stick is held, unless the settings say otherwise.</summary>
    public const int DefaultSeconds = 5;

    /// <summary>The shortest hold the settings accept.</summary>
    public const int MinSeconds = 1;

    /// <summary>The longest hold the settings accept.</summary>
    public const int MaxSeconds = 30;

    /// <summary>The hold times the settings page offers.</summary>
    public static IReadOnlyList<int> SecondsChoices { get; } = [2, 3, 5, 8];

    /// <summary>The right stick, pulled back, for five seconds.</summary>
    public static ButtonShortcut Default { get; } = new(ShortcutStick.Right, StickDirection.Back, DefaultSeconds);

    /// <summary>Whether any stick is listened to.</summary>
    public bool IsOn => Stick is not ShortcutStick.Off;

    /// <summary>How long the stick is held, as a time.</summary>
    public TimeSpan Hold => TimeSpan.FromSeconds(Math.Clamp(Seconds, MinSeconds, MaxSeconds));

    /// <summary>
    /// The shortcut in plain words, for the line under the button's label: "Right stick back 5s". Empty
    /// while the shortcut is off, so the button says nothing about a shortcut that is not there.
    /// </summary>
    public string Text => IsOn
        ? $"{Name(Stick)} {Word(Direction)} {Math.Clamp(Seconds, MinSeconds, MaxSeconds)}s"
        : "";

    /// <summary>A stick as the settings page names it.</summary>
    public static string Name(ShortcutStick stick) => stick switch
    {
        ShortcutStick.Right => "Right stick",
        ShortcutStick.Left => "Left stick",
        _ => "Off",
    };

    /// <summary>A direction as the settings page names it.</summary>
    public static string Name(StickDirection direction) => direction switch
    {
        StickDirection.Forward => "Forward",
        StickDirection.Left => "Left",
        StickDirection.Right => "Right",
        _ => "Back",
    };

    /// <summary>A direction in the middle of a sentence.</summary>
    private static string Word(StickDirection direction) => Name(direction).ToLowerInvariant();

    /// <summary>A stick as it is written to <c>settings.json</c>.</summary>
    public static string Written(ShortcutStick stick) => stick.ToString().ToLowerInvariant();

    /// <summary>A direction as it is written to <c>settings.json</c>.</summary>
    public static string Written(StickDirection direction) => direction.ToString().ToLowerInvariant();

    /// <summary>The stick a settings file names. Anything missing or unknown is the right one.</summary>
    public static ShortcutStick ParseStick(string? text)
        => Enum.TryParse<ShortcutStick>(text?.Trim(), ignoreCase: true, out var stick) && Enum.IsDefined(stick)
            ? stick
            : Default.Stick;

    /// <summary>The direction a settings file names. Anything missing or unknown is back.</summary>
    public static StickDirection ParseDirection(string? text)
        => Enum.TryParse<StickDirection>(text?.Trim(), ignoreCase: true, out var direction) && Enum.IsDefined(direction)
            ? direction
            : Default.Direction;

    /// <summary>The hold time a settings file gives, kept between the shortest and the longest. Missing is five seconds.</summary>
    public static int ClampSeconds(int? seconds)
        => Math.Clamp(seconds ?? DefaultSeconds, MinSeconds, MaxSeconds);
}
