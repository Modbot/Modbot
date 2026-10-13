using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.DiscordLists;

/// <summary>What the bot may do in one channel, by Discord's own permission names.</summary>
public sealed record DiscordChannelPermissionsView(
    bool ViewChannel,
    bool ReadMessageHistory,
    bool SendMessages,
    bool EmbedLinks,
    bool AttachFiles,
    bool ManageMessages);

/// <param name="Type">text, announcement, forum, media, voice, stage or category.</param>
/// <param name="CategoryId">The category the channel sits under, or null.</param>
/// <param name="Removed">Deleted in Discord. Kept so a setting that still names it can show which channel it was.</param>
public sealed record DiscordChannelView(
    string Id,
    string Name,
    string Type,
    string? CategoryId,
    int Position,
    bool Nsfw,
    bool Removed,
    DiscordChannelPermissionsView BotPermissions);

/// <param name="GuildId">The server in settings, or null when none is set.</param>
/// <param name="ServerName">Null until the bot has read the server once.</param>
/// <param name="RefreshedAt">When every channel and role was last read in one go. Null until the first time.</param>
/// <param name="UpdatedAt">When anything in the lists last changed.</param>
public sealed record DiscordChannelsResponse(
    string? GuildId,
    string? ServerName,
    DateTimeOffset? RefreshedAt,
    DateTimeOffset? UpdatedAt,
    bool BotCanViewAuditLog,
    bool BotCanManageRoles,
    IReadOnlyList<DiscordChannelView> Channels);

/// <param name="Color">0xRRGGBB, zero for a role with no colour.</param>
/// <param name="Managed">Owned by a bot or an integration; nobody can hand it out.</param>
/// <param name="BotCanAssign">The bot holds Manage Roles, the role is below the bot's highest, and it is neither managed nor @everyone.</param>
/// <param name="BotCanMention">
/// A mention of the role by the bot pings: the role is open to mentions, or the bot holds Mention
/// @everyone, @here and All Roles. Never @everyone itself.
/// </param>
/// <param name="Dangerous">
/// The role holds a permission the permission watch tells staff about (<see cref="DiscordDangerousPermissions"/>),
/// so a role menu will not give it.
/// </param>
public sealed record DiscordRoleView(
    string Id,
    string Name,
    int Color,
    int Position,
    bool Managed,
    bool Everyone,
    bool BotCanAssign,
    bool Removed,
    bool BotCanMention = false,
    bool Dangerous = false);

public sealed record DiscordRolesResponse(
    string? GuildId,
    string? ServerName,
    DateTimeOffset? RefreshedAt,
    DateTimeOffset? UpdatedAt,
    bool BotCanManageRoles,
    IReadOnlyList<DiscordRoleView> Roles);

/// <summary>
/// The Discord server's channels and roles as the bot last saw them, for settings to pick from.
/// </summary>
/// <remarks>
/// Read from Modbot's own tables, never from the gateway, so they answer while the bot is offline.
/// Gated on <see cref="ModbotPermissions.ManageSettings"/>, the permission that saves the Discord
/// settings these lists are picked for. The event form picks a channel and a role too, so somebody
/// who can see the calendar and holds Manage calendar or Manage templates may read them as well.
/// </remarks>
public static class DiscordListEndpoints
{
    public static IEndpointRouteBuilder MapDiscordLists(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The permission is checked in the handlers: Change settings, or See calendar with Manage
        // calendar or Manage templates. RequiresFlag asks for every flag, never for either of two.
        var group = app.MapGroup("/api/discord")
            .WithTags("Discord")
            .RequireAuthorization();

        group.MapGet("/channels", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (RefusedWithoutListRights(http) is { } refused)
                    return refused;

                var guildId = await GuildIdAsync(db, ct);
                var server = await ServerAsync(db, guildId, ct);

                var channels = guildId is null
                    ? []
                    : await db.DiscordChannels.AsNoTracking()
                        .Where(c => c.GuildId == guildId)
                        .OrderBy(c => c.Position)
                        .ThenBy(c => c.ChannelId)
                        .Select(c => new DiscordChannelView(
                            c.ChannelId,
                            c.Name,
                            c.Type,
                            c.CategoryId,
                            c.Position,
                            c.Nsfw,
                            c.RemovedAt != null,
                            new DiscordChannelPermissionsView(
                                c.BotCanView,
                                c.BotCanReadHistory,
                                c.BotCanSend,
                                c.BotCanEmbedLinks,
                                c.BotCanAttachFiles,
                                c.BotCanManageMessages)))
                        .ToListAsync(ct);

                return Results.Ok(new DiscordChannelsResponse(
                    guildId,
                    server?.Name,
                    server?.RefreshedAt,
                    server?.UpdatedAt,
                    server?.BotCanViewAuditLog ?? false,
                    server?.BotCanManageRoles ?? false,
                    channels));
            })
            .WithName("ListDiscordChannels")
            .WithSummary("List Discord channels")
            .WithDescription(
                "The Discord server's channels and what the bot may do in each. Needs Change settings, "
                + "or See calendar with Manage calendar or Manage templates, because the event form picks a channel.")
            .Produces<DiscordChannelsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/roles", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (RefusedWithoutListRights(http) is { } refused)
                    return refused;

                var guildId = await GuildIdAsync(db, ct);
                var server = await ServerAsync(db, guildId, ct);
                var mentionEveryone = server?.BotCanMentionEveryone ?? false;

                var roles = guildId is null
                    ? []
                    : await db.DiscordRoles.AsNoTracking()
                        .Where(r => r.GuildId == guildId)
                        .OrderByDescending(r => r.Position)
                        .ThenBy(r => r.RoleId)
                        .Select(r => new DiscordRoleView(
                            r.RoleId,
                            r.Name,
                            r.Color,
                            r.Position,
                            r.Managed,
                            r.Everyone,
                            r.BotCanAssign,
                            r.RemovedAt != null,
                            !r.Everyone && r.RoleId != r.GuildId && (r.Mentionable || mentionEveryone),
                            r.Permissions != null && (r.Permissions.Value & DiscordDangerousPermissions.Mask) != 0))
                        .ToListAsync(ct);

                return Results.Ok(new DiscordRolesResponse(
                    guildId,
                    server?.Name,
                    server?.RefreshedAt,
                    server?.UpdatedAt,
                    server?.BotCanManageRoles ?? false,
                    roles));
            })
            .WithName("ListDiscordRoles")
            .WithSummary("List Discord roles")
            .WithDescription(
                "The Discord server's roles and whether the bot could hand each out. Needs Change settings, "
                + "or See calendar with Manage calendar or Manage templates, because the event form picks a role to mention.")
            .Produces<DiscordRolesResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>
    /// The refusal for somebody who may neither change settings nor use the event form (See calendar with
    /// Manage calendar or Manage templates), or null when the caller may read the lists.
    /// </summary>
    private static IResult? RefusedWithoutListRights(HttpContext http)
    {
        var held = ModbotAuth.PermissionsOf(http.User);

        var mayRead = ModbotAuth.Allows(held, ModbotPermissions.ManageSettings)
            || (ModbotAuth.Allows(held, ModbotPermissions.ViewCalendar) && Calendar.CalendarEndpoints.HoldsCalendarOrTemplates(held));

        return mayRead ? null : Results.Json(
            new { error = "This needs Change settings, or See calendar with Manage calendar or Manage templates." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static async Task<string?> GuildIdAsync(ModbotContext db, CancellationToken ct)
    {
        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(guildId) ? null : guildId.Trim();
    }

    private static Task<DiscordServer?> ServerAsync(ModbotContext db, string? guildId, CancellationToken ct)
        => guildId is null
            ? Task.FromResult<DiscordServer?>(null)
            : db.DiscordServers.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);
}
