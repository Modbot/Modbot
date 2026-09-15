using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// One moderation event with the names filled in: what the embed and the lookup reply show.
/// </summary>
/// <param name="SubjectName">From <c>vrchat_user</c> when Modbot has fetched that profile; else null.</param>
/// <param name="ActorName">
/// From <c>vrchat_user</c> when known, else the name VRChat put on the audit entry at the time.
/// </param>
/// <param name="Description">VRChat's own sentence for the entry, when the fact carries one.</param>
public sealed record ModerationEventView(
    long Id,
    string Type,
    DateTimeOffset OccurredAt,
    string SubjectId,
    string? SubjectName,
    string? ActorId,
    string? ActorName,
    string? Description)
{
    public static ModerationEventView From(ModbotEvent fact, IReadOnlyDictionary<string, string?> names)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(names);

        var (actorDisplayName, description) = ReadPayload(fact.Data);

        names.TryGetValue(fact.SubjectId, out var subjectName);

        string? actorName = null;
        if (fact.ActorId is not null)
            names.TryGetValue(fact.ActorId, out actorName);

        return new ModerationEventView(
            fact.Id,
            fact.Type,
            fact.OccurredAt,
            fact.SubjectId,
            subjectName,
            fact.ActorId,
            actorName ?? actorDisplayName,
            description);
    }

    private static (string? ActorDisplayName, string? Description) ReadPayload(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);

            return (Text(root, "actorDisplayName"), Text(root, "description"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Display names for a set of ids: from <c>vrchat_user</c>, and for a Modbot account id the
/// account's username.
/// </summary>
public static class DisplayNames
{
    public static async Task<Dictionary<string, string?>> LoadAsync(
        ModbotContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var rows = await db.VRChatUsers.AsNoTracking()
            .Where(u => wanted.Contains(u.UserId))
            .Select(u => new { u.UserId, u.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var names = rows.ToDictionary(r => r.UserId, r => r.DisplayName, StringComparer.Ordinal);

        // Modbot's own actions name the account that did them by its id.
        var accountIds = wanted
            .Where(id => !names.ContainsKey(id))
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .ToArray();

        if (accountIds.Length > 0)
        {
            var accounts = await db.Users.AsNoTracking()
                .Where(u => accountIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var account in accounts)
                names[account.Id.ToString()] = account.Username;
        }

        return names;
    }
}
