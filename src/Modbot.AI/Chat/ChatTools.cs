using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Chat;

/// <summary>
/// Something the model may ask Modbot to do while it writes a reply (AI chat design §3).
/// </summary>
/// <remarks>
/// <para>
/// A tool is a read Modbot already serves to the web app, described for a model. It never gets
/// more than the person asking holds: <see cref="ChatToolContext.Held"/> is their own permissions,
/// and a tool whose <see cref="Needs"/> they lack is not offered at all (<see cref="ChatToolRegistry"/>).
/// </para>
/// <para>
/// Implementations are singletons and must keep no state. Anything scoped -- the database
/// context above all -- comes from <see cref="ChatToolContext.Services"/>, which is the request's
/// own scope.
/// </para>
/// </remarks>
public interface IChatTool
{
    /// <summary>What the model calls it: lower case and underscores. Also the key of its on/off switch.</summary>
    string Name { get; }

    /// <summary>Plain words for the settings page and the chip on the Chat page.</summary>
    string Label { get; }

    /// <summary>What the model is told the tool does and when to use it.</summary>
    string Description { get; }

    /// <summary>A JSON Schema object for the arguments.</summary>
    BinaryData Parameters { get; }

    /// <summary>Every permission the person asking must hold for the tool to be offered.</summary>
    ModbotPermissions Needs { get; }

    /// <summary>
    /// False for a tool that changes anything. Such a tool is off unless an operator switches it
    /// on (design §3.3). None exists yet.
    /// </summary>
    bool OnlyReads { get; }

    Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct);
}

/// <summary>Who is asking, as the tools see them.</summary>
/// <param name="Held">The person's own permissions, from their session.</param>
/// <param name="Services">The request's scope.</param>
public sealed record ChatToolContext(Guid UserId, ModbotPermissions Held, IServiceProvider Services);

/// <summary>Something a tool result names that the Chat page can open.</summary>
/// <remarks>
/// These are the sources of an answer. A tool sends back a reference for every row it returned, so
/// the page can show what the answer was built from and open each one where it lives: a person,
/// world or instance in its popup, a fact in the audit log, a case file on its own page, a Discord
/// message in that person's messages, an event on the calendar.
/// </remarks>
/// <param name="Kind">One of the constants below -- what the chip opens.</param>
/// <param name="Id">The VRChat user or world id, Modbot's own instance id, or the row's own id.</param>
/// <param name="Label">A name to show, when one is known.</param>
/// <param name="Author">For a Discord message: whose messages to open it in.</param>
public sealed record ChatReference(string Kind, string Id, string? Label, string? Author = null)
{
    public const string Person = "person";
    public const string World = "world";
    public const string Instance = "instance";
    public const string DiscordPerson = "discord-person";

    /// <summary>One entry of the audit log, by its own id.</summary>
    public const string Fact = "fact";

    /// <summary>One case file, by its id.</summary>
    public const string Case = "case";

    /// <summary>One Discord message, by its message id. <see cref="Author"/> says whose it is.</summary>
    public const string Message = "message";

    /// <summary>One calendar event, by its id.</summary>
    public const string Event = "event";
}

/// <summary>What a tool sends back to the model, and what it named along the way.</summary>
/// <param name="Content">Sent to the model as the tool's result. Usually JSON.</param>
/// <param name="Worked">False when the tool could not answer; <paramref name="Content"/> then says why.</param>
public sealed record ChatToolResult(string Content, IReadOnlyList<ChatReference> References, bool Worked = true)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ChatToolResult Json(object value, IEnumerable<ChatReference>? references = null) =>
        new(JsonSerializer.Serialize(value, Web), references?.DistinctBy(r => (r.Kind, r.Id)).ToList() ?? []);

    public static ChatToolResult Problem(string message) =>
        new(JsonSerializer.Serialize(new { error = message }, Web), [], Worked: false);
}

/// <summary>Reading tool arguments without trusting the model to have sent the right shape.</summary>
public static class ChatArguments
{
    public static string? Text(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
            return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static int Number(JsonElement arguments, string name, int fallback, int min, int max)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
                return Math.Clamp(n, min, max);

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                return Math.Clamp(parsed, min, max);
        }

        return fallback;
    }

    public static IReadOnlyList<string> List(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
            return [];

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!.Trim())
                .Where(v => v.Length > 0)
                .ToList(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) =>
                value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [],
        };
    }
}
