using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Lists;
using Modbot.Api.Auth;
using Modbot.Api.Features.Lists;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordSync;

/// <summary>One saved list giving one Discord role.</summary>
/// <param name="Given">How many people this pairing gave the role to and has not taken it back from.</param>
/// <param name="Problem">Why the last pass changed nothing, or what Discord refused.</param>
/// <param name="StoppedAt">Since when the brake has stopped it, or null.</param>
/// <param name="StoppedTaking">How many people the stopped pass would take the role from.</param>
/// <param name="RemovalsAllowed">How many removals somebody allowed with Apply, while that is still in use.</param>
public sealed record ListRoleView(
    Guid Id,
    Guid ListId,
    string ListName,
    string DiscordRoleId,
    string? RoleName,
    bool Enabled,
    int Given,
    string? Problem,
    DateTimeOffset? StoppedAt,
    int? StoppedTaking,
    int? RemovalsAllowed);

/// <summary>A saved list, for the picker.</summary>
public sealed record ListChoiceView(Guid Id, string Name);

/// <param name="On">Whether lists give their roles at all.</param>
/// <param name="BotCanManageRoles">Whether the bot holds Manage Roles in the server.</param>
/// <param name="RanAt">When the pass last ran.</param>
/// <param name="CanPreview">Whether the person asking may see who would change: it names people in lists.</param>
/// <param name="CanApply">Whether the person asking may let a stopped pairing carry on.</param>
public sealed record ListRolesView(
    bool On,
    bool BotCanManageRoles,
    DateTimeOffset? RanAt,
    string? Problem,
    IReadOnlyList<ListRoleView> Roles,
    IReadOnlyList<ListChoiceView> Lists,
    bool CanPreview,
    bool CanApply);

public sealed record ListRolesUpdate(bool On);

/// <param name="Enabled">Null means on.</param>
public sealed record ListRoleRequest(Guid ListId, string? DiscordRoleId, bool? Enabled = null);

public sealed record ListRoleUpdate(bool Enabled);

/// <summary>
/// Which plans to work out: a saved pairing by <paramref name="Id"/>, a pairing not saved yet by
/// <paramref name="ListId"/> and <paramref name="DiscordRoleId"/>, or with neither every pairing that is on.
/// </summary>
public sealed record ListRolePreviewRequest(Guid? Id = null, Guid? ListId = null, string? DiscordRoleId = null);

/// <param name="Taking">How many removals the person saw in the preview they are agreeing to.</param>
/// <param name="Leaving">How many people the preview showed leaving the server, whose given-rows go.</param>
public sealed record ListRoleApplyRequest(int Taking, int Leaving = 0);

/// <param name="What"><c>give</c> or <c>take</c>.</param>
public sealed record ListRoleChangeView(string What, string DiscordUserId, string? VRChatUserId, string? Name);

/// <summary>What one pairing would do right now.</summary>
/// <param name="AlreadyHave">In the list and in the server, already holding the role.</param>
/// <param name="TakenByHand">In the list; Modbot gave them the role and somebody took it off by hand. Left alone.</param>
/// <param name="NoLinkedDiscord">In the list by their VRChat account, with no linked Discord account.</param>
/// <param name="NotInServer">In the list with a Discord account that is not in the server.</param>
/// <param name="Holders">Everybody in the server holding the role now.</param>
/// <param name="Leaving">People Modbot gave the role to and saw leave the server. Nothing is sent; Modbot forgets it gave it.</param>
/// <param name="TakesHeld">Removals not made this time, for the reason in <paramref name="HeldBecause"/>.</param>
/// <param name="HeldBecause">Why nothing is taken away this time though other changes go ahead.</param>
/// <param name="Stops">Whether this many losses at once stops the pass until somebody presses Apply.</param>
/// <param name="Changes">The changes, up to 500.</param>
public sealed record ListRolePlanView(
    Guid? Id,
    Guid ListId,
    string ListName,
    string DiscordRoleId,
    string? RoleName,
    int Giving,
    int Taking,
    int AlreadyHave,
    int TakenByHand,
    int NoLinkedDiscord,
    int NotInServer,
    int Holders,
    int Leaving,
    int TakesHeld,
    string? HeldBecause,
    bool Stops,
    string? Problem,
    IReadOnlyList<ListRoleChangeView> Changes);

public sealed record ListRolePreviewView(IReadOnlyList<ListRolePlanView> Plans);

/// <summary>
/// Settings → Discord → Roles from lists (roles from lists design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Setting up</strong> is <see cref="ModbotPermissions.ManageDiscordSync"/>, the role pairs'
/// permission, since this decides who holds a Discord role the same way a pair does.
/// <strong>Seeing who would change</strong> names people in a list, so it also needs what seeing a
/// list needs (lists design §6). <strong>Apply</strong>, which lets a stopped pairing take a role
/// from many people, is <see cref="ModbotPermissions.RunDiscordSync"/>.
/// </para>
/// <para>
/// <strong>Nothing here sends anything to Discord.</strong> Saving, the switch and Apply change what
/// Modbot is allowed to do; the sync loop does it within a minute, at its own pace, from the same
/// planner the preview uses.
/// </para>
/// </remarks>
public static class ListRoleEndpoints
{
    /// <summary>How many changes a preview lists for each pairing before it only counts.</summary>
    public const int MaxListed = 500;

    private const ModbotPermissions ToPreview = ModbotPermissions.ManageDiscordSync | ListEndpoints.ToSee;

    private const ModbotPermissions ToApply = ModbotPermissions.RunDiscordSync | ListEndpoints.ToSee;

    public static IEndpointRouteBuilder MapListRoles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord-list-roles").WithTags("Settings");

        group.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) => Results.Ok(await ViewAsync(http, db, ct)))
            .WithName("GetDiscordListRoles")
            .WithSummary("Get roles from lists")
            .WithDescription("The Discord roles saved lists give, the switch, and what the bot may do in the server.")
            .Produces<ListRolesView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] ListRolesUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                if (body.On && !settings.DiscordListRolesOn)
                {
                    var server = await ServerAsync(db, settings.DiscordGuildId, ct);

                    if (server is { BotCanManageRoles: false })
                        return Results.BadRequest(new { error = "The bot needs Manage Roles in the Discord server before lists can give roles." });
                }

                var change = new SettingsChange("discordListRoles")
                    .Field("on", settings.DiscordListRolesOn, body.On);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordListRolesOn = body.On;
                await db.SaveChangesAsync(ct);

                if (!change.IsEmpty)
                    await change.RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, ct));
            })
            .WithName("SetDiscordListRoles")
            .WithSummary("Update roles from lists")
            .WithDescription("Switch roles from lists on or off. Off gives and takes nothing.")
            .Produces<ListRolesView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPost("", async (
                HttpContext http,
                [FromBody] ListRoleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ListRolePlanner planner,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (string.IsNullOrWhiteSpace(body.DiscordRoleId))
                    return Results.BadRequest(new { error = "Pick a Discord role." });

                var list = await db.SavedLists.AsNoTracking()
                    .FirstOrDefaultAsync(l => l.Id == body.ListId && l.DeletedAt == null, ct);

                if (list is null)
                    return Results.BadRequest(new { error = "Pick a list." });

                if (ListRolePlanner.ReadRules(list.Rules, out var rulesProblem) is null)
                    return Results.BadRequest(new { error = rulesProblem });

                var roleId = body.DiscordRoleId.Trim();

                if (await planner.WhyNotAsync(roleId, null, ct) is { } refused)
                    return Results.BadRequest(new { error = refused });

                var now = clock.UtcNow;
                var pairing = new DiscordListRole
                {
                    ListId = list.Id,
                    DiscordRoleId = roleId,
                    DiscordRoleName = await RoleNameAsync(db, roleId, ct),
                    Enabled = body.Enabled ?? true,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.DiscordListRoles.Add(pairing);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordListRoles")
                    .Items<string>("roles", [], [Describe(pairing, list.Name)])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, ct));
            })
            .WithName("AddDiscordListRole")
            .WithSummary("Add role from a list")
            .WithDescription(
                "Give a Discord role to everybody in a saved list who is in the server, and take it from those "
                + "it gave it to once they leave the list. Refused for @everyone, a bot's role, a role the bot "
                + "cannot give, a role with a staff permission, a role something else already gives, and a "
                + "list that lets everybody in. Needs what the preview needs, since saving follows it.")
            .Produces<ListRolesView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ToPreview);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromBody] ListRoleUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var pairing = await db.DiscordListRoles.FirstOrDefaultAsync(p => p.Id == id, ct);

                if (pairing is null)
                    return Results.NotFound();

                var listName = await ListNameAsync(db, pairing.ListId, ct);
                var before = Describe(pairing, listName);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                pairing.Enabled = body.Enabled;
                pairing.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordListRoles")
                    .Items<string>("roles", [before], [Describe(pairing, listName)])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, ct));
            })
            .WithName("SetDiscordListRole")
            .WithSummary("Update role from a list")
            .WithDescription("Switch one list's role on or off. Off keeps the roles it gave where they are.")
            .Produces<ListRolesView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var pairing = await db.DiscordListRoles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

                // The rows of who it gave the role to go with it; the roles stay in Discord.
                await db.DiscordListRoles.Where(p => p.Id == id).ExecuteDeleteAsync(ct);

                if (pairing is not null)
                {
                    await new SettingsChange("discordListRoles")
                        .Items<string>("roles", [Describe(pairing, await ListNameAsync(db, pairing.ListId, ct))], [])
                        .RecordAsync(http, ct);
                }

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, ct));
            })
            .WithName("DeleteDiscordListRole")
            .WithSummary("Delete role from a list")
            .WithDescription(
                "Stop a list giving a Discord role. Nobody loses the role: Modbot forgets who it gave it to, "
                + "and the role stays with everybody who has it.")
            .Produces<ListRolesView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPost("/preview", async (
                [FromBody] ListRolePreviewRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ListRolePlanner planner,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.Id is { } id)
                {
                    var pairing = await db.DiscordListRoles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

                    return pairing is null
                        ? Results.NotFound()
                        : Results.Ok(new ListRolePreviewView([View(await planner.PlanAsync(pairing, ct))]));
                }

                if (body.ListId is { } listId)
                {
                    if (string.IsNullOrWhiteSpace(body.DiscordRoleId))
                        return Results.BadRequest(new { error = "Pick a Discord role." });

                    return Results.Ok(new ListRolePreviewView([View(await planner.PlanNewAsync(listId, body.DiscordRoleId, ct))]));
                }

                var plans = await planner.PlanAllAsync(onlyEnabled: true, ct);
                return Results.Ok(new ListRolePreviewView([.. plans.Select(View)]));
            })
            .WithName("PreviewDiscordListRoles")
            .WithSummary("Preview roles from lists")
            .WithDescription(
                "Who would be given a list's Discord role and who would lose it right now, and how many in the "
                + "list have no linked Discord account or are not in the server. Changes nothing. One saved "
                + "pairing by `id`, one not saved yet by `listId` and `discordRoleId`, or every pairing that "
                + "is on.")
            .Produces<ListRolePreviewView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ToPreview);

        group.MapPost("/{id:guid}/apply", async (
                HttpContext http,
                Guid id,
                [FromBody] ListRoleApplyRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ListRolePlanner planner,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var pairing = await db.DiscordListRoles.FirstOrDefaultAsync(p => p.Id == id, ct);

                if (pairing is null)
                    return Results.NotFound();

                var plan = await planner.PlanAsync(pairing, ct);

                if (plan.Problem is { } problem)
                    return Results.Conflict(new { error = problem });

                // The numbers are what the person looked at. More than that is a list they have not
                // seen, so it is refused rather than allowed on the strength of an older look.
                if (plan.Taking > body.Taking || plan.Leaving > body.Leaving)
                    return Results.Conflict(new { error = "More would be taken away than you saw. Look again." });

                var change = new SettingsChange("discordListRoles")
                    .Field("removalsAllowed", pairing.RemovalsAllowed, (int?)plan.Losing);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                pairing.RemovalsAllowed = plan.Losing;
                pairing.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);

                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, ct));
            })
            .WithName("ApplyDiscordListRole")
            .WithSummary("Apply role from a list")
            .WithDescription(
                "Let a list's role carry on after it stopped because it would take the role from many people "
                + "at once. `taking` and `leaving` are what the preview showed; refused if more would go "
                + "now. Nothing is sent from here: the next pass, within a minute, makes the changes, and the "
                + "allowance shrinks as they are made.")
            .Produces<ListRolesView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequiresFlag(ToApply);

        return app;
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One pairing in the words the audit log shows: "Regulars gives Regular, on".</summary>
    private static string Describe(DiscordListRole pairing, string listName)
        => $"{(listName.Length > 0 ? listName : pairing.ListId.ToString())} gives "
           + $"{pairing.DiscordRoleName ?? pairing.DiscordRoleId}, {(pairing.Enabled ? "on" : "off")}";

    private static async Task<string> ListNameAsync(ModbotContext db, Guid listId, CancellationToken ct)
        => await db.SavedLists.AsNoTracking().Where(l => l.Id == listId).Select(l => l.Name).FirstOrDefaultAsync(ct)
           ?? string.Empty;

    private static async Task<string?> RoleNameAsync(ModbotContext db, string roleId, CancellationToken ct)
    {
        var guildId = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.DiscordGuildId).FirstOrDefaultAsync(ct);

        return await db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.RoleId == roleId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct);
    }

    private static Task<DiscordServer?> ServerAsync(ModbotContext db, string? guildId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(guildId)
            ? Task.FromResult<DiscordServer?>(null)
            : db.DiscordServers.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);

    private static ListRolePlanView View(ListRolePlan plan) => new(
        plan.ListRoleId,
        plan.ListId,
        plan.ListName,
        plan.DiscordRoleId,
        plan.RoleName,
        plan.Giving,
        plan.Taking,
        plan.AlreadyHave,
        plan.TakenByHand,
        plan.NoLinkedDiscord,
        plan.NotInServer,
        plan.Holders,
        plan.Leaving,
        plan.TakesHeld,
        plan.HeldBecause,
        plan.Stops,
        plan.Problem,
        [.. plan.Changes.Take(MaxListed).Select(c => new ListRoleChangeView(c.What, c.DiscordUserId, c.VRChatUserId, c.Name))]);

    private static async Task<ListRolesView> ViewAsync(HttpContext http, ModbotContext db, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);
        var server = await ServerAsync(db, settings.DiscordGuildId, ct);
        var state = await db.DiscordSyncState.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var roleNames = settings.DiscordGuildId is null
            ? []
            : await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == settings.DiscordGuildId && r.RemovedAt == null)
                .ToDictionaryAsync(r => r.RoleId, r => r.Name, StringComparer.Ordinal, ct);

        var lists = await db.SavedLists.AsNoTracking()
            .Select(l => new { l.Id, l.Name, l.DeletedAt })
            .ToListAsync(ct);

        var listNames = lists.ToDictionary(l => l.Id, l => l.Name);

        var given = await db.DiscordListRolesGiven.AsNoTracking()
            .GroupBy(g => g.ListRoleId)
            .Select(g => new { ListRoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ListRoleId, g => g.Count, ct);

        var pairings = await db.DiscordListRoles.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var held = ModbotAuth.PermissionsOf(http.User);

        return new ListRolesView(
            settings.DiscordListRolesOn,
            server?.BotCanManageRoles ?? false,
            state?.ListRolesRanAt,
            state?.ListRolesProblem,
            [.. pairings.Select(p => new ListRoleView(
                p.Id,
                p.ListId,
                listNames.GetValueOrDefault(p.ListId) ?? string.Empty,
                p.DiscordRoleId,
                roleNames.TryGetValue(p.DiscordRoleId, out var name) ? name : p.DiscordRoleName,
                p.Enabled,
                given.GetValueOrDefault(p.Id),
                p.Problem,
                p.StoppedAt,
                p.StoppedTaking,
                p.RemovalsAllowed))],
            [.. lists.Where(l => l.DeletedAt == null).OrderBy(l => l.Name).Select(l => new ListChoiceView(l.Id, l.Name))],
            ModbotAuth.Allows(held, ListEndpoints.ToSee),
            ModbotAuth.Allows(held, ToApply));
    }
}
