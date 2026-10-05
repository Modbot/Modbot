using System.Globalization;
using System.Text;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Core.Users;

namespace Modbot.Companion.TestRemote;

/// <summary>One command the test remote understands, read from one line of text.</summary>
public abstract record RemoteCommand
{
    /// <summary>What every panel is showing now.</summary>
    public sealed record State : RemoteCommand;

    /// <summary>A dashboard tab control, pressed by its label the way SteamVR's laser presses it.</summary>
    public sealed record Press(string Label) : RemoteCommand;

    /// <summary>A dashboard tab slider, pressed at a fraction of the way along its track.</summary>
    public sealed record Slide(string Label, double Fraction) : RemoteCommand;

    /// <summary>A trigger press on the main panel, at a point of its picture in pixels.</summary>
    public sealed record TapPanel(double X, double Y) : RemoteCommand;

    /// <summary>The thumbstick on the main panel's list, in whole rows; negative is up.</summary>
    public sealed record ScrollPanel(int Rows, double X, double Y) : RemoteCommand;

    /// <summary><strong>Put it back in front of me</strong>.</summary>
    public sealed record PutBack : RemoteCommand;

    /// <summary>One test event, as the Debug page's Send.</summary>
    public sealed record Event(TestEvent Test) : RemoteCommand;

    /// <summary>The Debug page's Send a run.</summary>
    public sealed record Run(TestEvent First) : RemoteCommand;

    /// <summary>Takes every pop-up card down.</summary>
    public sealed record ClearCards : RemoteCommand;

    /// <summary>A made-up hand takes hold of the main panel in its middle.</summary>
    public sealed record GrabPanel : RemoteCommand;

    /// <summary>The panel being held is carried to a place in front of the head, in metres.</summary>
    public sealed record MovePanel(float X, float Y, float Z) : RemoteCommand;

    /// <summary>The made-up hand lets the panel go.</summary>
    public sealed record ReleasePanel : RemoteCommand;

    /// <summary>Does nothing for a while, so a script can wait for a card or a redraw.</summary>
    public sealed record Wait(int Milliseconds) : RemoteCommand;
}

/// <summary>A line read: the command, or why it was not one.</summary>
public sealed record RemoteParse(RemoteCommand? Command, string? Error);

/// <summary>
/// Turns one line of text into a <see cref="RemoteCommand"/>. Words are split on spaces; a word
/// with spaces in it goes in double quotes.
/// </summary>
public static class RemoteCommands
{
    /// <summary>The longest <c>wait</c> allowed, so a typo cannot hold the remote for an hour.</summary>
    public const int LongestWait = 60_000;

    /// <summary>The command words, for the help line in an error.</summary>
    public const string Known =
        "state, press, slide, tap-panel, scroll-panel, put-back, event, run, clear-cards, grab-panel, move-panel, release-panel, wait";

    public static RemoteParse Parse(string? line)
    {
        var words = Words(line ?? string.Empty);
        if (words.Count == 0)
            return Fail("No command. Commands: " + Known);

        var name = words[0].ToLowerInvariant();
        var rest = words.Skip(1).ToList();

        return name switch
        {
            "state" => None(rest, new RemoteCommand.State()),
            "press" => rest.Count == 1 ? Ok(new RemoteCommand.Press(rest[0])) : Fail("press takes one label, in quotes if it has spaces."),
            "slide" => Slide(rest),
            "tap-panel" => TapPanel(rest),
            "scroll-panel" => ScrollPanel(rest),
            "put-back" => None(rest, new RemoteCommand.PutBack()),
            "event" => Event(rest),
            "run" => Run(rest),
            "clear-cards" => None(rest, new RemoteCommand.ClearCards()),
            "grab-panel" => None(rest, new RemoteCommand.GrabPanel()),
            "move-panel" => MovePanel(rest),
            "release-panel" => None(rest, new RemoteCommand.ReleasePanel()),
            "wait" => Wait(rest),
            _ => Fail($"Unknown command \"{words[0]}\". Commands: {Known}"),
        };
    }

    /// <summary>The line's words: split on spaces, with double quotes keeping spaces in.</summary>
    public static IReadOnlyList<string> Words(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var any = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    words.Add(word.ToString());

                word.Clear();
                any = false;
                continue;
            }

            word.Append(c);
            any = true;
        }

        if (any)
            words.Add(word.ToString());

        return words;
    }

    /// <summary>
    /// The person a test event is about, or why not. Test events only make up people; a VRChat
    /// user id, or anything whose id would not carry the test marker, is refused.
    /// </summary>
    public static string? RefuseRealPerson(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.StartsWith("usr_", StringComparison.OrdinalIgnoreCase))
            return "Test people only: give a made-up name, not a VRChat user id.";

        return TestPeople.IsTest(TestEvents.SubjectOf(trimmed))
            ? null
            : "Test people only.";
    }

    private static RemoteParse Slide(List<string> rest)
    {
        if (rest.Count != 2)
            return Fail("slide takes a label and a number from 0 to 1.");

        if (!Number(rest[1], out var fraction) || fraction is < 0 or > 1)
            return Fail("slide's number goes from 0 to 1.");

        return Ok(new RemoteCommand.Slide(rest[0], fraction));
    }

    private static RemoteParse TapPanel(List<string> rest)
    {
        if (rest.Count != 2 || !Number(rest[0], out var x) || !Number(rest[1], out var y))
            return Fail("tap-panel takes x and y, in pixels of the panel's picture.");

        return x < 0 || y < 0
            ? Fail("tap-panel's x and y start at 0.")
            : Ok(new RemoteCommand.TapPanel(x, y));
    }

    private static RemoteParse ScrollPanel(List<string> rest)
    {
        if (rest.Count is not (1 or 3) || !int.TryParse(rest[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rows))
            return Fail("scroll-panel takes a number of rows (negative is up), and optionally x and y to point at.");

        if (rows == 0 || Math.Abs(rows) > 50)
            return Fail("scroll-panel's rows go from -50 to 50, and not 0.");

        // Without a point, the middle of the picture: the list is there on every list screen.
        double x = -1, y = -1;
        if (rest.Count == 3 && (!Number(rest[1], out x) || !Number(rest[2], out y) || x < 0 || y < 0))
            return Fail("scroll-panel's x and y are pixels of the panel's picture, from 0.");

        return Ok(new RemoteCommand.ScrollPanel(rows, x, y));
    }

    private static RemoteParse MovePanel(List<string> rest)
    {
        if (rest.Count != 3
            || !Number(rest[0], out var x)
            || !Number(rest[1], out var y)
            || !Number(rest[2], out var z))
        {
            return Fail("move-panel takes x, y and z in metres from the head: right, up, and back (in front is negative z).");
        }

        if (Math.Abs(x) > 5 || Math.Abs(y) > 5 || Math.Abs(z) > 5)
            return Fail("move-panel stays within 5 metres of the head.");

        return Ok(new RemoteCommand.MovePanel((float)x, (float)y, (float)z));
    }

    private static RemoteParse Wait(List<string> rest)
    {
        if (rest.Count != 1 || !int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            return Fail("wait takes a number of milliseconds.");

        return ms > LongestWait
            ? Fail($"wait is at most {LongestWait} milliseconds.")
            : Ok(new RemoteCommand.Wait(ms));
    }

    private static RemoteParse Event(List<string> rest)
    {
        if (rest.Count < 2)
            return Fail("event takes a kind and a name, then optionally a rank, 18+ and a reason.");

        if (Kind(rest[0]) is not { } kind)
            return Fail($"Unknown event kind \"{rest[0]}\". Kinds: {string.Join(", ", Enum.GetNames<TestEventKind>().Select(Dashed))}");

        return Person(rest.Skip(1).ToList(), allowReason: true) is { Error: null, Test: { } test }
            ? Ok(new RemoteCommand.Event(test with { Kind = kind }))
            : Fail(Person(rest.Skip(1).ToList(), allowReason: true).Error!);
    }

    private static RemoteParse Run(List<string> rest)
    {
        if (rest.Count == 0)
            return Ok(new RemoteCommand.Run(new TestEvent(TestEventKind.Joined)));

        var person = Person(rest, allowReason: false);
        return person.Test is { } test
            ? Ok(new RemoteCommand.Run(test))
            : Fail(person.Error!);
    }

    /// <summary>A name, then optionally a rank, the 18+ mark and (for an event) a reason.</summary>
    private static (TestEvent? Test, string? Error) Person(List<string> words, bool allowReason)
    {
        var name = words[0];
        if (RefuseRealPerson(name) is { } refused)
            return (null, refused);

        TrustRank? rank = null;
        if (words.Count > 1)
        {
            if (!Rank(words[1], out rank))
                return (null, $"Unknown rank \"{words[1]}\". Ranks: none, {string.Join(", ", Enum.GetNames<TrustRank>())}");
        }

        var eighteenPlus = false;
        if (words.Count > 2)
        {
            if (!EighteenPlus(words[2], out eighteenPlus))
                return (null, $"\"{words[2]}\" is not 18+ or no.");
        }

        if (words.Count > 3 && !allowReason)
            return (null, "run takes a name, a rank and 18+, and nothing after them.");

        var reason = words.Count > 3 ? string.Join(' ', words.Skip(3)) : null;
        return (new TestEvent(TestEventKind.Joined, name, rank, eighteenPlus, reason), null);
    }

    private static TestEventKind? Kind(string word)
    {
        var bare = word.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<TestEventKind>(bare, ignoreCase: true, out var kind) && Enum.IsDefined(kind) && !int.TryParse(bare, out _)
            ? kind
            : null;
    }

    private static bool Rank(string word, out TrustRank? rank)
    {
        rank = null;
        if (word is "-" || word.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;

        var bare = word.Replace("-", string.Empty, StringComparison.Ordinal);
        if (Enum.TryParse<TrustRank>(bare, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(bare, out _))
        {
            rank = parsed;
            return true;
        }

        return false;
    }

    private static bool EighteenPlus(string word, out bool on)
    {
        switch (word.ToLowerInvariant())
        {
            case "18+" or "yes" or "true":
                on = true;
                return true;
            case "-" or "no" or "false":
                on = false;
                return true;
            default:
                on = false;
                return false;
        }
    }

    /// <summary>A kind's name as typed: <c>FlaggedJoin</c> is <c>flagged-join</c>.</summary>
    public static string Dashed(string name)
    {
        var dashed = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                dashed.Append('-');

            dashed.Append(char.ToLowerInvariant(name[i]));
        }

        return dashed.ToString();
    }

    private static bool Number(string word, out double value)
        => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static RemoteParse None(List<string> rest, RemoteCommand command)
        => rest.Count == 0 ? Ok(command) : Fail("That command takes nothing after it.");

    private static RemoteParse Ok(RemoteCommand command) => new(command, null);

    private static RemoteParse Fail(string error) => new(null, error);
}
