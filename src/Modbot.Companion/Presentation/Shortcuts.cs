namespace Modbot.Companion.Presentation;

/// <summary>Where a key is listed on the shortcut sheet.</summary>
public enum ShortcutGroup
{
    General,
    GoTo,
    Lists,
    Filters,
}

/// <summary>
/// One key the window answers to.
/// </summary>
/// <param name="Keys">
/// The keys, lower case, modifiers first: <c>mod+k</c>, <c>?</c>, <c>j</c>, <c>escape</c>,
/// <c>enter</c>, or a chord such as <c>g e</c> (press <c>g</c>, then <c>e</c> within a second).
/// <c>mod</c> is Ctrl; the companion has no Mac build.
/// </param>
/// <param name="Label">What it does, for the sheet and the command palette.</param>
/// <param name="Page">Belongs to the page behind the palette or the sheet: does not fire while the palette or the sheet is open.</param>
/// <param name="Hidden">Left off the sheet and the palette: a second spelling of a key that is already listed.</param>
public sealed record Shortcut(
    string Keys,
    string Label,
    ShortcutGroup Group,
    Action Run,
    bool Page = false,
    bool Hidden = false);

/// <summary>What one key press should do, decided against the registry.</summary>
/// <param name="Run">The shortcut to run, or null.</param>
/// <param name="Pending">The first key of a chord, to hold until the second arrives, or null.</param>
public readonly record struct ShortcutOutcome(Shortcut? Run, string? Pending)
{
    public static ShortcutOutcome Nothing => default;
}

/// <summary>
/// Key presses as tokens, and tokens as words.
/// </summary>
/// <remarks>
/// Shift is not written for printable keys: <c>?</c> is what the key produced, and <c>shift+/</c>
/// is what it took to produce it, which differs between keyboards. Shift is kept for named keys
/// such as <c>shift+enter</c>, where there is no character to speak for it.
/// </remarks>
public static class KeyTokens
{
    /// <summary>
    /// One key press as a token: <c>mod+k</c>, <c>?</c>, <c>j</c>, <c>escape</c>, <c>arrowdown</c>.
    /// </summary>
    /// <param name="key">
    /// The character the key produced (<c>j</c>, <c>?</c>) or the name of a key that produces
    /// none (<c>escape</c>, <c>enter</c>, <c>arrowdown</c>). Null for a modifier on its own.
    /// </param>
    public static string? Token(string? key, bool mod, bool alt, bool shift)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        var printable = key.Length == 1;
        var parts = new List<string>(4);
        if (mod)
            parts.Add("mod");
        if (alt)
            parts.Add("alt");
        if (shift && !printable)
            parts.Add("shift");
        parts.Add(key.ToLowerInvariant());

        return string.Join('+', parts);
    }

    /// <summary>The keys as a person reads them: <c>Ctrl K</c>, <c>G then E</c>, <c>?</c>.</summary>
    public static string Describe(string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return string.Join(" then ", keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(DescribeCombo));
    }

    /// <summary>One combo of a chord: <c>mod+k</c> as <c>Ctrl K</c>.</summary>
    public static string DescribeCombo(string combo)
    {
        ArgumentNullException.ThrowIfNull(combo);
        return string.Join(' ', combo.Split('+').Select(part => part switch
        {
            "mod" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            "escape" => "Esc",
            "enter" => "Enter",
            "arrowdown" => "↓",
            "arrowup" => "↑",
            "arrowleft" => "←",
            "arrowright" => "→",
            "space" => "Space",
            _ => part.Length == 1 ? part.ToUpperInvariant() : part,
        }));
    }
}

/// <summary>
/// The keyboard: one registry, one place that decides what a key does.
/// </summary>
/// <remarks>
/// <para>Modelled on the web app's keys (research 2026-09-16): <c>Ctrl+K</c> for the command
/// palette, <c>?</c> for the shortcut sheet, <c>g</c> then a letter to go to a page, <c>j</c>/<c>k</c>
/// to move in a list, <c>Enter</c> to open, <c>Esc</c> to close, <c>f</c> for a filter.</para>
/// <para>Two rules decide everything else here:</para>
/// <list type="bullet">
/// <item><strong>A key typed into a text box is text, never a shortcut.</strong> Only keys with
/// Ctrl held, and Escape, fire while a text box has focus. So <c>j</c> in the pairing box is a
/// letter and <c>Ctrl+K</c> there still opens the palette.</item>
/// <item><strong>A page's keys go quiet while a panel is open.</strong> <c>j</c> and <c>Enter</c>
/// belong to the list behind the palette, and moving that list's selection under a panel
/// nobody can see would be a surprise.</item>
/// </list>
/// <para>Shortcuts are registered in named sets, so a page can replace its own set when it is
/// shown without touching the window's. Later sets shadow earlier ones for the same keys, and
/// within a set the last one added wins.</para>
/// <para>No Avalonia in here, so the rules can be tested without a window.</para>
/// </remarks>
public sealed class ShortcutRegistry
{
    /// <summary>How long the first key of a chord waits for the second.</summary>
    public static readonly TimeSpan ChordTimeout = TimeSpan.FromSeconds(1);

    private readonly List<(string Set, Shortcut Shortcut)> _shortcuts = [];

    private readonly List<string> _sets = [];

    /// <summary>Replaces one named set of shortcuts. An empty list takes the set away.</summary>
    public void Set(string set, IEnumerable<Shortcut> shortcuts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(set);
        ArgumentNullException.ThrowIfNull(shortcuts);

        _shortcuts.RemoveAll(s => s.Set == set);
        if (!_sets.Contains(set))
            _sets.Add(set);

        foreach (var shortcut in shortcuts)
            _shortcuts.Add((set, shortcut));
    }

    /// <summary>Everything registered, in the order it was registered.</summary>
    public IReadOnlyList<Shortcut> All
        => [.. _sets.SelectMany(set => _shortcuts.Where(s => s.Set == set).Select(s => s.Shortcut))];

    /// <summary>
    /// What the sheet and the palette list: one row per key, the one that would fire, without
    /// the hidden ones, in group order.
    /// </summary>
    public IReadOnlyList<Shortcut> Listed
    {
        get
        {
            var byKeys = new Dictionary<string, Shortcut>(StringComparer.Ordinal);
            foreach (var shortcut in All)
                byKeys[shortcut.Keys] = shortcut;

            return [.. byKeys.Values.Where(s => !s.Hidden).OrderBy(s => s.Group)];
        }
    }

    /// <summary>
    /// Decides what one key press does.
    /// </summary>
    /// <param name="token">The press, from <see cref="KeyTokens.Token"/>.</param>
    /// <param name="pending">The first key of a chord pressed a moment ago, or null.</param>
    /// <param name="typing">Whether a text box has focus.</param>
    /// <param name="panelOpen">Whether the palette or the shortcut sheet is open.</param>
    public ShortcutOutcome Decide(string token, string? pending, bool typing, bool panelOpen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var candidates = All.Where(s => Allowed(s, typing, panelOpen)).ToList();
        var keys = candidates.Select(s => s.Keys).ToList();

        if (pending is not null)
        {
            var chord = $"{pending} {token}";
            if (keys.Contains(chord))
                return new ShortcutOutcome(Last(candidates, chord), null);

            // A chord that went nowhere falls through to the single key, so `g` then `j` still
            // moves the list rather than being swallowed.
        }

        if (keys.Any(k => k.StartsWith($"{token} ", StringComparison.Ordinal)))
            return new ShortcutOutcome(null, token);

        return keys.Contains(token)
            ? new ShortcutOutcome(Last(candidates, token), null)
            : ShortcutOutcome.Nothing;
    }

    private static bool Allowed(Shortcut shortcut, bool typing, bool panelOpen)
    {
        if (typing && !shortcut.Keys.Contains("mod+", StringComparison.Ordinal) && shortcut.Keys != "escape")
            return false;

        return !(panelOpen && shortcut.Page);
    }

    /// <summary>The last one registered wins, so a page can shadow a window key while it is open.</summary>
    private static Shortcut Last(List<Shortcut> candidates, string keys)
        => candidates.Last(s => s.Keys == keys);
}
