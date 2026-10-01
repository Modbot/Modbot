using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// The group's roles on VRChat, from the VRChat page's Settings → Roles tab: the list, a new role,
/// a change to one, and deleting one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read from VRChat when asked, never polled.</strong> One request when the tab opens and
/// one per Refresh. Nothing else asks.
/// </para>
/// <para>
/// <strong>A write is one request, and nothing is recorded unless VRChat accepted it.</strong> Then
/// one fact names who made the change, and the stored group snapshot takes the roles VRChat answered
/// with, so the next group poll does not record the same change again without a name on it. The
/// role's id travels in the body, or in the address on <c>PUT</c> and <c>DELETE /{id}</c> (API
/// conventions design §4); the route has no constraint, so its format is checked in neither place
/// (foundation §3.1.1).
/// </para>
/// <para>
/// <strong>Permissions are VRChat's own words, read from its answer.</strong> The SDK's permission
/// type is an enum, and a permission VRChat adds before the SDK knows it would vanish from the list
/// and then, on the next Save, from the role. So the rows are read from VRChat's JSON, and the body
/// sent back is written by <see cref="GroupRoleChange"/>.
/// </para>
/// </remarks>
public static class GroupRoleEndpoints
{
    public static IEndpointRouteBuilder MapGroupRoles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/group/roles").WithTags("Group page").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoleManager? vrchat,
                CancellationToken ct) =>
            {
                if (vrchat is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                var answer = await vrchat.ListAsync(groupId, ct);

                // Never an empty list in place of one Modbot could not read.
                if (!answer.Success)
                    return GroupPageAnswers.Refused(answer, "GetGroupRoles", groupId, settings);

                return Results.Ok(new GroupRoleList(
                    RowsOf(answer.RawResponse, settings)
                        .OrderBy(r => r.Order)
                        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("GetGroupRoles")
            .WithSummary("List group roles")
            .WithDescription(
                "The group's roles, read from VRChat when you ask, in VRChat's order. Each role's "
                + "`permissions` are VRChat's own ids (`group-bans-manage`; `*` is every permission). "
                + "`heldByModbot` says whether Modbot's VRChat account has the role, as last read. One "
                + "request to VRChat per call, never retried.")
            .Produces<GroupRoleList>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/", (
                HttpContext http,
                [FromBody] GroupRoleBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoleManager? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).SaveAsync(http, body, creating: true, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("CreateGroupRole")
            .WithSummary("Create group role")
            .WithDescription(
                "Create a role in the group on VRChat. `name` is required; `description` and "
                + "`permissions` (VRChat's ids) are optional. One request, never retried; a refusal "
                + "answers with VRChat's own message, and a 403 for a permission Modbot's VRChat "
                + "account lacks names it in `missingGroupPermission`. Once VRChat accepts, the audit "
                + "log records who created it.")
            .Produces<GroupRoleSaved>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/", (
                HttpContext http,
                [FromBody] GroupRoleBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoleManager? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).SaveAsync(http, body, creating: false, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("UpdateGroupRole")
            .WithMetadata(new ReplacedBy("PUT /api/group/roles/{id}"))
            .WithSummary("Update group role")
            .WithDescription(
                "Change a role: `id` names it. Leave `name`, `description` or `permissions` out to "
                + "keep them; `permissions` is the whole list, which VRChat replaces. Only the fields "
                + "that differ from what Modbot last recorded are sent. One request, never retried. "
                + "Once VRChat accepts, the audit log records who changed it and each field's old and "
                + "new value.")
            .Produces<GroupRoleSaved>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/delete", (
                HttpContext http,
                [FromBody] GroupRoleDelete body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoleManager? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).DeleteAsync(http, body, ct))
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("DeleteGroupRole")
            .WithMetadata(new ReplacedBy("DELETE /api/group/roles/{id}"))
            .WithSummary("Delete group role")
            .WithDescription(
                "Delete a role on VRChat. `id` names it; `name` is kept in the audit log with the "
                + "deletion, as the page showed it. Members who had the role lose it. One request, "
                + "never retried. A role VRChat no longer has answers 404.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        // The same change and deletion with the role in the address, as the rest of the API names
        // what it acts on (API conventions design §4). A route with no constraint checks nothing
        // about the id's format, so §3.1.1 holds here as it does in a body.
        group.MapPut("/{id}", async (
                string id,
                HttpContext http,
                [FromBody] GroupRoleBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoleManager? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.Id is { Length: > 0 } named && !string.Equals(named, id, StringComparison.Ordinal))
                    return GroupPageAnswers.Invalid("The address and the body name different roles.");

                return await new Writes(db, clock, vrchat, facts, partitions)
                    .SaveAsync(http, body with { Id = id }, creating: false, ct);
            })
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("UpdateGroupRoleById")
            .WithSummary("Update group role")
            .WithDescription(
                "Change the role named in the address. Leave `name`, `description` or `permissions` "
                + "out to keep them; `permissions` is the whole list, which VRChat replaces. Only the "
                + "fields that differ from what Modbot last recorded are sent. One request, never "
                + "retried. Once VRChat accepts, the audit log records who changed it and each "
                + "field's old and new value.")
            .Produces<GroupRoleSaved>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/{id}", (
                string id,
                [FromQuery] string? name,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupRoleManager? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                new Writes(db, clock, vrchat, facts, partitions).DeleteAsync(http, new GroupRoleDelete(id, name), ct))
            .RequiresFlag(ModbotPermissions.ManageGroupRoles)
            .WithName("DeleteGroupRoleById")
            .WithSummary("Delete group role")
            .WithDescription(
                "Delete the role named in the address on VRChat. `name`, when given, is kept in the "
                + "audit log with the deletion, as the page showed it. Members who had the role lose "
                + "it. One request, never retried. A role VRChat no longer has answers 404.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>The three writes, with what they share in one place.</summary>
    private sealed class Writes(
        ModbotContext db,
        IModbotClock clock,
        GroupRoleManager? vrchat = null,
        IFactWriter? facts = null,
        EventPartitionMaintainer? partitions = null)
    {
        public async Task<IResult> SaveAsync(HttpContext http, GroupRoleBody body, bool creating, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(body);

            if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                return Results.Forbid();

            if (vrchat is null || facts is null || partitions is null)
                return GroupPageAnswers.NotSetUp();

            // Tracked: a write stores the roles VRChat answers with in the snapshot.
            var settings = await db.GetSettingsAsync(ct);

            if (settings.ManagedGroupId is not { Length: > 0 } groupId)
                return GroupPageAnswers.NoGroup();

            var (role, problem) = GroupPageRules.TidyRole(body, creating);
            if (role is null)
                return GroupPageAnswers.Invalid(problem!);

            var before = creating ? null : RecordedRole(settings, role.Id!);
            var changes = GroupPageRules.RoleChanges(before, role);

            if (!creating && changes.Count == 0)
                return GroupPageAnswers.Invalid("Nothing about the role has changed.");

            var changed = changes.Select(c => c.Field).ToHashSet(StringComparer.Ordinal);

            var request = creating
                ? new GroupRoleChange(role.Name, role.Description, role.Permissions)
                : new GroupRoleChange(
                    changed.Contains("name") ? role.Name : null,
                    changed.Contains("description") ? role.Description : null,
                    changed.Contains("permissions") ? role.Permissions : null);

            string? savedRaw;
            string roleId;

            if (creating)
            {
                var answer = await vrchat.CreateAsync(groupId, request, ct);

                if (!answer.Success)
                    return GroupPageAnswers.Refused(answer, "CreateGroupRole", groupId, settings);

                roleId = answer.Value?.Id ?? string.Empty;
                savedRaw = answer.RawResponse;
                GroupInfoSync.RecordRoles(settings, added: answer.Value);
            }
            else
            {
                var answer = await vrchat.UpdateAsync(groupId, role.Id!, request, ct);

                if (!answer.Success)
                    return GroupPageAnswers.Refused(answer, "UpdateGroupRole", groupId, settings);

                roleId = role.Id!;
                savedRaw = answer.RawResponse;
                GroupInfoSync.RecordRoles(settings, all: answer.Value);
            }

            var saved = RowsOf(savedRaw, settings).FirstOrDefault(r => string.Equals(r.Id, roleId, StringComparison.Ordinal))
                ?? new GroupRoleRow(
                    roleId, role.Name ?? before?.Name, role.Description ?? before?.Description,
                    role.Permissions ?? before?.Permissions ?? [], 0, false, false, false, false, false);

            var data = creating
                ? new JsonObject
                {
                    ["roleId"] = roleId,
                    ["name"] = saved.Name,
                    ["description"] = saved.Description,
                    ["permissions"] = new JsonArray([.. saved.Permissions.Select(p => (JsonNode?)JsonValue.Create(p))]),
                }
                : new JsonObject
                {
                    ["roleId"] = roleId,
                    ["name"] = saved.Name,
                    ["changed"] = Changed(changes),
                };

            var now = clock.UtcNow;

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await GroupPageAnswers.WriteFactAsync(
                    facts, partitions, creating ? FactType.GroupRoleMade : FactType.GroupRoleEdited,
                    groupId, actor, now, data, ct);

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            return Results.Ok(new GroupRoleSaved(saved));
        }

        public async Task<IResult> DeleteAsync(HttpContext http, GroupRoleDelete body, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(body);

            if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                return Results.Forbid();

            if (vrchat is null || facts is null || partitions is null)
                return GroupPageAnswers.NotSetUp();

            var settings = await db.GetSettingsAsync(ct);

            if (settings.ManagedGroupId is not { Length: > 0 } groupId)
                return GroupPageAnswers.NoGroup();

            if (string.IsNullOrWhiteSpace(body.Id))
                return GroupPageAnswers.Invalid("Say which role to delete.");

            var name = string.IsNullOrWhiteSpace(body.Name) ? RecordedRole(settings, body.Id)?.Name : body.Name.Trim();

            var answer = await vrchat.DeleteAsync(groupId, body.Id, ct);

            if (!answer.Success)
            {
                // Somebody deleted it in VRChat first: the row was out of date, nothing failed.
                return answer.StatusCode == StatusCodes.Status404NotFound
                    ? Results.Json(
                        new { error = "That role is no longer there. Somebody may have deleted it in VRChat." },
                        statusCode: StatusCodes.Status404NotFound)
                    : GroupPageAnswers.Refused(answer, "DeleteGroupRole", groupId, settings);
            }

            GroupInfoSync.RecordRoles(settings, all: answer.Value);

            var now = clock.UtcNow;

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await GroupPageAnswers.WriteFactAsync(
                    facts, partitions, FactType.GroupRoleRemoved, groupId, actor, now,
                    new JsonObject { ["roleId"] = body.Id, ["name"] = name },
                    ct);

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            return Results.NoContent();
        }
    }

    private static JsonObject Changed(IReadOnlyList<(string Field, JsonNode? Old, JsonNode? New)> changes)
    {
        var fields = new JsonObject();
        foreach (var (field, old, @new) in changes)
            fields[field] = new JsonObject { ["old"] = old, ["new"] = @new };
        return fields;
    }

    /// <summary>
    /// A role as the last group read recorded it, with its permissions in VRChat's words; null when
    /// Modbot has no record of it.
    /// </summary>
    private static (string? Name, string? Description, IReadOnlyList<string> Permissions)? RecordedRole(
        Core.Data.Entities.Settings settings, string roleId)
    {
        var role = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles
            .FirstOrDefault(r => string.Equals(r.Id, roleId, StringComparison.Ordinal));

        return role is null
            ? null
            : (role.Name, role.Description, role.Permissions.Select(VRChatGroupPermissions.IdOf).ToList());
    }

    /// <summary>
    /// The roles in VRChat's answer, read from its own JSON: a list of roles, or one role. Anything
    /// else is no roles. See the class remarks for why not the SDK's model.
    /// </summary>
    internal static IReadOnlyList<GroupRoleRow> RowsOf(string? raw, Core.Data.Entities.Settings? settings)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var held = settings?.VRChatAccountRoleIds ?? [];

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            var items = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray().ToList(),
                JsonValueKind.Object => [root],
                _ => [],
            };

            return items
                .Where(e => e.ValueKind == JsonValueKind.Object && Text(e, "id") is { Length: > 0 })
                .Select(e =>
                {
                    var id = Text(e, "id")!;
                    return new GroupRoleRow(
                        id,
                        Text(e, "name"),
                        Text(e, "description"),
                        e.TryGetProperty("permissions", out var list) && list.ValueKind == JsonValueKind.Array
                            ? list.EnumerateArray()
                                .Where(p => p.ValueKind == JsonValueKind.String)
                                .Select(p => p.GetString()!)
                                .Where(p => p.Length > 0)
                                .Distinct(StringComparer.Ordinal)
                                .ToList()
                            : [],
                        e.TryGetProperty("order", out var order) && order.TryGetInt32(out var o) ? o : 0,
                        Flag(e, "defaultRole"),
                        Flag(e, "isManagementRole"),
                        Flag(e, "isSelfAssignable"),
                        Flag(e, "requiresTwoFactor"),
                        held.Contains(id, StringComparer.Ordinal));
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
