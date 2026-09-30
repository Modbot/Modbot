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
using Modbot.Core.Users;

namespace Modbot.Api.Features.Roles;

/// <summary>One role, as the roles page shows it.</summary>
/// <param name="Locked">
/// True for Administrator: the one role that must always mean everything, so nothing about it
/// can be edited.
/// </param>
/// <param name="UserCount">How many accounts hold it. A role somebody holds cannot be deleted.</param>
/// <param name="Position">
/// Where it sits in the order, first at 0 (design §3.5). The list comes back in this order, and a
/// person's rank is the position of their highest role. Administrator always counts as first,
/// whatever number is stored for it.
/// </param>
public sealed record RoleView(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<string> PermissionNames,
    bool IsBuiltIn,
    bool Locked,
    int UserCount,
    int Position = 0)
{
    public static RoleView From(ModbotRole role, int userCount) => new(
        role.Id,
        role.Name,
        role.Description,
        PermissionCatalog.NamesOf(role.Permissions),
        role.IsBuiltIn,
        role.Id == BuiltInRoles.AdministratorId,
        userCount,
        RoleRank.PositionOf(role));
}

/// <param name="Direction">"up" for one place higher, "down" for one place lower.</param>
public sealed record MoveRoleRequest(string Direction);

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
/// <strong>Only below your highest role</strong> (design §3.5): a caller may only edit, delete or
/// move a role that sits below their own highest role, and may not move one up to their own place
/// or above it. A new role is made at the bottom. Administrator, who is above the rule, may
/// reorder every role except Administrator, which is always first.
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
                CancellationToken ct) => Results.Ok(await ListAsync(db, ct)))
            .WithTags("Roles")
            .WithName("ListRoles")
            .WithSummary("List roles")
            .WithDescription("Every role, highest first, and the permission catalogue.")
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
                await LockOrderAsync(db, ct);

                // At the bottom, below every role that exists (design §3.5): a person making a
                // role never makes one that outranks themselves. Read under the lock, so two made
                // at once cannot take the same place.
                role.Position = (await db.Roles.MaxAsync(r => (int?)r.Position, ct) ?? -1) + 1;

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
            .WithDescription("Create a role, at the bottom of the order.")
            .Produces<RoleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        manage.MapPut("/{id:guid}", async (
                Guid id,
                [FromBody] RoleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (role is null)
                    return Results.NotFound();

                if (await RoleOrder.MayNotChangeRolesAsync(http, accounts, [role], ct) is { } outranked)
                    return outranked;

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
                + "Takes effect for everyone holding the role on their next request. "
                + "Refused unless the role is below your highest role.")
            .Produces<RoleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        manage.MapDelete("/{id:guid}", async (
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (role is null)
                    return Results.NotFound();

                if (await RoleOrder.MayNotChangeRolesAsync(http, accounts, [role], ct) is { } outranked)
                    return outranked;

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
            .WithDescription("Delete a role nobody holds. Refused unless the role is below your highest role.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        manage.MapPost("/{id:guid}/move", async (
                Guid id,
                [FromBody] MoveRoleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var up = string.Equals(body.Direction, "up", StringComparison.OrdinalIgnoreCase);
                if (!up && !string.Equals(body.Direction, "down", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Direction is up or down." });

                // The order is read and written under one lock, so two moves at once cannot leave
                // two roles in the same place.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await LockOrderAsync(db, ct);

                var roles = await OrderedAsync(db, ct);
                var index = roles.FindIndex(r => r.Id == id);
                if (index < 0)
                    return Results.NotFound();

                var role = roles[index];

                if (await RoleOrder.MayNotChangeRolesAsync(http, accounts, [role], ct) is { } outranked)
                    return outranked;

                // Administrator, and any role that carries the Administrator permission, is
                // always first: nothing moves it and nothing moves above it.
                if (RoleRank.IsAdministrator(role))
                    return Results.BadRequest(new { error = "The Administrator role is always first." });

                var otherIndex = up ? index - 1 : index + 1;
                if (otherIndex < 0 || otherIndex >= roles.Count)
                    return Results.BadRequest(new { error = up ? "That role is already first." : "That role is already last." });

                var other = roles[otherIndex];

                if (RoleRank.IsAdministrator(other))
                    return Results.BadRequest(new { error = "The Administrator role is always first." });

                // The role it changes places with moves too. Moving up, that is the one above,
                // and it must be below the caller as well: otherwise this role would take the
                // caller's own place and push the caller down.
                if (up && await RoleOrder.MayNotChangeRolesAsync(http, accounts, [other], ct) is { } blocked)
                    return blocked;

                if (role.Position != other.Position)
                {
                    // The two are next to each other in the order, so trading their numbers moves
                    // them past each other and leaves every other role exactly where it is.
                    (role.Position, other.Position) = (other.Position, role.Position);
                }
                else
                {
                    // Two roles sharing a number (two made at the same moment) would make a trade
                    // do nothing: number everything again, in the order it was shown.
                    (roles[index], roles[otherIndex]) = (roles[otherIndex], roles[index]);
                    for (var i = 0; i < roles.Count; i++)
                        roles[i].Position = i;
                }

                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.RoleChanged,
                    role.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["name"] = role.Name,
                        ["moved"] = up ? "up" : "down",
                        ["past"] = other.Name,
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ListAsync(db, ct));
            })
            .WithName("MoveRole")
            .WithSummary("Move role")
            .WithDescription(
                "Move a role one place up or down in the order. A person's rank is their highest "
                + "role. Refused for a role that is not below your highest role, and for a move "
                + "that would put a role at your own place or above it. Administrator is always "
                + "first. Returns the roles in their new order.")
            .Produces<RolesResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Names the lock so two people changing the order at the same moment take turns. Any stable
    /// string works; the prefix matches the other locks Modbot takes.
    /// </summary>
    private const string OrderLock = "modbot:roles:order";

    private static Task LockOrderAsync(ModbotContext db, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({OrderLock}))", ct);

    /// <summary>
    /// The roles highest first: by where they count as sitting (Administrator first whatever
    /// number is stored), then by stored position, then the older first, so ties are stable.
    /// </summary>
    private static async Task<List<ModbotRole>> OrderedAsync(ModbotContext db, CancellationToken ct)
    {
        var roles = await db.Roles.ToListAsync(ct);
        return Sorted(roles);
    }

    private static List<ModbotRole> Sorted(IEnumerable<ModbotRole> roles)
        => [.. roles
            .OrderBy(RoleRank.PositionOf)
            .ThenBy(r => r.Position)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)];

    private static async Task<RolesResponse> ListAsync(ModbotContext db, CancellationToken ct)
    {
        var roles = Sorted(await db.Roles.AsNoTracking().ToListAsync(ct));

        var counts = await db.UserRoles.AsNoTracking()
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.RoleId, g => g.Count, ct);

        return new RolesResponse(
            roles.Select(r => RoleView.From(r, counts.GetValueOrDefault(r.Id))).ToList(),
            PermissionCatalog.All);
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
