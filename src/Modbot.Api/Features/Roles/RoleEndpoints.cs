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

namespace Modbot.Api.Features.Roles;

/// <summary>One role, as the roles page shows it.</summary>
/// <param name="Locked">
/// True for Administrator: the one role that must always mean everything, so nothing about it
/// can be edited.
/// </param>
/// <param name="UserCount">How many accounts hold it. A role somebody holds cannot be deleted.</param>
public sealed record RoleView(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<string> PermissionNames,
    bool IsBuiltIn,
    bool Locked,
    int UserCount)
{
    public static RoleView From(ModbotRole role, int userCount) => new(
        role.Id,
        role.Name,
        role.Description,
        PermissionCatalog.NamesOf(role.Permissions),
        role.IsBuiltIn,
        role.Id == BuiltInRoles.AdministratorId,
        userCount);
}

/// <param name="Permissions">The catalogue, with labels, so the page and the API use the same words.</param>
public sealed record RolesResponse(IReadOnlyList<RoleView> Roles, IReadOnlyList<PermissionInfo> Permissions);

/// <param name="Permissions">Permission names from the catalogue.</param>
public sealed record RoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions);

/// <summary>
/// Roles: named sets of permissions (accounts and access design §3).
/// </summary>
/// <remarks>
/// <para>
/// Reading is open to any signed-in, linked account -- the users page needs the list to assign
/// from, and what a role means is not a secret within the team. Changing needs
/// <see cref="ModbotPermissions.ManageRoles"/>, and a caller may only put permissions into a role
/// that they hold themselves; otherwise manage-roles plus manage-users would add up to
/// Administrator by way of a role.
/// </para>
/// <para>
/// Built-in roles keep their names and cannot be deleted. Administrator cannot be edited at all.
/// A role people hold cannot be deleted: move them first, because silently stripping three
/// accounts' access is not what anyone pressing Delete on a role expects.
/// </para>
/// </remarks>
public static class RoleEndpoints
{
    public static IEndpointRouteBuilder MapRoles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/roles", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var roles = await db.Roles.AsNoTracking()
                    .OrderByDescending(r => r.IsBuiltIn)
                    .ThenBy(r => r.NameNormalized)
                    .ToListAsync(ct);

                var counts = await db.UserRoles.AsNoTracking()
                    .GroupBy(ur => ur.RoleId)
                    .Select(g => new { RoleId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.RoleId, g => g.Count, ct);

                return Results.Ok(new RolesResponse(
                    roles.Select(r => RoleView.From(r, counts.GetValueOrDefault(r.Id))).ToList(),
                    PermissionCatalog.All));
            })
            .WithTags("Roles")
            .WithName("ListRoles")
            .WithSummary("List roles")
            .WithDescription("Every role, and the permission catalogue.")
            .Produces<RolesResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        var manage = app.MapGroup("/api/roles")
            .WithTags("Roles")
            .RequiresFlag(ModbotPermissions.ManageRoles);

        manage.MapPost("/", async (
                [FromBody] RoleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Validate(http, body, out var permissions) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var normalized = ModbotRole.Normalize(body.Name);
                if (await db.Roles.AnyAsync(r => r.NameNormalized == normalized, ct))
                    return Results.Conflict(new { error = "A role with that name already exists." });

                var role = new ModbotRole
                {
                    Name = body.Name.Trim(),
                    NameNormalized = normalized,
                    Description = body.Description?.Trim() ?? string.Empty,
                    Permissions = permissions,
                    CreatedAt = clock.UtcNow,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Roles.Add(role);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.RoleCreated,
                    role.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["name"] = role.Name,
                        ["permissions"] = string.Join(", ", PermissionCatalog.NamesOf(role.Permissions)),
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(RoleView.From(role, 0));
            })
            .WithName("CreateRole")
            .WithSummary("Add role")
            .WithDescription("Create a role.")
            .Produces<RoleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        manage.MapPut("/{id:guid}", async (
                Guid id,
                [FromBody] RoleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (role is null)
                    return Results.NotFound();

                if (role.Id == BuiltInRoles.AdministratorId)
                    return Results.BadRequest(new { error = "The Administrator role cannot be edited." });

                if (Validate(http, body, out var permissions) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var normalized = ModbotRole.Normalize(body.Name);

                if (role.IsBuiltIn && normalized != role.NameNormalized)
                    return Results.BadRequest(new { error = "Built-in roles cannot be renamed." });

                if (normalized != role.NameNormalized
                    && await db.Roles.AnyAsync(r => r.NameNormalized == normalized && r.Id != id, ct))
                {
                    return Results.Conflict(new { error = "A role with that name already exists." });
                }

                var before = new JsonObject
                {
                    ["name"] = role.Name,
                    ["permissions"] = string.Join(", ", PermissionCatalog.NamesOf(role.Permissions)),
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                role.Name = body.Name.Trim();
                role.NameNormalized = normalized;
                role.Description = body.Description?.Trim() ?? string.Empty;
                role.Permissions = permissions;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.RoleChanged,
                    role.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["before"] = before,
                        ["after"] = new JsonObject
                        {
                            ["name"] = role.Name,
                            ["permissions"] = string.Join(", ", PermissionCatalog.NamesOf(role.Permissions)),
                        },
                    },
                    ct);

                await transaction.CommitAsync(ct);

                var count = await db.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
                return Results.Ok(RoleView.From(role, count));
            })
            .WithName("UpdateRole")
            .WithSummary("Update role")
            .WithDescription(
                "Change a role's name, description or permissions. "
                + "Takes effect for everyone holding the role on their next request.")
            .Produces<RoleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        manage.MapDelete("/{id:guid}", async (
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (role is null)
                    return Results.NotFound();

                if (role.IsBuiltIn)
                    return Results.BadRequest(new { error = "Built-in roles cannot be deleted." });

                var holders = await db.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
                if (holders > 0)
                {
                    return Results.Conflict(new
                    {
                        error = holders == 1
                            ? "One person holds this role."
                            : $"{holders} people hold this role.",
                    });
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Roles.Remove(role);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.RoleDeleted,
                    role.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["name"] = role.Name },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("DeleteRole")
            .WithSummary("Delete role")
            .WithDescription("Delete a role nobody holds.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static string? Validate(HttpContext http, RoleRequest body, out ModbotPermissions permissions)
    {
        permissions = ModbotPermissions.None;

        if (string.IsNullOrWhiteSpace(body.Name))
            return "A role needs a name.";

        if (body.Name.Trim().Length > 64)
            return "That role name is longer than 64 characters.";

        if ((body.Description?.Length ?? 0) > 256)
            return "That description is longer than 256 characters.";

        permissions = PermissionCatalog.Parse(body.Permissions ?? [], out var error);
        if (error is not null)
            return error;

        if (!ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), permissions))
            return "You can only put permissions into a role that you have yourself.";

        return null;
    }
}
