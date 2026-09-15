using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI.Chat;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>
/// A chat tool that only reads (AI chat design §3.2).
/// </summary>
/// <remarks>
/// Every tool calls the same query code the page it mirrors uses, so Chat and the app cannot
/// disagree about an answer or about who may see it. Where that code narrows by permission --
/// the audit log, a room's people -- the person's own permissions from
/// <see cref="ChatToolContext.Held"/> are passed straight through.
/// </remarks>
internal abstract class ReadTool : IChatTool
{
    public abstract string Name { get; }

    public abstract string Label { get; }

    public abstract string Description { get; }

    public BinaryData Parameters => BinaryData.FromString(Schema);

    /// <summary>The JSON Schema for the arguments.</summary>
    protected abstract string Schema { get; }

    public abstract ModbotPermissions Needs { get; }

    public bool OnlyReads => true;

    public abstract Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct);

    protected static T Get<T>(ChatToolContext context) where T : notnull =>
        context.Services.GetRequiredService<T>();

    /// <summary>An ILIKE pattern that matches the term anywhere, with its own wildcards escaped.</summary>
    protected static string Contains(string term) =>
        "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    protected static ChatReference Person(string id, string? name) => new(ChatReference.Person, id, name);

    protected static ChatReference World(string id, string? name) => new(ChatReference.World, id, name);

    protected static ChatReference DiscordPerson(string id, string? name) => new(ChatReference.DiscordPerson, id, name);

    protected static ChatReference Fact(long id, string? label) =>
        new(ChatReference.Fact, id.ToString(CultureInfo.InvariantCulture), label);

    protected static ChatReference Case(Guid id, string? label) => new(ChatReference.Case, id.ToString(), label);

    /// <summary>A Discord message, which opens in its author's messages.</summary>
    protected static ChatReference Message(string messageId, string authorId, string? label) =>
        new(ChatReference.Message, messageId, label, authorId);

    protected static ChatReference Event(Guid id, string? label) => new(ChatReference.Event, id.ToString(), label);

    /// <summary>
    /// Keeps the first <paramref name="limit"/> rows of a query asked for one more than it wanted,
    /// and says whether there were more.
    /// </summary>
    /// <remarks>
    /// A tool result is read by a model with a token budget and by a moderator in a step. Rows, and
    /// a count of what was left out, answer more questions than a dump the model has to be cut off
    /// part-way through.
    /// </remarks>
    protected static (List<T> Rows, bool More) FirstOf<T>(List<T> rows, int limit)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var more = rows.Count > limit;
        if (more)
            rows.RemoveRange(limit, rows.Count - limit);

        return (rows, more);
    }

    protected static IEnumerable<ChatReference> RoomReferences(InstanceRow room)
    {
        yield return World(room.WorldId, room.WorldName);
        yield return new ChatReference(
            ChatReference.Instance,
            room.Id.ToString(),
            room.VRChatInstanceId is { } number ? $"{room.WorldName ?? "Room"} #{number}" : room.WorldName);
    }

    /// <summary>A room, as the model is shown it: no pictures, the id it can pass to <c>get_instance</c>.</summary>
    protected static object RoomSummary(InstanceRow room) => new
    {
        instanceId = room.Id,
        room.WorldId,
        room.WorldName,
        number = room.VRChatInstanceId,
        room.GroupAccessType,
        room.Region,
        room.OpenedAt,
        room.ClosedAt,
        room.PeopleNow,
        room.PeakPeople,
        room.MinutesOpen,
    };
}
