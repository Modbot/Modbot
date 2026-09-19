using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.DiscordSync;

/// <param name="Decides">vrchat, discord or nobody.</param>
/// <param name="Problem">The last change this pair asked for that was refused.</param>
public sealed record RolePairView(
    Guid Id,
    string VRChatRoleId,
    string? VRChatRoleName,
    string DiscordRoleId,
    string? DiscordRoleName,
    string Decides,
    bool Enabled,
    bool BotCanAssign,
    string? Problem);

/// <param name="BanCopyAction">ban or remove.</param>
/// <param name="BotCanBanMembers">Whether the bot holds Ban Members in the server.</param>
/// <param name="BotCanRemoveMembers">Whether the bot holds Kick Members in the server.</param>
/// <param name="BotCanManageRoles">Whether the bot holds Manage Roles in the server.</param>
public sealed record DiscordSyncSettingsView(
    bool RoleSyncOn,
    bool BanSyncToDiscord,
    bool BanSyncToVRChat,
    string BanCopyAction,
    bool BotCanBanMembers,
    bool BotCanRemoveMembers,
    bool BotCanManageRoles,
    DateTimeOffset? RolesRanAt,
    string? RolesProblem,
    DateTimeOffset? BansReadAt,
    string? BansProblem,
    IReadOnlyList<RolePairView> Pairs,
    IReadOnlyList<GroupRoleView> GroupRoles);

/// <summary>One of the managed group's roles, for the pair form to pick from.</summary>
public sealed record GroupRoleView(string Id, string Name);

public sealed record DiscordSyncSettingsUpdate(
    bool RoleSyncOn,
    bool BanSyncToDiscord,
    bool BanSyncToVRChat,
    string? BanCopyAction);

public sealed record RolePairUpdate(string VRChatRoleId, string DiscordRoleId, string Decides, bool Enabled);

/// <summary>One change a sync would make, for the screen.</summary>
public sealed record PlannedChangeView(
    string What,
    string Platform,
    string? VRChatUserId,
    string? DiscordUserId,
    string? Name,
    string? RoleName,
    string Why);

public sealed record SyncPreviewView(int Total, IReadOnlyList<PlannedChangeView> Changes, string? Problem);

/// <summary>
/// Settings → Discord → Role and ban sync (M5 §3, §4).
/// </summary>
/// <remarks>
/// <para>
/// Two permissions, not one. Setting the pairs up and seeing what a sync would do is
/// <see cref="ModbotPermissions.ManageDiscordSync"/>; making it happen against a server that is
/// already running is <see cref="ModbotPermissions.RunDiscordSync"/>, because the first run can
/// ban or move hundreds of people in one press.
/// </para>
/// <para>
/// <strong>A permission the bot does not hold is refused here</strong>, with the permission named,
/// rather than discovered as a failure on the first pass (M5 §7). A bot that has never connected
/// is not second-guessed: nothing is known about what it may do, so the switch is allowed and the
/// pass reports the refusal if there is one.
/// </para>
/// <para>
/// <strong>Switching a ban direction on starts from now.</strong> The marker ban sync reads from is
/// moved to the newest fact, so turning a switch on cannot replay years of history as a flood of
/// bans. The backlog is the catch-up button, pressed after looking at the list.
/// </para>
/// </remarks>
public static class DiscordSyncEndpoints
{
    public static IEndpointRouteBuilder MapDiscordSync(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord-sync").WithTags("Settings");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) => Results.Ok(await ViewAsync(db, ct)))
            .WithName("GetDiscordSync")
            .WithSummary("Get Discord sync")
            .WithDescription("Role pairs, the sync switches, and what the bot may do in the server.")
            .Produces<DiscordSyncSettingsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPut("", async (
                [FromBody] DiscordSyncSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var action = body.BanCopyAction ?? DiscordBanCopyActions.Ban;

                if (!DiscordBanCopyActions.IsKnown(action))
                    return Results.BadRequest(new { error = "A VRChat ban can either ban in Discord or remove from the server." });

                var settings = await db.GetSettingsAsync(ct);
                var server = await ServerAsync(db, settings.DiscordGuildId, ct);

                if (body.RoleSyncOn && !settings.DiscordRoleSyncOn && server is { BotCanManageRoles: false })
                    return Results.BadRequest(new { error = "The bot needs Manage Roles in the Discord server before roles can be kept in step." });

                if (body.BanSyncToDiscord && !settings.DiscordBanSyncToDiscord && server is not null)
                {
                    if (action == DiscordBanCopyActions.Ban && !server.BotCanBanMembers)
                        return Results.BadRequest(new { error = "The bot needs Ban Members in the Discord server before a group ban can be copied there." });

                    if (action == DiscordBanCopyActions.Remove && !server.BotCanRemoveMembers)
                        return Results.BadRequest(new { error = "The bot needs Kick Members in the Discord server before a group ban can remove somebody there." });
                }

                // A direction going from off to on starts from now, not from the beginning of the
                // log. The bans that are already different are the catch-up button's business.
                var switchedOn = (body.BanSyncToDiscord && !settings.DiscordBanSyncToDiscord)
                                 || (body.BanSyncToVRChat && !settings.DiscordBanSyncToVRChat);

                settings.DiscordRoleSyncOn = body.RoleSyncOn;
                settings.DiscordBanSyncToDiscord = body.BanSyncToDiscord;
                settings.DiscordBanSyncToVRChat = body.BanSyncToVRChat;
                settings.DiscordBanCopyAction = action;

                if (switchedOn)
                    await StartFromNowAsync(db, clock, ct);

                await db.SaveChangesAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("SetDiscordSync")
            .WithSummary("Update Discord sync")
            .WithDescription("Save the sync switches.")
            .Produces<DiscordSyncSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPost("/pairs", async (
                [FromBody] RolePairUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Refuse(body) is { } refusal)
                    return Results.BadRequest(new { error = refusal });

                var vrchatRoleId = body.VRChatRoleId.Trim();
                var discordRoleId = body.DiscordRoleId.Trim();

                var taken = await db.DiscordRolePairs.AsNoTracking()
                    .AnyAsync(p => p.VRChatRoleId == vrchatRoleId || p.DiscordRoleId == discordRoleId, ct);

                if (taken)
                    return Results.BadRequest(new { error = "One of those roles is already paired with another." });

                var now = clock.UtcNow;

                var pair = new DiscordRolePair
                {
                    VRChatRoleId = vrchatRoleId,
                    DiscordRoleId = discordRoleId,
                    Decides = body.Decides,
                    Enabled = body.Enabled,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                db.DiscordRolePairs.Add(pair);
                await NameRolesAsync(db, pair, ct);
                await db.SaveChangesAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("AddDiscordRolePair")
            .WithSummary("Add role pair")
            .WithDescription("Pair a group role with a Discord role.")
            .Produces<DiscordSyncSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPut("/pairs/{id:guid}", async (
                Guid id,
                [FromBody] RolePairUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Refuse(body) is { } refusal)
                    return Results.BadRequest(new { error = refusal });

                var pair = await db.DiscordRolePairs.FirstOrDefaultAsync(p => p.Id == id, ct);

                if (pair is null)
                    return Results.NotFound();

                var vrchatRoleId = body.VRChatRoleId.Trim();
                var discordRoleId = body.DiscordRoleId.Trim();

                var taken = await db.DiscordRolePairs.AsNoTracking()
                    .AnyAsync(p => p.Id != id && (p.VRChatRoleId == vrchatRoleId || p.DiscordRoleId == discordRoleId), ct);

                if (taken)
                    return Results.BadRequest(new { error = "One of those roles is already paired with another." });

                pair.VRChatRoleId = vrchatRoleId;
                pair.DiscordRoleId = discordRoleId;
                pair.Decides = body.Decides;
                pair.Enabled = body.Enabled;
                pair.UpdatedAt = clock.UtcNow;
                pair.Problem = null;

                await NameRolesAsync(db, pair, ct);
                await db.SaveChangesAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("SetDiscordRolePair")
            .WithSummary("Update role pair")
            .WithDescription("Change a role pair.")
            .Produces<DiscordSyncSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapDelete("/pairs/{id:guid}", async (
                Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                await db.DiscordRolePairs.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("DeleteDiscordRolePair")
            .WithSummary("Delete role pair")
            .WithDescription("Stop pairing two roles. Neither role is changed.")
            .Produces<DiscordSyncSettingsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPost("/preview", async (
                [FromServices] IDiscordSyncRunner runner,
                CancellationToken ct) => Results.Ok(View(await runner.PreviewAsync(ct))))
            .WithName("PreviewDiscordSync")
            .WithSummary("Preview Discord sync")
            .WithDescription("What the two syncs would change right now. Changes nothing.")
            .Produces<SyncPreviewView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageDiscordSync);

        group.MapPost("/run", async (
                [FromServices] IDiscordSyncRunner runner,
                CancellationToken ct) => Results.Ok(View(await runner.CatchUpAsync(ct))))
            .WithName("RunDiscordSync")
            .WithSummary("Run Discord sync")
            .WithDescription("Copy the roles and bans that are already different.")
            .Produces<SyncPreviewView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.RunDiscordSync);

        return app;
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private static string? Refuse(RolePairUpdate body)
    {
        if (string.IsNullOrWhiteSpace(body.VRChatRoleId))
            return "Pick a group role.";

        if (string.IsNullOrWhiteSpace(body.DiscordRoleId))
            return "Pick a Discord role.";

        if (!RoleSyncDecides.IsKnown(body.Decides))
            return "A pair is decided by VRChat, by Discord, or by nobody.";

        return null;
    }

    /// <summary>
    /// Writes today's names for both roles onto the pair, so a fact about a role change years from
    /// now can say "Staff" rather than an id.
    /// </summary>
    private static async Task NameRolesAsync(ModbotContext db, DiscordRolePair pair, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        pair.DiscordRoleName = await db.DiscordRoles.AsNoTracking()
            .Where(r => r.RoleId == pair.DiscordRoleId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct);

        pair.VRChatRoleName = (GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot)?.Roles ?? [])
            .FirstOrDefault(r => r.Id == pair.VRChatRoleId)?.Name;
    }

    /// <summary>Moves the ban marker to the newest fact, so a switch turned on starts from now.</summary>
    private static async Task StartFromNowAsync(ModbotContext db, IModbotClock clock, CancellationToken ct)
    {
        var newest = await db.Events.AsNoTracking().OrderByDescending(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync(ct);

        var state = await db.DiscordSyncState.FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (state is null)
        {
            state = new DiscordSyncState { Id = 1 };
            db.DiscordSyncState.Add(state);
        }

        state.BansReadThrough = newest;
        state.BansReadAt = clock.UtcNow;
        state.BansProblem = null;
    }

    private static Task<DiscordServer?> ServerAsync(ModbotContext db, string? guildId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(guildId)
            ? Task.FromResult<DiscordServer?>(null)
            : db.DiscordServers.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);

    private static async Task<DiscordSyncSettingsView> ViewAsync(ModbotContext db, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);
        var server = await ServerAsync(db, settings.DiscordGuildId, ct);
        var state = await db.DiscordSyncState.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var snapshot = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot);
        var vrchatNames = (snapshot?.Roles ?? [])
            .Where(r => r.Name is { Length: > 0 })
            .ToDictionary(r => r.Id, r => r.Name!, StringComparer.Ordinal);

        var discordRoles = settings.DiscordGuildId is null
            ? []
            : await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == settings.DiscordGuildId)
                .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct);

        var pairs = await db.DiscordRolePairs.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        return new DiscordSyncSettingsView(
            settings.DiscordRoleSyncOn,
            settings.DiscordBanSyncToDiscord,
            settings.DiscordBanSyncToVRChat,
            settings.DiscordBanCopyAction,
            server?.BotCanBanMembers ?? false,
            server?.BotCanRemoveMembers ?? false,
            server?.BotCanManageRoles ?? false,
            state?.RolesRanAt,
            state?.RolesProblem,
            state?.BansReadAt,
            state?.BansProblem,
            pairs.Select(p => new RolePairView(
                    p.Id,
                    p.VRChatRoleId,
                    vrchatNames.TryGetValue(p.VRChatRoleId, out var vrchatName) ? vrchatName : p.VRChatRoleName,
                    p.DiscordRoleId,
                    discordRoles.TryGetValue(p.DiscordRoleId, out var role) ? role.Name : p.DiscordRoleName,
                    p.Decides,
                    p.Enabled,
                    role?.BotCanAssign ?? false,
                    p.Problem))
                .ToList(),
            (snapshot?.Roles ?? [])
                .Select(r => new GroupRoleView(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name!))
                .ToList());
    }

    private static SyncPreviewView View(SyncPreview preview) => new(
        preview.Total,
        preview.Changes
            .Select(c => new PlannedChangeView(c.What, c.Platform, c.VRChatUserId, c.DiscordUserId, c.Name, c.RoleName, c.Why))
            .ToList(),
        preview.Problem);
}
