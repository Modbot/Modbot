using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Partners;

/// <summary>
/// The Partners page: the other groups this one works with, who to talk to there, who here looks
/// after the friendship, the last collab and notes (partner tracker).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Staff only, and nothing leaves Modbot.</strong> Seeing and changing both need
/// <see cref="ModbotPermissions.ManagePartners"/>. Nothing here calls VRChat: every field is what
/// staff typed. The one thing read is a group's name Modbot already holds from an earlier
/// instance popup (<see cref="OtherGroupName"/>), used only when the name is left empty.
/// </para>
/// <para>
/// Each change is recorded as a fact naming the partner and which fields changed, never the
/// contacts or notes themselves, so the log holds no copy of them once a partner is deleted.
/// </para>
/// </remarks>
public static class PartnerEndpoints
{
    public static IEndpointRouteBuilder MapPartners(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/partners").WithTags("Partners");

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var partners = await db.Partners.AsNoTracking().ToListAsync(ct);

                // Most recent collab first; the ones with none after them, by name.
                var ordered = partners
                    .OrderByDescending(p => p.LastCollabOn.HasValue)
                    .ThenByDescending(p => p.LastCollabOn)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(View)
                    .ToList();

                return Results.Ok(new PartnersView(ordered));
            })
            .RequiresFlag(ModbotPermissions.ManagePartners)
            .WithName("ListPartners")
            .WithSummary("List partners")
            .WithDescription("Every partner group, the most recent collab first, then those with none by name.")
            .Produces<PartnersView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] PartnerRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var partner = new Partner { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };

                if (await ApplyAsync(db, partner, body, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                partner.UpdatedAt = partner.CreatedAt;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Partners.Add(partner);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.PartnerCreated, partner.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = partner.Name }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(partner));
            })
            .RequiresFlag(ModbotPermissions.ManagePartners)
            .WithName("CreatePartner")
            .WithSummary("Add partner")
            .WithDescription("Add a partner group. A refusal (400) says what is wrong in error.")
            .Produces<PartnerView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] PartnerRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var partner = await db.Partners.FirstOrDefaultAsync(p => p.Id == id, ct);
                if (partner is null)
                    return Results.NotFound();

                var before = Snapshot(partner);

                if (await ApplyAsync(db, partner, body, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var changed = Differences(before, Snapshot(partner));
                if (changed.Count == 0)
                    return Results.Ok(View(partner));

                partner.UpdatedAt = clock.UtcNow;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.PartnerChanged, partner.Id.ToString(), Actor.Of(http),
                    new JsonObject
                    {
                        ["name"] = partner.Name,
                        ["changed"] = new JsonArray([.. changed.Select(c => (JsonNode?)JsonValue.Create(c))]),
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(partner));
            })
            .RequiresFlag(ModbotPermissions.ManagePartners)
            .WithName("UpdatePartner")
            .WithSummary("Update partner")
            .WithDescription("Replace everything known about a partner. A refusal (400) says what is wrong in error.")
            .Produces<PartnerView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var partner = await db.Partners.FirstOrDefaultAsync(p => p.Id == id, ct);
                if (partner is null)
                    return Results.NotFound();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Partners.Remove(partner);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.PartnerDeleted, partner.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = partner.Name }, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManagePartners)
            .WithName("DeletePartner")
            .WithSummary("Delete partner")
            .WithDescription("Delete a partner and everything known about it.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Copies the request onto <paramref name="partner"/> after trimming it; returns what is wrong,
    /// or null. Nothing is copied when something is wrong.
    /// </summary>
    private static async Task<string?> ApplyAsync(ModbotContext db, Partner partner, PartnerRequest body, CancellationToken ct)
    {
        var groupId = Clean(body.GroupId);
        if (groupId is { Length: > Partner.MaxGroupIdLength })
            return $"The group id can be at most {Partner.MaxGroupIdLength} characters.";

        var name = Clean(body.Name);
        if (name is null && groupId is not null)
        {
            // A name Modbot already holds for that group, from an instance popup. Not asked of
            // VRChat here.
            name = await db.OtherGroupNames.AsNoTracking()
                .Where(g => g.GroupId == groupId)
                .Select(g => g.Name)
                .FirstOrDefaultAsync(ct);
        }

        if (name is null)
            return "Give the group a name.";
        if (name.Length > Partner.MaxNameLength)
            return $"The name can be at most {Partner.MaxNameLength} characters.";

        var status = Clean(body.Status)?.ToLowerInvariant() ?? PartnerStatuses.Active;
        if (!PartnerStatuses.All.Contains(status))
            return "The status must be active, paused or ended.";

        var envoy = Clean(body.Envoy);
        if (envoy is { Length: > Partner.MaxEnvoyLength })
            return $"The envoy can be at most {Partner.MaxEnvoyLength} characters.";

        var contacts = (body.Contacts ?? []).Select(Clean).OfType<string>().ToArray();
        if (contacts.Length > Partner.MaxContacts)
            return $"There can be at most {Partner.MaxContacts} contacts.";
        if (contacts.Any(c => c.Length > Partner.MaxContactLength))
            return $"A contact can be at most {Partner.MaxContactLength} characters.";
        if (contacts.Any(c => c.Contains('\n') || c.Contains('\r')))
            return "A contact must be on one line.";

        var note = Clean(body.LastCollabNote);
        if (note is { Length: > Partner.MaxCollabNoteLength })
            return $"What the last collab was can be at most {Partner.MaxCollabNoteLength} characters.";

        var notes = Clean(body.Notes);
        if (notes is { Length: > Partner.MaxNotesLength })
            return $"The notes can be at most {Partner.MaxNotesLength} characters.";

        partner.Name = name;
        partner.GroupId = groupId;
        partner.Status = status;
        partner.Envoy = envoy;
        partner.Contacts = contacts;
        partner.LastCollabOn = body.LastCollabOn;
        partner.LastCollabNote = note;
        partner.Notes = notes;

        return null;
    }

    private static string? Clean(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Dictionary<string, string> Snapshot(Partner p) => new()
    {
        ["name"] = p.Name,
        ["groupId"] = p.GroupId ?? "",
        ["status"] = p.Status,
        ["envoy"] = p.Envoy ?? "",
        ["contacts"] = string.Join('\n', p.Contacts),
        ["lastCollabOn"] = p.LastCollabOn?.ToString("yyyy-MM-dd") ?? "",
        ["lastCollabNote"] = p.LastCollabNote ?? "",
        ["notes"] = p.Notes ?? "",
    };

    /// <summary>The names of the fields that differ, in the order they are kept.</summary>
    private static List<string> Differences(Dictionary<string, string> before, Dictionary<string, string> after)
        => [.. after.Keys.Where(k => before[k] != after[k])];

    private static PartnerView View(Partner p) => new(
        p.Id, p.Name, p.GroupId, p.Status, p.Envoy, p.Contacts, p.LastCollabOn, p.LastCollabNote, p.Notes,
        p.CreatedAt, p.UpdatedAt);
}
