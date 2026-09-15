using System.Text.Json;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Chat;

/// <summary>Every chat tool this build has, and which of them a given person is offered.</summary>
public sealed class ChatToolRegistry
{
    public ChatToolRegistry(IEnumerable<IChatTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        All = [.. tools.OrderBy(t => t.Name, StringComparer.Ordinal)];

        // A second tool under one name would make which one runs depend on registration order.
        var clash = All.GroupBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
            throw new InvalidOperationException($"Two chat tools are named '{clash.Key}'.");
    }

    public IReadOnlyList<IChatTool> All { get; }

    /// <summary>
    /// The tools to put in front of the model for this person: switched on, and needing nothing
    /// they do not hold.
    /// </summary>
    /// <remarks>
    /// A tool left out here is left out of the request entirely, so the model cannot ask for it
    /// and is not told it exists (design §3.1). The loop also refuses to run any tool that was not
    /// offered, so a model naming one anyway gets nothing.
    /// </remarks>
    public IReadOnlyList<IChatTool> OfferedTo(ModbotPermissions held, IReadOnlyDictionary<string, bool> switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        return [.. All.Where(t => IsOn(t, switches) && Allows(held, t.Needs))];
    }

    /// <summary>
    /// A read-only tool is on unless switched off; a tool that acts is off unless switched on.
    /// </summary>
    public static bool IsOn(IChatTool tool, IReadOnlyDictionary<string, bool> switches)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(switches);

        return switches.TryGetValue(tool.Name, out var on) ? on : tool.OnlyReads;
    }

    /// <summary>The same rule the API's permission check applies: Administrator satisfies everything.</summary>
    public static bool Allows(ModbotPermissions held, ModbotPermissions needs) =>
        held.HasFlag(ModbotPermissions.Administrator) || (held & needs) == needs;

    /// <summary>Reads <c>settings.ai_chat_tool_switches</c>. Anything unreadable counts as no switches.</summary>
    public static IReadOnlyDictionary<string, bool> ParseSwitches(string? json)
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
}
