using System.Text.Json.Nodes;
using Modbot.AI.Chat;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Chat;

/// <summary>One person a question's tools read about.</summary>
internal sealed record LookedUpPerson(FactPlatform Platform, string Id, string? Name);

/// <summary>
/// Records who asked about whom on the Chat page (AI chat design §13).
/// </summary>
/// <remarks>
/// <para>
/// Looking somebody up through Chat has to be as visible as opening their profile, or Chat becomes
/// the quiet way to read a member's history. So a question whose tools returned people writes a
/// fact naming the moderator who asked and the people that were read, and it lands in the audit
/// log and in each of their own histories like any other entry.
/// </para>
/// <para>
/// <strong>One per question, not one per tool call.</strong> A question that runs six tools about
/// one person is one lookup, and six identical entries would bury the timeline it is meant to
/// serve. A question that read two people writes one entry for each of them, because the audit log
/// files an entry under one subject and a moderator reading their own history must find it there;
/// both entries carry the whole list, so it still reads as one question.
/// </para>
/// <para>
/// <strong>The question is not stored.</strong> The conversation already holds the words, and this
/// is a record of access rather than of what was said.
/// </para>
/// </remarks>
internal static class ChatLookupFacts
{
    /// <summary>
    /// The most people one question records. A question that swept a member list is still a lookup;
    /// it is not a reason to write a thousand facts.
    /// </summary>
    public const int MostPeople = 20;

    /// <summary>
    /// The people the tools of one reply returned, in the order they were first named, and the
    /// tools that named them.
    /// </summary>
    public static (IReadOnlyList<LookedUpPerson> People, IReadOnlyList<string> Tools) Read(IEnumerable<ChatTurn> toolTurns)
    {
        ArgumentNullException.ThrowIfNull(toolTurns);

        var people = new List<LookedUpPerson>();
        var seen = new HashSet<(FactPlatform, string)>();
        var tools = new List<string>();

        foreach (var turn in toolTurns)
        {
            var named = false;

            foreach (var reference in turn.References ?? [])
            {
                var platform = reference.Kind switch
                {
                    ChatReference.Person => FactPlatform.VRChat,
                    ChatReference.DiscordPerson => FactPlatform.Discord,
                    _ => (FactPlatform?)null,
                };

                if (platform is not { } on || string.IsNullOrWhiteSpace(reference.Id))
                    continue;

                named = true;

                if (people.Count < MostPeople && seen.Add((on, reference.Id)))
                    people.Add(new LookedUpPerson(on, reference.Id, reference.Label));
            }

            if (named && turn.ToolName is { Length: > 0 } tool && !tools.Contains(tool, StringComparer.Ordinal))
                tools.Add(tool);
        }

        return (people, tools);
    }

    /// <summary>Writes the fact for one question. Does nothing when no person was read.</summary>
    public static Task RecordAsync(
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        DateTimeOffset now,
        Actor asker,
        Guid conversationId,
        IReadOnlyList<LookedUpPerson> people,
        IReadOnlyList<string> tools,
        CancellationToken ct)
        => RecordAsync(
            facts, partitions, now, asker,
            new JsonObject { ["conversationId"] = conversationId.ToString() },
            people, tools, ct);

    /// <summary>
    /// The same fact for a lookup that was not a Chat question: the MCP server, where the asker's
    /// own AI app ran the tool. <paramref name="details"/> says which (<c>via</c>, <c>client</c>).
    /// </summary>
    public static async Task RecordAsync(
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        DateTimeOffset now,
        Actor asker,
        JsonObject details,
        IReadOnlyList<LookedUpPerson> people,
        IReadOnlyList<string> tools,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(tools);

        if (people.Count == 0)
            return;

        // The hosted maintainer keeps partitions ahead, but a lookup must never go unrecorded for
        // want of one -- and in a test host there is no maintainer.
        await partitions.EnsureForAsync(now, ct);

        var everyone = new JsonArray();
        foreach (var person in people)
        {
            everyone.Add(new JsonObject
            {
                ["platform"] = person.Platform.ToString(),
                ["id"] = person.Id,
                ["name"] = person.Name,
            });
        }

        var named = new JsonArray();
        foreach (var tool in tools)
            named.Add(tool);

        var records = people.Select(person =>
        {
            var data = new JsonObject { ["actorDisplayName"] = asker.Username };
            foreach (var (key, value) in details)
                data[key] = value?.DeepClone();
            data["people"] = everyone.DeepClone();
            data["tools"] = named.DeepClone();

            return new FactRecord
            {
                Type = FactType.ChatLookup,
                OccurredAt = now,
                SubjectPlatform = person.Platform,
                SubjectId = person.Id,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = asker.Id.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            };
        });

        await facts.WriteManyAsync(records, ct);
    }
}
