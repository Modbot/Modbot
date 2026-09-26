using System.Globalization;
using System.Text.Json;
using Modbot.AI.Chat;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>
/// The audit log, read the way the Audit log page reads it.
/// </summary>
/// <remarks>
/// The type list always goes through <see cref="AuditVisibility.Resolve"/> with the person's own
/// permissions, so somebody who may read moderation history but not the operational log gets the
/// same timeline here that the page shows them.
/// </remarks>
internal static class AuditSearch
{
    public static async Task<IReadOnlyList<AuditEntry>> EntriesAsync(
        ChatToolContext context,
        IReadOnlyCollection<string> types,
        string? subjectId,
        string? actorId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct)
    {
        var visible = AuditVisibility.Resolve(context.Held, types);

        // The query must never run with an empty type list: that is "nothing permitted", not
        // "everything".
        if (visible.Count == 0)
            return [];

        var db = (ModbotContext)context.Services.GetService(typeof(ModbotContext))!;
        var page = await new AuditQuery(db).PageAsync(
            new AuditRequest(visible, [], subjectId, null, actorId, null, from, to, null, limit), ct);

        return page.Entries;
    }

    public static async Task<ChatToolResult> RunAsync(
        ChatToolContext context,
        IReadOnlyCollection<string> types,
        string? subjectId,
        string? actorId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct)
    {
        var entries = await EntriesAsync(context, types, subjectId, actorId, from, to, limit, ct);

        return ChatToolResult.Json(
            new { count = entries.Count, entries = entries.Select(Summary) },
            References(entries));
    }

    /// <summary>
    /// One entry, with the id it is filed under.
    /// </summary>
    /// <remarks>
    /// The id is here so the answer can say where a claim came from: the page turns it into a
    /// source chip that opens the audit log at that entry.
    /// </remarks>
    public static object Summary(AuditEntry e) => new
    {
        factId = e.Id,
        e.OccurredAt,
        e.OccurredBefore,
        what = FactLabels.For(e.Type),
        e.Type,
        e.Source,
        subject = new { platform = e.SubjectPlatform, id = e.SubjectId, name = e.SubjectName },
        actor = e.ActorId is null ? null : new { platform = e.ActorPlatform, id = e.ActorId, name = e.ActorName },
        e.WorldId,
        e.WorldName,
        number = e.InstanceId,
        instanceName = e.InstanceName,
        instanceId = e.ModbotInstanceId,
        e.Description,
        e.Data,
    };

    public static IEnumerable<ChatReference> References(IEnumerable<AuditEntry> entries)
    {
        var vrchat = FactPlatform.VRChat.ToString();
        var discord = FactPlatform.Discord.ToString();

        foreach (var e in entries)
        {
            yield return new ChatReference(
                ChatReference.Fact, e.Id.ToString(CultureInfo.InvariantCulture), FactLabels.For(e.Type));

            if (e.SubjectKind == SubjectKind.Person && e.SubjectPlatform == vrchat)
                yield return new ChatReference(ChatReference.Person, e.SubjectId, e.SubjectName);

            if (e.SubjectKind == SubjectKind.Person && e.SubjectPlatform == discord)
                yield return new ChatReference(ChatReference.DiscordPerson, e.SubjectId, e.SubjectName);

            if (e.ActorId is { } actor && e.ActorPlatform == vrchat)
                yield return new ChatReference(ChatReference.Person, actor, e.ActorName);

            if (e.ActorId is { } discordActor && e.ActorPlatform == discord)
                yield return new ChatReference(ChatReference.DiscordPerson, discordActor, e.ActorName);

            if (e.WorldId is { } world)
                yield return new ChatReference(ChatReference.World, world, e.WorldName);

            if (e.ModbotInstanceId is { } instance)
            {
                yield return new ChatReference(
                    ChatReference.Instance, instance.ToString(), Places.InstanceRows.Label(e.WorldName, e.WorldId!, e.InstanceId, e.InstanceName));
            }
        }
    }
}

/// <summary>Search the audit log by type, person, actor and date.</summary>
internal sealed class SearchAuditLogTool : ReadTool
{
    public override string Name => "search_audit_log";

    public override string Label => "Search the audit log";

    public override string Description =>
        "Search the audit log, newest first. Filter by entry types, the person it was about "
        + "(subjectId), who did it (actorId) and a date range. Call with listTypes true to get the "
        + "entry types you may search, with a label for each.";

    protected override string Schema => """
        {"type":"object","properties":{"types":{"type":"array","items":{"type":"string"},"description":"Entry types, for example vrchat.group.member.ban. Leave out for every type."},"subjectId":{"type":"string","description":"The id of the person or thing the entry is about."},"actorId":{"type":"string","description":"The id of whoever did it."},"from":{"type":"string","description":"Earliest date or time, ISO 8601."},"to":{"type":"string","description":"Latest date or time, ISO 8601."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 25."},"listTypes":{"type":"boolean","description":"Only list the searchable entry types."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAuditLog;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("listTypes", out var list)
            && list.ValueKind == JsonValueKind.True)
        {
            return ChatToolResult.Json(new
            {
                types = AuditVisibility.VisibleTypes(context.Held).Select(t => new { type = t, label = FactLabels.For(t) }),
            });
        }

        var asked = ChatArguments.List(arguments, "types");
        var types = asked
            .Select(t => t.ToLowerInvariant())
            .Where(FactType.IsWellFormed)
            .ToList();

        // Asked for types and none of them is one this person may read: an empty answer, never
        // the whole log.
        if (asked.Count > 0 && (types.Count == 0 || AuditVisibility.Resolve(context.Held, types).Count == 0))
            return ChatToolResult.Json(new { count = 0, entries = Array.Empty<object>() });

        return await AuditSearch.RunAsync(
            context,
            types,
            ChatArguments.Text(arguments, "subjectId"),
            ChatArguments.Text(arguments, "actorId"),
            ChatTime.Of(ChatArguments.Text(arguments, "from")),
            ChatTime.Of(ChatArguments.Text(arguments, "to")),
            ChatArguments.Number(arguments, "limit", 25, 1, 50),
            ct);
    }
}
