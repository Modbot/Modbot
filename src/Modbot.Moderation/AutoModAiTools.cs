using System.Text.Json;

namespace Modbot.Moderation;

/// <summary>One thing the AI may do for AutoMod, and its switch.</summary>
/// <param name="Name">The key of its switch in <c>settings.automod_ai_tools</c>.</param>
/// <param name="Label">What the settings page calls it.</param>
/// <param name="OnByDefault">Where the switch stands until somebody moves it.</param>
public sealed record AutoModAiTool(string Name, string Label, bool OnByDefault);

/// <summary>
/// The AI tools AutoMod may use, and which of them a group has switched on (AutoMod design §6).
/// </summary>
/// <remarks>
/// <para>
/// Each switch is checked where the tool would run, not only on the settings page: the engine does
/// not ask the AI about topics while <see cref="ClassifyTopics"/> is off, hands over no pictures
/// while <see cref="CheckPictures"/> is off, and the flag endpoints refuse an AI opinion while
/// <see cref="ReviewFlag"/> is off. A switched-off tool is never called.
/// </para>
/// <para>
/// Only switches somebody changed are stored, the way Chat's tool switches are, so a tool added in
/// a later version starts where its kind should.
/// </para>
/// </remarks>
public static class AutoModAiTools
{
    /// <summary>Check text no term list matched against the group's AI topics.</summary>
    public const string ClassifyTopics = "classify_topics";

    /// <summary>Send a rule's pictures with the text (AI moderation design §17).</summary>
    public const string CheckPictures = "check_pictures";

    /// <summary>Read a flag and say whether to keep or dismiss it, when a moderator asks.</summary>
    public const string ReviewFlag = "review_flag";

    /// <summary>With the opinion, propose what a moderator might do about the flag. Advice only.</summary>
    public const string ProposeAction = "propose_action";

    public static IReadOnlyList<AutoModAiTool> All { get; } =
    [
        new(ClassifyTopics, "Check text against AI topics", OnByDefault: true),
        new(CheckPictures, "Check pictures", OnByDefault: true),
        new(ReviewFlag, "Give an opinion on a flag", OnByDefault: true),
        new(ProposeAction, "Propose an action for a flag", OnByDefault: false),
    ];

    public static bool IsTool(string? name) => All.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>Whether a tool is on, given the stored switches.</summary>
    public static bool IsOn(IReadOnlyDictionary<string, bool> switches, string tool)
    {
        ArgumentNullException.ThrowIfNull(switches);

        if (switches.TryGetValue(tool, out var on))
            return on;

        return All.FirstOrDefault(t => t.Name == tool)?.OnByDefault ?? false;
    }

    /// <summary>Reads <c>settings.automod_ai_tools</c>. Anything unreadable counts as no switches.</summary>
    public static IReadOnlyDictionary<string, bool> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, bool>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(json) is { } parsed
                ? new Dictionary<string, bool>(parsed, StringComparer.Ordinal)
                : new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The switches to store after a change: only those that differ from the tool's default, so a
    /// later version can change a default without every deployment's old switch pinning it.
    /// </summary>
    public static string Serialize(IReadOnlyDictionary<string, bool> switches)
    {
        ArgumentNullException.ThrowIfNull(switches);

        var kept = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var tool in All)
        {
            if (switches.TryGetValue(tool.Name, out var on) && on != tool.OnByDefault)
                kept[tool.Name] = on;
        }

        return JsonSerializer.Serialize(kept);
    }
}
