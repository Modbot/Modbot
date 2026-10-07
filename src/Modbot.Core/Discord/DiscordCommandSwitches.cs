using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modbot.Core.Discord;

/// <summary>One command the operator can switch on or off (Discord commands design §3.8).</summary>
/// <param name="Name">
/// The command's name as Discord shows it: <c>lookup</c> for a slash command, the menu's own words
/// for a right-click menu. The key in the <c>discord_commands</c> setting.
/// </param>
/// <param name="OnByDefault">What a command with no stored choice does.</param>
/// <param name="Menu">A right-click menu rather than a slash command.</param>
public sealed record DiscordCommandSwitch(string Name, bool OnByDefault, bool Menu = false);

/// <summary>
/// Which of the bot's commands are switched on, from the <c>discord_commands</c> setting: a JSON
/// object mapping a command's name to true or false (Discord commands design §3.8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sparse on purpose.</strong> A name that is not in the object takes its default from
/// <see cref="All"/>, so a command added later needs no migration and a deployment that never
/// touched a switch follows a revised default. A name that is stored but no longer in the list is
/// ignored.
/// </para>
/// <para>
/// <strong>Off is not registered.</strong> The bot leaves a switched-off command out of the list it
/// gives Discord, so a member never sees a command that would only say it is off.
/// </para>
/// <para>
/// This list lives in Core because the web app's Commands card reads it too, and the web app does not
/// know the bot. A test in the bot's tests holds it to the commands the bot registers, so a command
/// cannot be added to one and forgotten in the other.
/// </para>
/// </remarks>
public static class DiscordCommandSwitches
{
    /// <summary>What the setting holds before anybody has chosen anything.</summary>
    public const string Empty = "{}";

    /// <summary>
    /// Every command with a switch. Staff commands are on by default, and so are the commands every
    /// member already had before the switches existed; <c>/me</c> was always opt-in and stays so.
    /// Commands for members that come later (<c>/events</c> and <c>/remindme</c> so far) are off until
    /// the operator turns them on.
    /// </summary>
    public static IReadOnlyList<DiscordCommandSwitch> All { get; } =
    [
        new("lookup", OnByDefault: true),
        new("recent", OnByDefault: true),
        new("modbot", OnByDefault: true),
        new("link", OnByDefault: true),
        new("me", OnByDefault: false),
        new("help", OnByDefault: true),
        new("verify", OnByDefault: true),
        new("note", OnByDefault: true),
        new("watch", OnByDefault: true),
        new("live", OnByDefault: true),
        new("ban", OnByDefault: true),
        new("kick", OnByDefault: true),
        new("gate", OnByDefault: true),
        new("event", OnByDefault: true),
        new("post", OnByDefault: true),
        new("events", OnByDefault: false),
        new("remindme", OnByDefault: false),
        new("Look up in Modbot", OnByDefault: true, Menu: true),
        new("Add a note", OnByDefault: true, Menu: true),
    ];

    /// <summary>The switch for a command, or null when the name is not one of ours.</summary>
    public static DiscordCommandSwitch? Find(string name)
        => All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// What is stored, as a map. Anything that is not a JSON object of true and false values reads
    /// as nothing stored, so a hand-edited row cannot take the bot down.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Read(string? json)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(json))
            return map;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject stored)
                return map;

            foreach (var (name, value) in stored)
            {
                if (value is JsonValue v && v.TryGetValue<bool>(out var on))
                    map[name] = on;
            }
        }
        catch (JsonException)
        {
        }

        return map;
    }

    /// <summary>Whether a command is on: the stored choice, or its default when there is none.</summary>
    public static bool IsOn(string? json, string name)
        => Read(json).TryGetValue(name, out var stored)
            ? stored
            : Find(name)?.OnByDefault ?? false;

    /// <summary>Every command's name with whether it is on, in list order.</summary>
    public static IReadOnlyDictionary<string, bool> Current(string? json)
    {
        var stored = Read(json);
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var command in All)
            map[command.Name] = stored.TryGetValue(command.Name, out var on) ? on : command.OnByDefault;

        return map;
    }

    /// <summary>
    /// The setting for these choices: only the names that differ from their default are kept, so an
    /// unchanged command keeps following its default. Names that are not commands are dropped.
    /// </summary>
    public static string Write(IReadOnlyDictionary<string, bool> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var stored = new JsonObject();

        foreach (var command in All)
        {
            if (choices.TryGetValue(command.Name, out var on) && on != command.OnByDefault)
                stored[command.Name] = on;
        }

        return stored.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>The setting with one command set, the others as they were.</summary>
    public static string With(string? json, string name, bool on)
    {
        var map = new Dictionary<string, bool>(Current(json), StringComparer.Ordinal) { [name] = on };
        return Write(map);
    }
}
