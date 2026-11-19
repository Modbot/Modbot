using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat;
using Npgsql;

namespace Modbot.Api.Features.ProblemAvatars;

/// <param name="Id">The entry's id, for removing it.</param>
/// <param name="Name">The avatar's name, as it was marked. Empty for an avatar added by its id that VRChat gave no name for.</param>
/// <param name="AvatarId">The avatar's id when it was added from a link or an id; null when it was added by name.</param>
/// <param name="AddedBy">The username of the account that marked it.</param>
public sealed record ProblemAvatarView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("avatarId")] string? AvatarId,
    [property: JsonPropertyName("addedBy")] string AddedBy,
    [property: JsonPropertyName("addedAt")] DateTimeOffset AddedAt);

/// <param name="Text">An avatar name, a vrchat.com avatar link, or an <c>avtr_</c> id.</param>
public sealed record AddProblemAvatarRequest([property: JsonPropertyName("text")] string? Text);

/// <param name="VRChatUserId">The person whose current avatar is marked.</param>
public sealed record MarkPersonAvatarRequest([property: JsonPropertyName("vrchatUserId")] string? VRChatUserId);

/// <param name="AvatarName">The avatar they were last seen wearing. Null when none was ever seen.</param>
/// <param name="Listed">Whether that name is already on the list.</param>
public sealed record PersonAvatarView(
    [property: JsonPropertyName("avatarName")] string? AvatarName,
    [property: JsonPropertyName("listed")] bool Listed);

/// <summary>
/// The problem avatar list: avatar names that raise a flagged alert on whoever wears one (avatar
/// watch design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same permission as the flag rules</strong>, because this is a flag rule: a
/// moderator who may change what makes a person Flagged may change this list.
/// </para>
/// <para>
/// <strong>Adding is idempotent.</strong> A name that is already on the list comes back as it is, so
/// a second click on Mark avatar, or two moderators marking the same avatar, is not an error.
/// </para>
/// <para>
/// <strong>Ids travel in the body or the query string, never in a path</strong>, as notes and
/// watches do: a legacy VRChat id can contain anything (foundation §3.1.1). Only the entry's own
/// id, Modbot's, is in a path.
/// </para>
/// </remarks>
public static class ProblemAvatarEndpoints
{
    public static IEndpointRouteBuilder MapProblemAvatars(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/problem-avatars")
            .WithTags("Problem avatars")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async ([FromServices] ModbotContext db, CancellationToken ct) =>
                Results.Ok(await db.ProblemAvatars.AsNoTracking()
                    .OrderBy(a => a.NameKey)
                    .Select(a => new ProblemAvatarView(a.Id, a.Name, a.AvatarId, a.AddedByUsername, a.AddedAt))
                    .ToListAsync(ct)))
            .WithName("ListProblemAvatars")
            .WithSummary("List problem avatars")
            .WithDescription(
                "The avatar names that raise a flagged alert on whoever wears one, by name, in alphabetical "
                + "order. VRChat's log gives avatar names and no ids, so two avatars with one name are one entry.")
            .Produces<IReadOnlyList<ProblemAvatarView>>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                [FromBody] AddProblemAvatarRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IVRChatGate gate,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var text = body.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                    return Results.BadRequest(new { error = "Type an avatar name or paste its link." });

                if (ProblemAvatar.AvatarIdFrom(text) is not { } avatarId)
                    return Results.Ok(await AddAsync(db, facts, clock, Actor.Of(http), text, ct));

                if (avatarId.Length > ProblemAvatar.MaxIdLength)
                    return Results.BadRequest(new { error = "That avatar id is too long." });

                // Already listed: nothing to look up, and VRChat is not asked.
                if (await db.ProblemAvatars.AsNoTracking().FirstOrDefaultAsync(a => a.AvatarId == avatarId, ct) is { } listed)
                    return Results.Ok(View(listed));

                var name = await LookUpNameAsync(gate, avatarId, ct) ?? string.Empty;

                return Results.Ok(await AddAsync(db, facts, clock, Actor.Of(http), name, ct, avatarId));
            })
            .WithName("AddProblemAvatar")
            .WithSummary("Add a problem avatar by name or link")
            .WithDescription(
                "`text` is an avatar name, a vrchat.com avatar link or an `avtr_` id. A name is added as it is. "
                + "A link or id is looked up on VRChat (avatars.read, one lookup per ten seconds, never retried) "
                + "and added with the name VRChat gives; when VRChat gives none the id is added alone. "
                + "Adding what is already listed returns it.")
            .Produces<ProblemAvatarView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/suggest", async (
                [FromQuery] string? text,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
                Results.Ok(await SuggestAsync(db, clock, text, ct)))
            .WithName("SuggestProblemAvatars")
            .WithSummary("Suggest avatars Modbot has seen")
            .WithDescription(
                "Up to 8 avatar names, newest first, that people were seen wearing in the last 90 days and "
                + "that hold the text, leaving out the ones already on the list. Empty for less than 2 characters. "
                + "Nothing is asked of VRChat.")
            .Produces<IReadOnlyList<string>>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/from-person", async (
                [FromBody] MarkPersonAvatarRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (string.IsNullOrWhiteSpace(body.VRChatUserId))
                    return Results.BadRequest(new { error = "Say which person." });

                if (await CurrentAvatarAsync(db, body.VRChatUserId, ct) is not { } name)
                    return Results.NotFound(new { error = "Modbot has not seen that person wear an avatar." });

                return Results.Ok(await AddAsync(db, facts, clock, Actor.Of(http), name, ct));
            })
            .WithName("MarkPersonAvatar")
            .WithSummary("Mark a person's avatar as a problem")
            .WithDescription(
                "Adds the avatar the person was last seen wearing, by its exact name, to the list. "
                + "404 when Modbot has never seen them wear one.")
            .Produces<ProblemAvatarView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/person", async (
                [FromQuery] string? vrchat,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(vrchat))
                    return Results.BadRequest(new { error = "Say which person." });

                return Results.Ok(await PersonAvatarAsync(db, vrchat, ct) ?? new PersonAvatarView(null, false));
            })
            .WithName("GetPersonAvatar")
            .WithSummary("Get the avatar a person is wearing")
            .WithDescription(
                "The avatar the person was last seen wearing, and whether it is on the list. `avatarName` is "
                + "null when Modbot has never seen them wear one.")
            .Produces<PersonAvatarView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("/{id:guid}", async (
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var entry = await db.ProblemAvatars.FirstOrDefaultAsync(a => a.Id == id, ct);
                if (entry is null)
                    return Results.NotFound(new { error = "That avatar is not on the list." });

                db.ProblemAvatars.Remove(entry);
                await db.SaveChangesAsync(ct);

                await RecordAsync(facts, Actor.Of(http), new JsonObject { ["setting"] = "problemAvatars", ["removed"] = entry.Name }, ct);

                return Results.NoContent();
            })
            .WithName("RemoveProblemAvatar")
            .WithSummary("Remove a problem avatar")
            .WithDescription("Takes an avatar name off the list. People wearing it are no longer alerted on.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// The avatar a person was last seen wearing: the newest arrival, sighting or avatar change of
    /// theirs that carried a name. Null when none ever did.
    /// </summary>
    /// <remarks>
    /// A join usually carries no name (the log names the avatar a moment after), so this looks past
    /// it to the newest fact that does. The name is returned exactly as it was saved.
    /// </remarks>
    internal static async Task<string?> CurrentAvatarAsync(ModbotContext db, string vrchatUserId, CancellationToken ct)
    {
        string[] types = [FactType.AvatarChanged, FactType.InstanceJoined, FactType.InstancePresenceObserved];

        var recent = await db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == vrchatUserId && types.Contains(e.Type))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Select(e => e.Data)
            .Take(50)
            .ToListAsync(ct);

        return recent
            .Select(data => AuditJson.Text(AuditJson.Parse(data), "avatarName"))
            .FirstOrDefault(name => name is not null);
    }

    /// <summary>The avatar a person was last seen wearing and whether it is on the list, or null when none was ever seen.</summary>
    internal static async Task<PersonAvatarView?> PersonAvatarAsync(ModbotContext db, string vrchatUserId, CancellationToken ct)
    {
        if (await CurrentAvatarAsync(db, vrchatUserId, ct) is not { } name)
            return null;

        var key = ProblemAvatar.KeyOf(name);

        return new PersonAvatarView(name, await db.ProblemAvatars.AsNoTracking().AnyAsync(a => a.NameKey == key, ct));
    }

    /// <summary>
    /// Adds an avatar to the list, for the account that marked it: from the web app, or from a paired
    /// companion. <paramref name="name"/> is empty only when <paramref name="avatarId"/> is given.
    /// </summary>
    internal static async Task<ProblemAvatarView> AddAsync(
        ModbotContext db,
        AccountFacts facts,
        IModbotClock clock,
        Actor? actor,
        string name,
        CancellationToken ct,
        string? avatarId = null)
    {
        // A name longer than the column is cut, not refused: the log gives names this code did not choose.
        name = ProblemAvatar.Clip(name);
        var key = ProblemAvatar.KeyOf(name);

        if (await FindAsync(db, key, avatarId, ct) is { } existing)
            return View(existing);

        var entry = new ProblemAvatar
        {
            Name = key.Length == 0 ? string.Empty : name,
            NameKey = key,
            AvatarId = avatarId,
            AddedByUserId = actor?.Id ?? Guid.Empty,
            AddedByUsername = actor?.Username ?? string.Empty,
            AddedAt = clock.UtcNow,
        };

        db.ProblemAvatars.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Somebody marked the same avatar a moment ago: theirs stands, and so does this answer.
            db.Entry(entry).State = EntityState.Detached;
            return View(await FindAsync(db, key, avatarId, ct) ?? throw new InvalidOperationException("The entry that blocked this one is gone.", e));
        }

        await RecordAsync(facts, actor, new JsonObject { ["setting"] = "problemAvatars", ["added"] = key.Length == 0 ? avatarId : name }, ct);

        return View(entry);
    }

    /// <summary>The entry with this name key (when there is a name) or this avatar id (when there is one).</summary>
    private static async Task<ProblemAvatar?> FindAsync(ModbotContext db, string key, string? avatarId, CancellationToken ct)
    {
        if (key.Length > 0 && await db.ProblemAvatars.AsNoTracking().FirstOrDefaultAsync(a => a.NameKey == key, ct) is { } byName)
            return byName;

        return avatarId is null
            ? null
            : await db.ProblemAvatars.AsNoTracking().FirstOrDefaultAsync(a => a.AvatarId == avatarId, ct);
    }

    /// <summary>
    /// The avatar's name from VRChat, or null when VRChat gave none: a private or removed avatar, a
    /// rate limit, no account, no network. One call, never repeated (spec 4.3.1).
    /// </summary>
    private static async Task<string?> LookUpNameAsync(IVRChatGate gate, string avatarId, CancellationToken ct)
    {
        var result = await gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.AvatarsRead, avatarId, "GetAvatar"),
            (client, token) => client.Avatars.GetAvatarWithHttpInfoAsync(avatarId, token),
            VRChatCallPriority.Interactive,
            ct);

        return result.Success && !string.IsNullOrWhiteSpace(result.Value?.Name) ? result.Value.Name : null;
    }

    private const int SuggestLimit = 8;

    /// <summary>How far back <see cref="SuggestAsync"/> looks.</summary>
    private static readonly TimeSpan SuggestWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// Avatar names people were seen wearing that hold the text, newest first, not already on the list.
    /// Read from the avatar names the fact log already holds; nothing new is collected for it.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> SuggestAsync(
        ModbotContext db, IModbotClock clock, string? text, CancellationToken ct)
    {
        var term = text?.Trim();
        if (term is null || term.Length < 2)
            return [];

        // LIKE's own characters in what was typed are text, not wildcards.
        var pattern = "%" + term.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal) + "%";
        var since = clock.UtcNow - SuggestWindow;
        var changed = FactType.AvatarChanged;
        var joined = FactType.InstanceJoined;
        var observed = FactType.InstancePresenceObserved;

        // Names that differ only in case are one; the newest spelling is the one shown.
        var names = await db.Database.SqlQuery<string>($"""
            SELECT n.name AS "Value"
            FROM (
                SELECT (array_agg(e.data->>'avatarName' ORDER BY e.occurred_at DESC))[1] AS name,
                       MAX(e.occurred_at) AS last_seen
                FROM modbot_event e
                WHERE e.type IN ({changed}, {joined}, {observed})
                  AND e.occurred_at >= {since}
                  AND e.data->>'avatarName' ILIKE {pattern}
                GROUP BY lower(btrim(e.data->>'avatarName'))
            ) n
            ORDER BY n.last_seen DESC
            LIMIT 40
            """).ToListAsync(ct);

        var taken = (await db.ProblemAvatars.AsNoTracking().Select(a => a.NameKey).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        return names
            .Where(n => !taken.Contains(ProblemAvatar.KeyOf(n)))
            .Take(SuggestLimit)
            .ToList();
    }

    private static Task RecordAsync(AccountFacts facts, Actor? actor, JsonObject data, CancellationToken ct)
        => facts.RecordAsync(FactType.SettingsChanged, "settings", actor, data, ct);

    private static ProblemAvatarView View(ProblemAvatar entry)
        => new(entry.Id, entry.Name, entry.AvatarId, entry.AddedByUsername, entry.AddedAt);
}
