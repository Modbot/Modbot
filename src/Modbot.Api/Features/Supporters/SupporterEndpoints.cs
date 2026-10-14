using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.DiscordSync;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Supporters;
using Modbot.Core.Time;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Supporters;

/// <summary>One level supporters pay at, with the roles it gives.</summary>
/// <param name="Provider">The site the tier is on, <c>kofi</c>.</param>
/// <param name="Supporters">How many supporters are paid up on it now.</param>
public sealed record SupporterTierView(
    Guid Id,
    string Provider,
    string Name,
    string? DiscordRoleId,
    string? VRChatRoleId,
    int Supporters);

/// <summary>One supporter as the page shows them.</summary>
/// <param name="DiscordUserId">Their Discord account, or null while they have not been matched.</param>
/// <param name="DiscordName">Their name in the server, when they are in it.</param>
/// <param name="State"><c>active</c> or <c>ended</c>.</param>
/// <param name="RolesEndAt">When the roles come off if no payment comes first.</param>
/// <param name="DiscordRoleGiven">Modbot has given the Discord role.</param>
/// <param name="VRChatRoleGiven">Modbot has given the VRChat group role.</param>
/// <param name="VRChatLinked">Their Discord account is linked to a VRChat account.</param>
/// <param name="Problem">The last refusal, as a sentence.</param>
/// <param name="Code">The site's code for their latest payment, which they can give staff to be matched.</param>
public sealed record SupporterView(
    Guid Id,
    string Provider,
    string Name,
    string? DiscordUserId,
    string? DiscordName,
    Guid? TierId,
    string? TierName,
    string State,
    DateTimeOffset LastPaidAt,
    DateTimeOffset PaidThrough,
    DateTimeOffset RolesEndAt,
    bool DiscordRoleGiven,
    bool VRChatRoleGiven,
    bool VRChatLinked,
    string? Problem,
    string? Code);

/// <param name="KofiOn">Ko-fi is switched on in Settings.</param>
/// <param name="BotCanManageRoles">The bot holds Manage Roles in the server.</param>
/// <param name="GroupRoles">The VRChat group's roles, as the last read of the group found them.</param>
public sealed record SupportersResponse(
    bool KofiOn,
    bool BotCanManageRoles,
    IReadOnlyList<SupporterTierView> Tiers,
    IReadOnlyList<SupporterView> Supporters,
    IReadOnlyList<GroupRoleView> GroupRoles,
    int MostTierNameLength);

/// <summary>A tier staff add by hand. Leave a role out to give none.</summary>
public sealed record SupporterTierBody(string Name, string? DiscordRoleId, string? VRChatRoleId);

/// <summary>The roles a tier gives. Empty or null gives none.</summary>
public sealed record SupporterTierRolesBody(string? DiscordRoleId, string? VRChatRoleId);

/// <summary>The Discord account a supporter is.</summary>
public sealed record SupporterLinkBody(string DiscordUserId);

/// <summary>
/// Supporters: who pays, at which tier, and which roles each tier gives. Needs Manage settings. Every
/// change is recorded as a settings change.
/// </summary>
/// <remarks>
/// The payments themselves arrive at <see cref="KofiWebhookEndpoint.Path"/>; the roles are given and
/// taken back by the role job in the bot (<c>SupporterRoles</c>), which this wakes.
/// </remarks>
public static class SupporterEndpoints
{
    public static IEndpointRouteBuilder MapSupporters(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/supporters")
            .WithTags("Supporters")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(await ViewAsync(db, ct)))
            .WithName("ListSupporters")
            .WithSummary("List supporters and tiers")
            .WithDescription(
                "The tiers with the roles each gives, and the supporters with the newest payment first (at most "
                + "500). A supporter with no `discordUserId` has not been matched to a Discord account yet.")
            .Produces<SupportersResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/tiers", async (
                HttpContext http,
                [FromBody] SupporterTierBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] SupporterSignal signal,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var name = body.Name?.Trim() ?? string.Empty;

                if (name.Length == 0 || name.Length > SupporterLimits.MostTierNameLength)
                    return Results.BadRequest(new { error = $"Give the tier the name Ko-fi uses, up to {SupporterLimits.MostTierNameLength} characters." });

                if (await db.SupporterTiers.CountAsync(ct) >= SupporterLimits.MostTiers)
                    return Results.BadRequest(new { error = $"There can be at most {SupporterLimits.MostTiers} tiers." });

                var key = SupporterRules.TierKeyOf(name);

                if (await db.SupporterTiers.AnyAsync(t => t.Provider == SupporterProviders.Kofi && t.Key == key, ct))
                    return Results.Conflict(new { error = "That tier is already there." });

                var roles = await CheckRolesAsync(db, body.DiscordRoleId, body.VRChatRoleId, ct);

                if (roles.Problem is not null)
                    return Results.BadRequest(new { error = roles.Problem });

                var tier = new SupporterTier
                {
                    Provider = SupporterProviders.Kofi,
                    Key = key,
                    Name = name,
                    DiscordRoleId = roles.DiscordRoleId,
                    VRChatRoleId = roles.VRChatRoleId,
                    CreatedAt = clock.UtcNow,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.SupporterTiers.Add(tier);
                await db.SaveChangesAsync(ct);
                await new SettingsChange("supporters").Items<string>("tiersAdded", [], [name]).RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                signal.Changed();
                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("AddSupporterTier")
            .WithSummary("Add a supporter tier")
            .WithDescription(
                "Add a tier by the name the site uses for it, with the Discord role and the VRChat group role it gives. "
                + "A tier is also added by itself the first time a payment names it, with no roles.")
            .Accepts<SupporterTierBody>("application/json")
            .Produces<SupportersResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/tiers/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromBody] SupporterTierRolesBody body,
                [FromServices] ModbotContext db,
                [FromServices] SupporterSignal signal,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var tier = await db.SupporterTiers.FirstOrDefaultAsync(t => t.Id == id, ct);

                if (tier is null)
                    return Results.NotFound(new { error = "No such tier." });

                var roles = await CheckRolesAsync(db, body.DiscordRoleId, body.VRChatRoleId, ct);

                if (roles.Problem is not null)
                    return Results.BadRequest(new { error = roles.Problem });

                var change = new SettingsChange("supporters")
                    .Field($"{tier.Name}.discordRoleId", tier.DiscordRoleId, roles.DiscordRoleId)
                    .Field($"{tier.Name}.vrchatRoleId", tier.VRChatRoleId, roles.VRChatRoleId);

                if (!change.IsEmpty)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    tier.DiscordRoleId = roles.DiscordRoleId;
                    tier.VRChatRoleId = roles.VRChatRoleId;
                    await db.SaveChangesAsync(ct);
                    await change.RecordAsync(http, ct);
                    await transaction.CommitAsync(ct);

                    signal.Changed();
                }

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("SetSupporterTierRoles")
            .WithSummary("Set the roles a tier gives")
            .WithDescription(
                "Change the Discord role and the VRChat group role a tier gives. Supporters on the tier get the new roles "
                + "within a minute, and the roles Modbot gave them before are taken away.")
            .Accepts<SupporterTierRolesBody>("application/json")
            .Produces<SupportersResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/tiers/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var tier = await db.SupporterTiers.FirstOrDefaultAsync(t => t.Id == id, ct);

                if (tier is null)
                    return Results.NotFound(new { error = "No such tier." });

                if (await db.Supporters.AnyAsync(s => s.TierId == id && s.State == SupporterStates.Active, ct))
                    return Results.Conflict(new { error = "Supporters are still on this tier." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.SupporterTiers.Remove(tier);
                await db.SaveChangesAsync(ct);
                await new SettingsChange("supporters").Items<string>("tiersRemoved", [], [tier.Name]).RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("DeleteSupporterTier")
            .WithSummary("Delete a supporter tier")
            .WithDescription("Refused (409) while anyone paid up is on the tier. Supporters whose payments have stopped lose the tier.")
            .Produces<SupportersResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/link", async (
                HttpContext http,
                Guid id,
                [FromBody] SupporterLinkBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] SupporterSignal signal,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var supporter = await db.Supporters.FirstOrDefaultAsync(s => s.Id == id, ct);

                if (supporter is null)
                    return Results.NotFound(new { error = "No such supporter." });

                var discordUserId = body.DiscordUserId?.Trim() ?? string.Empty;
                var guildId = await db.Settings.AsNoTracking().Select(s => s.DiscordGuildId).FirstOrDefaultAsync(ct);

                var member = discordUserId.Length == 0 || guildId is null
                    ? null
                    : await db.DiscordMembers.AsNoTracking()
                        .Where(m => m.GuildId == guildId && m.UserId == discordUserId && !m.IsBot)
                        .Select(m => new { m.DisplayName })
                        .FirstOrDefaultAsync(ct);

                if (member is null)
                    return Results.BadRequest(new { error = "That person is not a member of the Discord server." });

                if (await db.Supporters.AnyAsync(s => s.Id != id && s.Provider == supporter.Provider && s.DiscordUserId == discordUserId, ct))
                    return Results.Conflict(new { error = "Another supporter is already matched to that account." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var before = supporter.DiscordUserId;
                supporter.DiscordUserId = discordUserId;
                supporter.ThanksSentAt = null;
                supporter.Problem = null;
                supporter.TryAgainAt = null;
                supporter.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);
                await new SettingsChange("supporters")
                    .Field($"{supporter.Name}.discordUserId", before, discordUserId)
                    .RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                signal.Changed();
                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("LinkSupporter")
            .WithSummary("Match a supporter to a Discord account")
            .WithDescription(
                "For a supporter Ko-fi did not send a Discord account for: staff find them by the code from their payment "
                + "and pick their account. Their roles are given within a minute. The account must be a member of the server, "
                + "and not already matched to another supporter.")
            .Accepts<SupporterLinkBody>("application/json")
            .Produces<SupportersResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>The roles as saved, or what is wrong with them: both must exist where they are said to.</summary>
    private static async Task<(string? DiscordRoleId, string? VRChatRoleId, string? Problem)> CheckRolesAsync(
        ModbotContext db, string? discordRoleId, string? vrchatRoleId, CancellationToken ct)
    {
        var discord = string.IsNullOrWhiteSpace(discordRoleId) ? null : discordRoleId.Trim();
        var vrchat = string.IsNullOrWhiteSpace(vrchatRoleId) ? null : vrchatRoleId.Trim();

        var settings = await db.GetSettingsAsync(ct);

        if (discord is not null
            && !await db.DiscordRoles.AsNoTracking().AnyAsync(r => r.GuildId == settings.DiscordGuildId && r.RoleId == discord, ct))
        {
            return (null, null, "That Discord role is not in the server.");
        }

        // The group's roles are known once the group has been read; before that nothing can be checked.
        if (vrchat is not null
            && GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot) is { } snapshot
            && !snapshot.Roles.Any(r => string.Equals(r.Id, vrchat, StringComparison.Ordinal)))
        {
            return (null, null, "That role is not in the VRChat group.");
        }

        return (discord, vrchat, null);
    }

    private static async Task<SupportersResponse> ViewAsync(ModbotContext db, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);

        var botCanManageRoles = settings.DiscordGuildId is null
            ? false
            : await db.DiscordServers.AsNoTracking()
                .Where(s => s.GuildId == settings.DiscordGuildId)
                .Select(s => s.BotCanManageRoles)
                .FirstOrDefaultAsync(ct);

        var tiers = await db.SupporterTiers.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(ct);

        var paidUp = await db.Supporters.AsNoTracking()
            .Where(s => s.State == SupporterStates.Active && s.TierId != null)
            .GroupBy(s => s.TierId!.Value)
            .Select(g => new { TierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TierId, g => g.Count, ct);

        var supporters = await db.Supporters.AsNoTracking()
            .OrderByDescending(s => s.LastPaidAt)
            .Take(SupporterLimits.MostListed)
            .ToListAsync(ct);

        var ids = supporters.Select(s => s.Id).ToList();
        var discordIds = supporters.Where(s => s.DiscordUserId != null).Select(s => s.DiscordUserId!).ToList();

        var names = settings.DiscordGuildId is null
            ? []
            : await db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == settings.DiscordGuildId && discordIds.Contains(m.UserId))
                .ToDictionaryAsync(m => m.UserId, m => m.DisplayName, StringComparer.Ordinal, ct);

        var linked = (await db.ActiveAccountLinks()
                .Where(l => discordIds.Contains(l.DiscordUserId))
                .Select(l => l.DiscordUserId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        // The latest code for each: the one on the newest receipt.
        var codes = (await db.SupporterPayments.AsNoTracking()
                .Where(p => ids.Contains(p.SupporterId))
                .Select(p => new { p.SupporterId, p.Code, p.PaidAt })
                .ToListAsync(ct))
            .GroupBy(p => p.SupporterId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PaidAt).First().Code);

        var tierNames = tiers.ToDictionary(t => t.Id, t => t.Name);

        var groupRoles = (GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)?.Roles ?? [])
            .Select(r => new GroupRoleView(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name!))
            .ToList();

        return new SupportersResponse(
            settings.KofiOn,
            botCanManageRoles,
            [.. tiers.Select(t => new SupporterTierView(
                t.Id, t.Provider, t.Name, t.DiscordRoleId, t.VRChatRoleId, paidUp.GetValueOrDefault(t.Id)))],
            [.. supporters.Select(s => new SupporterView(
                s.Id,
                s.Provider,
                s.Name,
                s.DiscordUserId,
                s.DiscordUserId is { } d ? names.GetValueOrDefault(d) : null,
                s.TierId,
                s.TierId is { } t ? tierNames.GetValueOrDefault(t) : null,
                s.State,
                s.LastPaidAt,
                s.PaidThrough,
                SupporterRules.RolesEndAt(s.PaidThrough),
                s.GivenDiscordRoleId is not null,
                s.GivenVRChatRoleId is not null,
                s.DiscordUserId is { } u && linked.Contains(u),
                s.Problem,
                codes.GetValueOrDefault(s.Id)))],
            groupRoles,
            SupporterLimits.MostTierNameLength);
    }
}
