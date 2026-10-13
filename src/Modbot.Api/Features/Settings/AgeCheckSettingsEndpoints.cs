using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.AgeChecks;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Settings;

/// <summary>One role of the VRChat group, as the group role picker lists it.</summary>
/// <param name="Id">VRChat's id for the role. Opaque.</param>
/// <param name="Name">The role's name.</param>
public sealed record AgeCheckGroupRole(string Id, string Name);

/// <summary>Settings → Discord → 18+ check, as stored.</summary>
/// <param name="GiveRoleIds">The Discord roles a member is given.</param>
/// <param name="RemoveRoleIds">The Discord roles taken away.</param>
/// <param name="GroupRoleId">The VRChat group role the person is given. Null for none.</param>
/// <param name="DeleteTicket">Delete the ticket once the check is done.</param>
/// <param name="TicketCategoryId">The category whose channels may be deleted. Null for none.</param>
/// <param name="GroupRoles">The group's roles, for the picker.</param>
public sealed record AgeCheckSettingsResponse(
    IReadOnlyList<string> GiveRoleIds,
    IReadOnlyList<string> RemoveRoleIds,
    string? GroupRoleId,
    bool DeleteTicket,
    string? TicketCategoryId,
    IReadOnlyList<AgeCheckGroupRole> GroupRoles);

/// <summary>Changes to Settings → Discord → 18+ check. Every part left out keeps what is stored.</summary>
/// <param name="GiveRoleIds">The Discord roles to give. An empty list for none.</param>
/// <param name="RemoveRoleIds">The Discord roles to take away. An empty list for none.</param>
/// <param name="GroupRoleId">The VRChat group role. Empty for none.</param>
/// <param name="DeleteTicket">Delete the ticket once the check is done.</param>
/// <param name="TicketCategoryId">The category. Empty for none.</param>
public sealed record AgeCheckSettingsUpdate(
    List<string>? GiveRoleIds = null,
    List<string>? RemoveRoleIds = null,
    string? GroupRoleId = null,
    bool? DeleteTicket = null,
    string? TicketCategoryId = null);

/// <summary>
/// Settings → Discord → 18+ check: the Discord roles to give and take away, the VRChat group role to
/// give, and whether to delete the ticket. Needs Manage settings. Every change is recorded as a
/// settings change.
/// </summary>
public static class AgeCheckSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAgeCheckSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/age-check")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetAgeCheckSettings")
            .WithSummary("Get 18+ check settings")
            .WithDescription(
                "The Discord roles the 18+ check gives and takes away, the VRChat group role it gives, "
                + "whether it deletes the ticket and the category whose channels it may delete. "
                + "`groupRoles` lists the group's roles for the picker.")
            .Produces<AgeCheckSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] AgeCheckSettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                var giveBefore = settings.AgeCheckGiveRoleIds ?? [];
                var removeBefore = settings.AgeCheckRemoveRoleIds ?? [];
                var giveAfter = body.GiveRoleIds is null ? giveBefore : Clean(body.GiveRoleIds);
                var removeAfter = body.RemoveRoleIds is null ? removeBefore : Clean(body.RemoveRoleIds);
                var groupRoleAfter = body.GroupRoleId is null ? settings.AgeCheckGroupRoleId : Clean(body.GroupRoleId);
                var deleteAfter = body.DeleteTicket ?? settings.AgeCheckDeleteTicket;
                var categoryAfter = body.TicketCategoryId is null ? settings.AgeCheckTicketCategoryId : Clean(body.TicketCategoryId);

                var problems = new List<string>();

                foreach (var roleId in giveAfter.Except(giveBefore, StringComparer.Ordinal))
                {
                    if (await AgeCheckRoleCheck.RefusalAsync(db, roleId, ct) is { } refusal)
                        problems.Add(refusal);
                }

                foreach (var roleId in giveAfter.Intersect(removeAfter, StringComparer.Ordinal))
                {
                    var name = await AgeCheckRoleCheck.NameOfAsync(db, roleId, ct) ?? roleId;
                    problems.Add($"{name} is in both lists. A role is given or taken away, not both.");
                }

                // The linked roles job gives these back as soon as they are taken away.
                foreach (var roleId in removeAfter)
                {
                    if (roleId == settings.DiscordLinkedRoleId || roleId == settings.DiscordEighteenPlusRoleId)
                    {
                        var name = await AgeCheckRoleCheck.NameOfAsync(db, roleId, ct) ?? roleId;
                        problems.Add($"{name} is the Linked or the 18+ role, which Modbot gives to linked members itself, so it cannot be taken away here.");
                    }
                }

                if (groupRoleAfter is not null
                    && groupRoleAfter != settings.AgeCheckGroupRoleId
                    && GroupRolesOf(settings) is { Count: > 0 } known
                    && !known.Any(r => string.Equals(r.Id, groupRoleAfter, StringComparison.Ordinal)))
                {
                    problems.Add("That role is not in the VRChat group.");
                }

                if (categoryAfter is not null
                    && categoryAfter != settings.AgeCheckTicketCategoryId
                    && !await db.DiscordChannels.AsNoTracking().AnyAsync(
                        c => c.GuildId == settings.DiscordGuildId
                            && c.ChannelId == categoryAfter
                            && c.Type == DiscordChannelTypes.Category,
                        ct))
                {
                    problems.Add("That is not a category in the Discord server.");
                }

                if (problems.Count > 0)
                    return Results.BadRequest(new { error = string.Join(" ", problems), problems });

                var change = new SettingsChange("ageCheck")
                    .Items("giveRoleIds", giveBefore, giveAfter)
                    .Items("removeRoleIds", removeBefore, removeAfter)
                    .Field("groupRoleId", settings.AgeCheckGroupRoleId, groupRoleAfter)
                    .Field("deleteTicket", settings.AgeCheckDeleteTicket, deleteAfter)
                    .Field("ticketCategoryId", settings.AgeCheckTicketCategoryId, categoryAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.AgeCheckGiveRoleIds = giveAfter.Count == 0 ? null : giveAfter;
                settings.AgeCheckRemoveRoleIds = removeAfter.Count == 0 ? null : removeAfter;
                settings.AgeCheckGroupRoleId = groupRoleAfter;
                settings.AgeCheckDeleteTicket = deleteAfter;
                settings.AgeCheckTicketCategoryId = categoryAfter;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetAgeCheckSettings")
            .WithSummary("Update 18+ check settings")
            .WithDescription(
                "Change the Discord roles the 18+ check gives (`giveRoleIds`) and takes away "
                + "(`removeRoleIds`), the VRChat group role it gives (`groupRoleId`, empty for none), "
                + "whether it deletes the ticket (`deleteTicket`) and the category whose channels it may "
                + "delete (`ticketCategoryId`, empty for none). A role that holds a dangerous permission "
                + "cannot be given; a role cannot be in both lists; the Linked and 18+ roles cannot be "
                + "taken away. A refusal (400) lists everything wrong at once, in `problems`.")
            .Accepts<AgeCheckSettingsUpdate>("application/json")
            .Produces<AgeCheckSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The ids as typed: trimmed, blanks dropped, each once, in order.</summary>
    private static List<string> Clean(IEnumerable<string> ids)
        => [.. ids.Select(Clean).OfType<string>().Distinct(StringComparer.Ordinal)];

    private static IReadOnlyList<GroupRoleSnapshot> GroupRolesOf(Core.Data.Entities.Settings settings)
        => GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles ?? [];

    private static AgeCheckSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(
            settings.AgeCheckGiveRoleIds ?? [],
            settings.AgeCheckRemoveRoleIds ?? [],
            settings.AgeCheckGroupRoleId,
            settings.AgeCheckDeleteTicket,
            settings.AgeCheckTicketCategoryId,
            [.. GroupRolesOf(settings).Select(r => new AgeCheckGroupRole(r.Id, r.Name ?? r.Id))]);
}
