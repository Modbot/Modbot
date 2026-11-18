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
using Npgsql;

namespace Modbot.Api.Features.ProblemAvatars;

/// <param name="Id">The entry's id, for removing it.</param>
/// <param name="Name">The avatar's name, as it was marked.</param>
/// <param name="AddedBy">The username of the account that marked it.</param>
public sealed record ProblemAvatarView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("addedBy")] string AddedBy,
    [property: JsonPropertyName("addedAt")] DateTimeOffset AddedAt);

public sealed record AddProblemAvatarRequest([property: JsonPropertyName("name")] string? Name);

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
                    .Select(a => new ProblemAvatarView(a.Id, a.Name, a.AddedByUsername, a.AddedAt))
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
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (string.IsNullOrWhiteSpace(body.Name))
                    return Results.BadRequest(new { error = "An avatar needs a name." });

                if (body.Name.Length > ProblemAvatar.MaxNameLength)
                {
                    return Results.BadRequest(new
                    {
                        error = $"A name is at most {ProblemAvatar.MaxNameLength} characters.",
                    });
                }

                return Results.Ok(await AddAsync(db, facts, clock, Actor.Of(http), body.Name, ct));
            })
            .WithName("AddProblemAvatar")
            .WithSummary("Add a problem avatar")
            .WithDescription(
                "Adds an avatar name to the list. A name already on the list, whatever its case, comes back "
                + "unchanged.")
            .Produces<ProblemAvatarView>()
            .Produces(StatusCodes.Status400BadRequest)
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

    /// <summary>Adds a name to the list, for the account that marked it: from the web app, or from a paired companion.</summary>
    internal static async Task<ProblemAvatarView> AddAsync(
        ModbotContext db,
        AccountFacts facts,
        IModbotClock clock,
        Actor? actor,
        string name,
        CancellationToken ct)
    {
        var key = ProblemAvatar.KeyOf(name);

        if (await db.ProblemAvatars.AsNoTracking().FirstOrDefaultAsync(a => a.NameKey == key, ct) is { } existing)
            return View(existing);

        var entry = new ProblemAvatar
        {
            Name = name,
            NameKey = key,
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
            return View(await db.ProblemAvatars.AsNoTracking().FirstAsync(a => a.NameKey == key, ct));
        }

        await RecordAsync(facts, actor, new JsonObject { ["setting"] = "problemAvatars", ["added"] = name }, ct);

        return View(entry);
    }

    private static Task RecordAsync(AccountFacts facts, Actor? actor, JsonObject data, CancellationToken ct)
        => facts.RecordAsync(FactType.SettingsChanged, "settings", actor, data, ct);

    private static ProblemAvatarView View(ProblemAvatar entry)
        => new(entry.Id, entry.Name, entry.AddedByUsername, entry.AddedAt);
}
