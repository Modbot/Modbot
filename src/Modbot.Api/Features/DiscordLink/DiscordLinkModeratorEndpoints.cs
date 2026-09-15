using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.DiscordLink;

/// <summary>A role Modbot gave a linked member, with its name when the bot has read it.</summary>
public sealed record LinkedRoleView(string Id, string? Name);

/// <summary>One VRChat person's active Discord link, as the person popup shows it.</summary>
/// <param name="Roles">The roles Modbot gave and believes the member still holds.</param>
/// <param name="RoleError">The last role change Discord refused, or null.</param>
public sealed record DiscordLinkView(
    Guid Id,
    string DiscordUserId,
    string DiscordUsername,
    string VRChatUserId,
    DateTimeOffset LinkedAt,
    string StartedFrom,
    IReadOnlyList<LinkedRoleView> Roles,
    bool NotInServer,
    string? RoleError);

public sealed record DiscordLinkLookup(DiscordLinkView? Link);

/// <summary>
/// A linked profile's Discord side for moderators (M5 §5.3), and ending a link on a member's behalf
/// (Discord account linking design §7, §10).
/// </summary>
/// <remarks>
/// Reading needs <see cref="ModbotPermissions.ViewProfile"/>, like the rest of the popup. Ending one
/// needs <see cref="ModbotPermissions.ManageDiscordLinks"/>: it takes away the roles Modbot gave.
/// </remarks>
public static class DiscordLinkModeratorEndpoints
{
    public static IEndpointRouteBuilder MapDiscordLinkModeration(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord-links").WithTags("Discord account link");

        group.MapGet("", async (
                [FromQuery] string? vrchatUserId,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(vrchatUserId))
                    return Results.BadRequest(new { error = "vrchatUserId is required." });

                var link = await db.ActiveAccountLinks()
                    .FirstOrDefaultAsync(l => l.VRChatUserId == vrchatUserId, ct);

                return Results.Ok(new DiscordLinkLookup(link is null ? null : await ViewAsync(db, link, ct)));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetDiscordLinkForPerson")
            .WithSummary("The Discord account linked to a VRChat user, if any")
            .Produces<DiscordLinkLookup>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/unlink", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] DiscordAccountLinks links,
                HttpContext http,
                CancellationToken ct) =>
            {
                var link = await db.DiscordAccountLinks.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (link is null)
                    return Results.NotFound(new { error = "That link does not exist." });

                await links.UnlinkAsync(link, Actor.Of(http), ct);
                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageDiscordLinks)
            .WithName("UnlinkDiscordLink")
            .WithSummary("End a member's link. The roles Modbot gave are taken away; history is kept.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<DiscordLinkView> ViewAsync(ModbotContext db, DiscordAccountLink link, CancellationToken ct)
    {
        var ids = new[] { link.LinkedRoleId, link.EighteenPlusRoleId }.OfType<string>().ToArray();

        var names = ids.Length == 0
            ? new Dictionary<string, string>()
            : await db.DiscordRoles.AsNoTracking()
                .Where(r => ids.Contains(r.RoleId))
                .ToDictionaryAsync(r => r.RoleId, r => r.Name, ct);

        // The stored member list answers once the bot has read it; before that, what the role job
        // last heard from Discord.
        var guildId = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.DiscordGuildId).FirstOrDefaultAsync(ct);
        var listed = guildId is not null
                     && await db.DiscordServers.AsNoTracking().AnyAsync(s => s.GuildId == guildId && s.MembersListedAt != null, ct);

        var notInServer = listed
            ? !await db.DiscordMembers.AsNoTracking().AnyAsync(m => m.GuildId == guildId && m.UserId == link.DiscordUserId && m.LeftAt == null, ct)
            : link.NotInServerAt is not null;

        return new DiscordLinkView(
            link.Id,
            link.DiscordUserId,
            link.DiscordUsername,
            link.VRChatUserId,
            link.LinkedAt,
            link.StartedFrom,
            [.. ids.Select(roleId => new LinkedRoleView(roleId, names.GetValueOrDefault(roleId)))],
            notInServer,
            link.RoleError);
    }
}
