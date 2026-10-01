using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;

namespace Modbot.Companion.Crash;

/// <summary>
/// The instance, as VRChat's log last described it, at the moment <strong>Copy crash details</strong>
/// was pressed.
/// </summary>
/// <param name="WorldName">The world's readable name, or null when the log has not said it.</param>
/// <param name="Instance">The instance, or null when the log names none.</param>
/// <param name="People">Who the log says is in it, with the avatar each was last seen wearing.</param>
public sealed record CrashScene(
    string? WorldName,
    InstanceLocation? Instance,
    IReadOnlyList<PersonHere> People);

/// <summary>
/// One block of text for a report to VRChat about somebody crashing an instance: which instance,
/// who was in it and what they were wearing, and VRChat's own <c>[Behaviour]</c> log lines from the
/// last few minutes.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> VRChat's staff ask for exactly this when somebody
/// reports a crasher — the instance, the people, the avatars and the log — and the companion is
/// already holding the first three in memory. Putting them together by hand, from a log file of
/// tens of thousands of lines, is what stops most crash reports being made at all.</para>
/// <para><strong>Nothing here leaves the machine.</strong> The text is put on the clipboard when the
/// moderator presses <strong>Copy crash details</strong>, and that is the end of it: it is not sent
/// to a paired server, not to Modbot Cloud, not to VRChat. Where it goes next is wherever the
/// moderator pastes it. The instance secret (<c>nonce</c>) is taken out of every line first, the
/// same way it is taken out of every location the companion reads.</para>
/// <para><strong>What it does not have.</strong> Avatar ids. VRChat's log names a remote person's
/// avatar by its display name only, by design (research note §4), so that is what is here.</para>
/// <para>Pure: no clock, no disk. The lines come from <see cref="RecentBehaviourLines"/>.</para>
/// </remarks>
public static class CrashDetails
{
    /// <summary>How far back the log lines go, from the last one VRChat wrote.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>At most this many lines, newest kept, so the block still fits in a report form.</summary>
    public const int MaxLines = 400;

    private static readonly Regex Nonce = new(@"~nonce\([^)]*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The block, or null when there is nothing to say — no instance and no log lines, which is
    /// what a companion sees before VRChat has been opened at all.
    /// </summary>
    /// <param name="scene">The instance and the people in it.</param>
    /// <param name="lines">Raw <c>[Behaviour]</c> lines, oldest first.</param>
    /// <param name="utcOffset">This PC's offset from UTC at the last line, so a reader in another timezone can place the times.</param>
    public static string? Write(CrashScene scene, IReadOnlyList<string> lines, TimeSpan utcOffset)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(lines);

        if (scene.Instance is null && lines.Count == 0)
            return null;

        var text = new StringBuilder();
        text.AppendLine("Crash details, from the Modbot companion");

        if (scene.Instance is { } instance)
        {
            text.Append("World: ");
            text.AppendLine(scene.WorldName is { Length: > 0 } name ? $"{name} ({instance.WorldId})" : instance.WorldId);
            text.Append("Instance: ");
            text.AppendLine(Location(instance));
        }
        else
        {
            text.AppendLine("Instance: not in VRChat's log");
        }

        text.Append("Times are this PC's local time, UTC");
        text.AppendLine(Offset(utcOffset));
        text.AppendLine();

        text.AppendLine(CultureInfo.InvariantCulture, $"People in the instance ({scene.People.Count}):");
        foreach (var person in scene.People)
        {
            text.Append(person.DisplayName);
            text.Append(" (");
            text.Append(person.UserId);
            text.Append("), avatar: ");
            text.AppendLine(person.AvatarName is { Length: > 0 } avatar ? avatar : "not in the log");
        }

        text.AppendLine();

        var kept = lines.Count > MaxLines ? lines.Skip(lines.Count - MaxLines).ToList() : lines;
        text.AppendLine(CultureInfo.InvariantCulture,
            $"VRChat's [Behaviour] log lines from the last {(int)Window.TotalMinutes} minutes ({kept.Count}):");
        foreach (var line in kept)
            text.AppendLine(Nonce.Replace(line, "~nonce(removed)"));

        return text.ToString();
    }

    /// <summary>
    /// The location the way VRChat writes it, so it can be pasted straight into a report: the
    /// world, the instance, and every qualifier the companion kept. The instance secret is not one
    /// of them; it was dropped when the location was read.
    /// </summary>
    public static string Location(InstanceLocation instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var text = new StringBuilder($"{instance.WorldId}:{instance.InstanceId}");
        foreach (var name in instance.QualifierNames)
        {
            text.Append('~');
            text.Append(name);
            if (instance.Qualifier(name) is { } value)
                text.Append('(').Append(value).Append(')');
        }

        return text.ToString();
    }

    private static string Offset(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// The last few minutes of <c>[Behaviour]</c> lines in the VRChat log the companion is reading.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> The end of one file: the VRChat output log the companion
/// is already reading, opened for reading and shared, exactly as <see cref="VRChatLogTail"/> opens
/// it. Only the last few megabytes, and only when the moderator presses <strong>Copy crash
/// details</strong>. The lines are not kept after the block is made.</para>
/// <para><strong>Nothing here leaves the machine.</strong> The lines go into the text the moderator
/// copies, and nowhere else. They are not sent to a paired server or to Modbot Cloud, which never
/// receive a raw log line at all.</para>
/// </remarks>
public static class RecentBehaviourLines
{
    /// <summary>How much of the end of the file is read. Ten minutes of a busy instance is far less.</summary>
    public const int TailBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The <c>[Behaviour]</c> lines written in the <paramref name="window"/> before the last line
    /// in the file, oldest first, as VRChat wrote them. Empty when there is no file or it cannot be
    /// read.
    /// </summary>
    public static IReadOnlyList<string> Read(string? file, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(file))
            return [];

        string text;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Position = start;

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            text = reader.ReadToEnd();

            // Starting part way into the file lands in the middle of a line; that first piece is
            // not a line and is dropped.
            if (start > 0 && text.IndexOf('\n') is var cut and >= 0)
                text = text[(cut + 1)..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Pick(text.Split('\n'), window);
    }

    /// <summary>The choosing half of <see cref="Read"/>, with no file: what a test calls.</summary>
    public static IReadOnlyList<string> Pick(IEnumerable<string> rawLines, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(rawLines);

        var behaviour = new List<(DateTime At, string Line)>();
        DateTime? last = null;

        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd('\r');
            if (!VRChatLogLineParser.TryParse(line, out var parsed))
                continue;

            if (last is null || parsed.Timestamp > last)
                last = parsed.Timestamp;

            if (string.Equals(parsed.Tag, "Behaviour", StringComparison.Ordinal))
                behaviour.Add((parsed.Timestamp, line));
        }

        if (last is not { } end)
            return [];

        var from = end - window;
        return [.. behaviour.Where(b => b.At >= from).Select(b => b.Line)];
    }
}
