using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Roles;
using Modbot.Api.Features.Settings;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.DiscordStaffRoles;

/// <summary>One mapping: a Discord role that gives a Modbot role.</summary>
/// <param name="Direction">discord (Discord decides) or both (both ways).</param>
/// <param name="NotSetUp">
/// A both-ways link the bot cannot give the Discord role for, which works as Discord decides until it
/// can; or any link while Modbot is not receiving member updates, when nothing is given or taken.
/// </param>
/// <param name="CanChange">Whether the caller may change or remove it: the Modbot role is below their highest role.</param>
/// <param name="Problem">The last change in Discord it asked for that was refused.</param>
public sealed record StaffRoleView(
    Guid Id,
    string DiscordRoleId,
    string? DiscordRoleName,
    Guid RoleId,
    string RoleName,
    string Direction,
    bool NotSetUp,
    bool CanChange,
    string? Problem);

/// <summary>A Modbot role, for the picker.</summary>
/// <param name="CanMap">Whether the caller may map a Discord role to it: not Administrator, below their highest role, no permission they lack.</param>
public sealed record StaffRoleTarget(Guid Id, string Name, int Position, bool CanMap);

/// <param name="On">The switch. Off gives and takes nothing.</param>
/// <param name="HeldAt">Since when the pass has been stopped by the brake, or null.</param>
/// <param name="HeldCount">How many accounts the stopped pass would take roles from.</param>
public sealed record StaffRolesView(
    bool On,
    DateTimeOffset? RanAt,
    string? Problem,
    DateTimeOffset? HeldAt,
    int? HeldCount,
    IReadOnlyList<StaffRoleView> Mappings,
    IReadOnlyList<StaffRoleTarget> Roles);

/// <param name="Direction">discord or both.</param>
public sealed record StaffRoleRequest(string DiscordRoleId, Guid RoleId, string Direction);

/// <param name="Id">The saved mapping this is, or null for one not saved yet.</param>
public sealed record StaffRolePreviewMapping(Guid? Id, string DiscordRoleId, Guid RoleId, string Direction);

/// <param name="Mappings">Every mapping as it would be after the change being looked at.</param>
public sealed record StaffRolesPreviewRequest(IReadOnlyList<StaffRolePreviewMapping> Mappings);

/// <param name="What">give, take, give-discord, take-discord, no-account or not-proven.</param>
/// <param name="ByHand">For a take: the role was given by hand rather than by a Discord role.</param>
public sealed record StaffRoleChangeView(
    string What,
    Guid? UserId,
    string? Name,
    string? DiscordUserId,
    string RoleName,
    string? DiscordRoleName,
    bool ByHand,
    string Why);

/// <param name="Covered">How many accounts the mappings reach.</param>
/// <param name="WouldStop">Whether the brake would stop the pass, so it waits for Apply.</param>
/// <param name="Notes">Members holding a mapped Discord role whom nothing can be done for.</param>
public sealed record StaffRolesPreviewView(
    int Covered,
    bool WouldStop,
    IReadOnlyList<StaffRoleChangeView> Changes,
    IReadOnlyList<StaffRoleChangeView> Notes,
    IReadOnlyList<string> Problems);

public sealed record StaffRolesSwitchRequest(bool On);

public sealed record StaffRolesApplyView(int Given, int Taken, int Left, string? Problem, StaffRolesView Settings);

/// <summary>
/// Staff roles from Discord (design 2026-10-02): which Discord roles give which Modbot roles, the
/// switch, the preview and the Apply button.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One list, two screens.</strong> The roles page edits the mappings of one role and
/// Settings → Discord the whole list; both call these endpoints, so each shows what the other made.
/// </para>
/// <para>
/// <strong>Manage roles and Manage users together.</strong> A mapping changes who holds a role by a
/// rule about a role. Saving one is held to the rules for handing a role out by hand (accounts and
/// access design §3.5): the role must be below the caller's highest role and allow nothing the
/// caller lacks. A role carrying the Administrator permission can never be mapped.
/// </para>
/// </remarks>
public static class StaffRoleEndpoints
{
    /// <summary>The most changes a preview lists. The counts still cover all of them.</summary>
    public const int MaxListed = 500;

    public static IEndpointRouteBuilder MapStaffRoles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/staff-roles")
            .WithTags("Roles")
            .RequiresFlag(ModbotPermissions.ManageRoles | ModbotPermissions.ManageUsers);

        group.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                CancellationToken ct) => Results.Ok(await ViewAsync(http, db, accounts, ct)))
            .WithName("GetStaffRoles")
            .WithSummary("Get staff roles from Discord")
            .WithDescription("Which Discord roles give which Modbot roles, the switch, and when the pass last ran.")
            .Produces<StaffRolesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/preview", async (
                [FromBody] StaffRolesPreviewRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // A saved row keeps what only the server knows about it: when it was saved, and
                // whether Discord refused it lately. Changing a row's sides or direction is a save,
                // so it is treated as one that has not been refused.
                var saved = (await StaffRoles.RulesAsync(db, ct)).ToDictionary(r => r.Id);

                var rules = new List<StaffRoleRule>();
                foreach (var m in body.Mappings ?? [])
                {
                    if (string.IsNullOrWhiteSpace(m.DiscordRoleId) || !StaffRoleDirections.IsKnown(m.Direction))
                        return Results.BadRequest(new { error = BadMapping });

                    var rule = new StaffRoleRule(m.Id ?? Guid.CreateVersion7(), m.DiscordRoleId.Trim(), m.RoleId, m.Direction);
                    if (m.Id is { } id && saved.TryGetValue(id, out var was)
                        && was.DiscordRoleId == rule.DiscordRoleId && was.RoleId == rule.RoleId && was.Direction == rule.Direction)
                    {
                        rule = was;
                    }

                    rules.Add(rule);
                }

                var plan = await StaffRoles.PlanAsync(db, rules, withNotes: true, clock.UtcNow, ct);
                return Results.Ok(View(plan));
            })
            .WithName("PreviewStaffRoles")
            .WithSummary("Preview staff roles from Discord")
            .WithDescription("Who would gain or lose a role if the mappings were these. Changes nothing.")
            .Produces<StaffRolesPreviewView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("", async (
                HttpContext http,
                [FromBody] StaffRoleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(db, ct);

                var checkedBody = await CheckAsync(http, db, accounts, body, null, ct);
                if (checkedBody.Refusal is { } refusal)
                    return refusal;

                var now = clock.UtcNow;
                var mapping = new DiscordStaffRole
                {
                    DiscordRoleId = body.DiscordRoleId.Trim(),
                    DiscordRoleName = checkedBody.DiscordRole!.Name,
                    RoleId = checkedBody.Role!.Id,
                    Direction = body.Direction,
                    CreatedById = ModbotAuth.UserIdOf(http.User),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                var after = (await StaffRoles.RulesAsync(db, ct)).Append(Rule(mapping) with { SavedAt = null }).ToList();
                var plan = await StaffRoles.PlanAsync(db, after, withNotes: false, now, ct);

                db.DiscordStaffRoles.Add(mapping);
                await db.SaveChangesAsync(ct);
                await RecordAsync(facts, http, FactType.StaffRoleMapped, mapping, checkedBody.Role, plan, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, accounts, ct));
            })
            .WithName("AddStaffRole")
            .WithSummary("Add staff role from Discord")
            .WithDescription("Let a Discord role give a Modbot role.")
            .Produces<StaffRolesView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromBody] StaffRoleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var mapping = await db.DiscordStaffRoles.Include(m => m.Role).FirstOrDefaultAsync(m => m.Id == id, ct);
                if (mapping is null)
                    return Results.NotFound();

                // The role it gives now is the caller's to change as well as the one it will give.
                if (await RoleOrder.MayNotChangeRolesAsync(http, accounts, [mapping.Role], ct) is { } outranked)
                    return outranked;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(db, ct);

                var checkedBody = await CheckAsync(http, db, accounts, body, id, ct);
                if (checkedBody.Refusal is { } refusal)
                    return refusal;

                var oldRoleId = mapping.RoleId;
                var changedSides = mapping.DiscordRoleId != body.DiscordRoleId.Trim()
                                   || mapping.RoleId != checkedBody.Role!.Id
                                   || mapping.Direction != body.Direction;

                var proposed = (await StaffRoles.RulesAsync(db, ct))
                    .Select(r => r.Id == id ? new StaffRoleRule(id, body.DiscordRoleId.Trim(), checkedBody.Role!.Id, body.Direction) : r)
                    .ToList();
                var plan = await StaffRoles.PlanAsync(db, proposed, withNotes: false, clock.UtcNow, ct);

                mapping.DiscordRoleId = body.DiscordRoleId.Trim();
                mapping.DiscordRoleName = checkedBody.DiscordRole!.Name;
                mapping.RoleId = checkedBody.Role!.Id;
                mapping.Role = checkedBody.Role;
                mapping.Direction = body.Direction;
                mapping.UpdatedAt = clock.UtcNow;
                mapping.Problem = null;
                mapping.RefusedAt = null;
                await db.SaveChangesAsync(ct);

                // What the two sides agreed on under the old shape says nothing about the new one.
                if (changedSides)
                    await db.DiscordStaffRoleStates.Where(s => s.MappingId == id).ExecuteDeleteAsync(ct);

                if (oldRoleId != mapping.RoleId)
                    await ForgetFromDiscordAsync(db, oldRoleId, ct);

                await RecordAsync(facts, http, FactType.StaffRoleMapped, mapping, checkedBody.Role, plan, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, accounts, ct));
            })
            .WithName("SetStaffRole")
            .WithSummary("Update staff role from Discord")
            .WithDescription("Change which Discord role gives which Modbot role, or which way it works.")
            .Produces<StaffRolesView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var mapping = await db.DiscordStaffRoles.Include(m => m.Role).FirstOrDefaultAsync(m => m.Id == id, ct);
                if (mapping is null)
                    return Results.Ok(await ViewAsync(http, db, accounts, ct));

                if (await RoleOrder.MayNotChangeRolesAsync(http, accounts, [mapping.Role], ct) is { } outranked)
                    return outranked;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.DiscordStaffRoles.Remove(mapping);
                await db.SaveChangesAsync(ct);

                // Removing a mapping takes nothing away: what it gave becomes an ordinary role.
                await ForgetFromDiscordAsync(db, mapping.RoleId, ct);

                await RecordAsync(facts, http, FactType.StaffRoleUnmapped, mapping, mapping.Role, null, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, accounts, ct));
            })
            .WithName("DeleteStaffRole")
            .WithSummary("Delete staff role from Discord")
            .WithDescription("Stop a Discord role giving a Modbot role. Nobody's roles change.")
            .Produces<StaffRolesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/on", async (
                HttpContext http,
                [FromBody] StaffRolesSwitchRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var change = new SettingsChange("staffRoles").Field("on", settings.DiscordStaffRolesOn, body.On);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordStaffRolesOn = body.On;
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(http, db, accounts, ct));
            })
            .WithName("SetStaffRolesOn")
            .WithSummary("Switch staff roles from Discord")
            .WithDescription("Turn giving and taking Modbot roles from Discord roles on or off.")
            .Produces<StaffRolesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/apply", async (
                HttpContext http,
                [FromServices] IStaffRoleRunner runner,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                // Apply carries out what the brake held back, so the person pressing it must be
                // allowed to make every one of those changes by hand: each role given or taken is
                // below their highest role, and so is each account. The plan is worked out once and
                // the runner carries out that same plan, so what was checked is what runs.
                var plan = await StaffRoles.PlanAsync(db, null, withNotes: false, clock.UtcNow, ct);

                var roleIds = plan.Changes.Select(c => c.RoleId).Distinct().ToList();
                var changedRoles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
                if (await RoleOrder.MayNotChangeRolesAsync(http, accounts, changedRoles, ct) is { } outranked)
                    return outranked;

                // And nothing it gives allows what the presser cannot do themselves.
                var givenRoleIds = plan.Changes.Where(c => !StaffRoleChangeKinds.TakesAway(c.What)).Select(c => c.RoleId).ToHashSet();
                var heldPermissions = ModbotAuth.PermissionsOf(http.User);
                if (changedRoles.Any(r => givenRoleIds.Contains(r.Id) && !ModbotAuth.Allows(heldPermissions, r.Permissions)))
                    return Results.BadRequest(new { error = "You can only give people permissions you have yourself." });

                var userIds = plan.Changes.Where(c => c.UserId is not null).Select(c => c.UserId!.Value).Distinct().ToList();
                var affected = await accounts.UsersWithRoles().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync(ct);
                foreach (var account in affected)
                {
                    if (await RoleOrder.MayNotChangeAccountAsync(http, accounts, account, ct) is { } above)
                        return above;
                }

                var pass = await runner.ApplyAsync(plan, ct);

                await facts.RecordAsync(
                    FactType.StaffRolesApplied,
                    Subject,
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["given"] = pass.Given,
                        ["taken"] = pass.Taken,
                        ["left"] = pass.Left,
                        ["problem"] = pass.Problem,
                    },
                    ct);

                var view = await ViewAsync(http, db, accounts, ct);

                return Results.Ok(new StaffRolesApplyView(pass.Given, pass.Taken, pass.Left, pass.Problem, view));
            })
            .WithName("ApplyStaffRoles")
            .WithSummary("Apply staff roles from Discord")
            .WithDescription("Run the pass now, even when it would take roles from many accounts at once.")
            .Produces<StaffRolesApplyView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    // ── Checks ─────────────────────────────────────────────────────────────────────────────

    internal const string BadMapping = "Pick a Discord role, a Modbot role and which way it works.";

    internal const string NotADiscordRole = "Pick a Discord role from the server. @everyone and roles owned by bots cannot be used.";

    internal const string AdministratorRole = "No Discord role can give a role that carries Administrator.";

    internal const string DiscordRoleTaken = "That Discord role already gives a Modbot role.";

    internal const string OneForBothWays = "A role that works both ways can have only one Discord role.";

    internal const string PairedElsewhere = "That Discord role is paired with a group role, so it cannot work both ways.";

    internal const string GivenByList = "A list already gives that Discord role, so it cannot work both ways.";

    internal const string PowerfulRole =
        "A Discord role that can ban, kick, manage the server or manage roles cannot work both ways.";

    /// <summary>The subject of the facts about linked roles as a whole.</summary>
    internal const string Subject = "staff-roles";

    private sealed record Checked(IResult? Refusal, ModbotRole? Role, DiscordRole? DiscordRole);

    private static async Task<Checked> CheckAsync(
        HttpContext http, ModbotContext db, UserAccountService accounts, StaffRoleRequest body, Guid? exceptId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.DiscordRoleId) || !StaffRoleDirections.IsKnown(body.Direction))
            return Refuse(BadMapping);

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == body.RoleId, ct);
        if (role is null)
            return Refuse(BadMapping);

        if (RoleRank.IsAdministrator(role))
            return Refuse(AdministratorRole);

        if (!ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), role.Permissions))
            return Refuse("You can only give people permissions you have yourself.");

        if (await RoleOrder.MayNotChangeRolesAsync(http, accounts, [role], ct) is { } outranked)
            return new Checked(outranked, null, null);

        var guildId = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.DiscordGuildId).FirstOrDefaultAsync(ct);
        var discordRoleId = body.DiscordRoleId.Trim();

        var discordRole = string.IsNullOrWhiteSpace(guildId)
            ? null
            : await db.DiscordRoles.AsNoTracking().FirstOrDefaultAsync(r => r.GuildId == guildId && r.RoleId == discordRoleId, ct);

        if (discordRole is null || !StaffRoles.CanMap(discordRole))
            return Refuse(NotADiscordRole);

        var others = await db.DiscordStaffRoles.AsNoTracking()
            .Where(m => exceptId == null || m.Id != exceptId)
            .Select(m => new { m.DiscordRoleId, m.RoleId, m.Direction })
            .ToListAsync(ct);

        if (others.Any(m => m.DiscordRoleId == discordRoleId))
            return Refuse(DiscordRoleTaken);

        var sameRole = others.Where(m => m.RoleId == role.Id).ToList();
        if (sameRole.Count > 0 && (body.Direction == StaffRoleDirections.Both || sameRole.Any(m => m.Direction == StaffRoleDirections.Both)))
            return Refuse(OneForBothWays);

        if (body.Direction == StaffRoleDirections.Both)
        {
            // Two syncs writing the same Discord role would undo each other.
            if (await db.DiscordRolePairs.AsNoTracking().AnyAsync(p => p.DiscordRoleId == discordRoleId, ct))
                return Refuse(PairedElsewhere);

            // A list giving the role would undo what this decides every minute (roles from lists design §4).
            if (await db.DiscordListRoles.AsNoTracking().AnyAsync(p => p.DiscordRoleId == discordRoleId, ct))
                return Refuse(GivenByList);

            // Both ways hands the Discord role out from Modbot: never one that carries power over
            // the server. A role whose permissions are not read yet counts as one that might.
            if (StaffRoles.IsPowerful(discordRole))
                return Refuse(PowerfulRole);
        }

        return new Checked(null, role, discordRole);
    }

    /// <summary>One save at a time, shared with VRChat role pairs (<see cref="StaffRoles.LockSavesAsync"/>).</summary>
    private static Task LockAsync(ModbotContext db, CancellationToken ct) => StaffRoles.LockSavesAsync(db, ct);

    private static Checked Refuse(string sentence) => new(Results.BadRequest(new { error = sentence }), null, null);

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private static StaffRoleRule Rule(DiscordStaffRole m) => new(m.Id, m.DiscordRoleId, m.RoleId, m.Direction, m.UpdatedAt, m.RefusedAt);

    /// <summary>
    /// With no mapping left for a role, the roles the mappings gave become ordinary ones: they no
    /// longer go with a Discord role or with the Discord account.
    /// </summary>
    private static async Task ForgetFromDiscordAsync(ModbotContext db, Guid roleId, CancellationToken ct)
    {
        if (await db.DiscordStaffRoles.AnyAsync(m => m.RoleId == roleId, ct))
            return;

        await db.UserRoles
            .Where(r => r.RoleId == roleId && r.FromDiscord)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.FromDiscord, false), ct);
    }

    private static Task RecordAsync(
        AccountFacts facts, HttpContext http, string type, DiscordStaffRole mapping, ModbotRole role, StaffRolePlan? plan, CancellationToken ct)
    {
        var data = new JsonObject
        {
            ["mappingId"] = mapping.Id.ToString(),
            ["name"] = role.Name,
            ["roleId"] = role.Id.ToString(),
            ["discordRoleId"] = mapping.DiscordRoleId,
            ["discordRoleName"] = mapping.DiscordRoleName,
            ["direction"] = mapping.Direction,
        };

        if (plan is not null)
        {
            data["gains"] = plan.Changes.Count(c => !StaffRoleChangeKinds.TakesAway(c.What));
            data["losses"] = plan.Changes.Count(c => StaffRoleChangeKinds.TakesAway(c.What));
        }

        return facts.RecordAsync(type, role.Id.ToString(), Actor.Of(http), data, ct);
    }

    private static async Task<StaffRolesView> ViewAsync(HttpContext http, ModbotContext db, UserAccountService accounts, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);
        var state = await db.DiscordSyncState.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var discordRoles = string.IsNullOrWhiteSpace(settings.DiscordGuildId)
            ? []
            : await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == settings.DiscordGuildId)
                .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct);

        var now = http.RequestServices.GetRequiredService<IModbotClock>().UtcNow;
        var rolesChangedAt = discordRoles.Count == 0 ? (DateTimeOffset?)null : discordRoles.Values.Max(r => r.UpdatedAt);

        // Without member updates the stored roles go stale, so every link is Not set up and says why.
        var noMemberUpdates = StaffRoles.MemberUpdatesMissing(state, now);

        var roles = await db.Roles.AsNoTracking().ToListAsync(ct);
        var callerIsAdministrator = RoleOrder.IsAdministrator(http);
        var callerRank = await RoleOrder.CallerRankAsync(http, accounts, ct);
        var held = ModbotAuth.PermissionsOf(http.User);

        bool Below(ModbotRole role) => callerIsAdministrator || RoleRank.IsBelow(RoleRank.PositionOf(role), callerRank);

        var byId = roles.ToDictionary(r => r.Id);

        var mappings = await db.DiscordStaffRoles.AsNoTracking()
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return new StaffRolesView(
            settings.DiscordStaffRolesOn,
            state?.StaffRolesRanAt,
            noMemberUpdates ? StaffRoles.NoMemberUpdates : state?.StaffRolesProblem,
            state?.StaffRolesHeldAt,
            state?.StaffRolesHeldCount,
            mappings
                .Where(m => byId.ContainsKey(m.RoleId))
                .Select(m =>
                {
                    var role = byId[m.RoleId];
                    var discordRole = discordRoles.GetValueOrDefault(m.DiscordRoleId);

                    return new StaffRoleView(
                        m.Id,
                        m.DiscordRoleId,
                        discordRole is { Name.Length: > 0 } ? discordRole.Name : m.DiscordRoleName,
                        role.Id,
                        role.Name,
                        m.Direction,
                        noMemberUpdates
                            || (m.Direction == StaffRoleDirections.Both && !StaffRoles.Works(Rule(m), discordRole, rolesChangedAt, now)),
                        Below(role),
                        m.Problem);
                })
                .ToList(),
            roles
                .OrderBy(RoleRank.PositionOf)
                .ThenBy(r => r.Position)
                .Select(r => new StaffRoleTarget(
                    r.Id,
                    r.Name,
                    RoleRank.PositionOf(r),
                    !RoleRank.IsAdministrator(r) && Below(r) && ModbotAuth.Allows(held, r.Permissions)))
                .ToList());
    }

    private static StaffRolesPreviewView View(StaffRolePlan plan)
    {
        static StaffRoleChangeView One(StaffRoleChange c) => new(c.What, c.UserId, c.Name, c.DiscordUserId, c.RoleName, c.DiscordRoleName, c.ByHand, c.Why);

        // Takes first: they are what somebody saving a mapping most needs to see.
        var changes = plan.Changes
            .OrderBy(c => StaffRoleChangeKinds.TakesAway(c.What) ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListed)
            .Select(One)
            .ToList();

        return new StaffRolesPreviewView(
            plan.Covered,
            StaffRoles.Brakes(plan.AccountsLosing, plan.Covered),
            changes,
            plan.Notes.Take(MaxListed).Select(One).ToList(),
            plan.Problems);
    }
}
