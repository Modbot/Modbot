using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Users;
using Modbot.VRChat.Sync;

// The namespace of this file is also called Settings, so the row it saves needs a name of its own.
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Features.Settings;

/// <summary>A role a rule can name.</summary>
public sealed record AutoInviteRoleView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

/// <param name="Enabled">Whether Modbot invites people on its own.</param>
/// <param name="MinutesInInstance">How long somebody must have been in the instance. Never under five.</param>
/// <param name="MinimumMinutesInInstance">The smallest number the screen may offer.</param>
/// <param name="InviteAgainAfterDays">How long before the same person may be invited again.</param>
/// <param name="Rules">The rule tree, in the shape the giveaway rule builder reads and writes.</param>
/// <param name="RuleKinds">The rules the builder offers, in the order it lists them.</param>
/// <param name="TrustRanks">The trust ranks a rule may ask for, lowest first.</param>
/// <param name="GroupRoles">The group's own roles, for the rule that names one.</param>
/// <param name="DiscordRoles">The server's roles, for the rule that names one.</param>
/// <param name="ModerationFactRetentionDays">How long moderation facts are kept. 0 is forever.</param>
/// <param name="PresenceFactRetentionDays">How long presence facts are kept. 0 is forever.</param>
/// <param name="InvitesSent">How many invites have gone out.</param>
/// <param name="LastInviteAt">When the last one went out, or null.</param>
public sealed record AutoInviteView(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("minutesInInstance")] int MinutesInInstance,
    [property: JsonPropertyName("minimumMinutesInInstance")] int MinimumMinutesInInstance,
    [property: JsonPropertyName("inviteAgainAfterDays")] int InviteAgainAfterDays,
    [property: JsonPropertyName("rules")] JsonObject Rules,
    [property: JsonPropertyName("ruleKinds")] IReadOnlyList<string> RuleKinds,
    [property: JsonPropertyName("trustRanks")] IReadOnlyList<string> TrustRanks,
    [property: JsonPropertyName("groupRoles")] IReadOnlyList<AutoInviteRoleView> GroupRoles,
    [property: JsonPropertyName("discordRoles")] IReadOnlyList<AutoInviteRoleView> DiscordRoles,
    [property: JsonPropertyName("moderationFactRetentionDays")] int ModerationFactRetentionDays,
    [property: JsonPropertyName("presenceFactRetentionDays")] int PresenceFactRetentionDays,
    [property: JsonPropertyName("invitesSent")] int InvitesSent,
    [property: JsonPropertyName("lastInviteAt")] DateTimeOffset? LastInviteAt);

public sealed record SetAutoInvitesRequest(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("minutesInInstance")] int MinutesInInstance,
    [property: JsonPropertyName("inviteAgainAfterDays")] int InviteAgainAfterDays,
    [property: JsonPropertyName("rules")] JsonNode? Rules);

/// <summary>
/// Settings → Auto-invites: whether Modbot invites people to the group on its own, and who
/// qualifies (auto-invites design §9).
/// </summary>
/// <remarks>
/// <para>
/// Behind <see cref="ModbotPermissions.ManageAutoInvites"/> rather than
/// <see cref="ModbotPermissions.ManageSettings"/>: everything else under settings changes what
/// Modbot does to its own data, and this decides who ends up inside the group.
/// </para>
/// <para>
/// The roles and the rule kinds ride on the view so the rule builder works on this tab without
/// the caller also holding <c>RunGiveaways</c>. They are the same lists, read from the same two
/// places, as the giveaway builder serves.
/// </para>
/// </remarks>
public static class AutoInviteSettingsEndpoints
{
    /// <summary>The longest an "invite again after" may be set to. Ten years is never, in practice.</summary>
    public const int MostAgainAfterDays = 3650;

    public static IEndpointRouteBuilder MapAutoInviteSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/auto-invites")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageAutoInvites);

        group.MapGet("/", async ([FromServices] ModbotContext db, CancellationToken ct) =>
                Results.Ok(await ViewAsync(db, ct)))
            .WithName("GetAutoInvites")
            .WithSummary("Get auto-invites")
            .WithDescription("Whether Modbot invites people to the group on its own, and who qualifies.")
            .Produces<AutoInviteView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetAutoInvitesRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // The five minutes are refused rather than quietly raised. A screen that accepted
                // "2" and stored "5" would be lying about what it saved (design §4.1).
                if (body.MinutesInInstance < SettingsRow.MinimumAutoInviteMinutes)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Somebody has to be in the instance for at least "
                            + $"{SettingsRow.MinimumAutoInviteMinutes} minutes.",
                    });
                }

                if (body.InviteAgainAfterDays is < 1 or > MostAgainAfterDays)
                    return Results.BadRequest(new { error = $"Invite again after must be between 1 and {MostAgainAfterDays} days." });

                var rule = GiveawayRules.Read(body.Rules, out var problem);

                if (problem is not null || rule is null)
                    return Results.BadRequest(new { error = problem ?? "Those rules could not be read." });

                var settings = await db.GetSettingsAsync(ct);

                var before = new JsonObject
                {
                    ["enabled"] = settings.GroupAutoInviteEnabled,
                    ["minutesInInstance"] = settings.GroupAutoInviteMinutesInInstance,
                    ["inviteAgainAfterDays"] = settings.GroupAutoInviteAgainAfterDays,
                    ["rules"] = GiveawayRules.Describe(GiveawayRules.ReadStored(settings.GroupAutoInviteRules)),
                };

                settings.GroupAutoInviteEnabled = body.Enabled;
                settings.GroupAutoInviteMinutesInInstance = body.MinutesInInstance;
                settings.GroupAutoInviteAgainAfterDays = body.InviteAgainAfterDays;
                settings.GroupAutoInviteRules = GiveawayRules.Store(rule);

                var after = new JsonObject
                {
                    ["enabled"] = body.Enabled,
                    ["minutesInInstance"] = body.MinutesInInstance,
                    ["inviteAgainAfterDays"] = body.InviteAgainAfterDays,
                    ["rules"] = GiveawayRules.Describe(rule),
                };

                if (!db.ChangeTracker.HasChanges())
                    return Results.Ok(await ViewAsync(db, ct));

                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "autoInvites",
                        ["before"] = before,
                        ["after"] = after,
                    },
                    ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("SetAutoInvites")
            .WithSummary("Set auto-invites")
            .WithDescription("Turn auto-invites on or off, and set the rules, the minutes and how long before somebody may be invited again.")
            .Produces<AutoInviteView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<AutoInviteView> ViewAsync(ModbotContext db, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);
        var snapshot = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot);
        var guildId = settings.DiscordGuildId;

        var groupRoles = (snapshot?.Roles ?? [])
            .Select(r => new AutoInviteRoleView(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name!))
            .ToList();

        var discordRoles = guildId is null
            ? []
            : await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == guildId && r.RemovedAt == null && !r.Everyone)
                .OrderByDescending(r => r.Position)
                .Select(r => new AutoInviteRoleView(r.RoleId, r.Name))
                .ToListAsync(ct);

        var sent = await db.GroupAutoInvites.AsNoTracking().CountAsync(i => i.Worked == true, ct);

        var last = await db.GroupAutoInvites.AsNoTracking()
            .OrderByDescending(i => i.InvitedAt)
            .Select(i => (DateTimeOffset?)i.InvitedAt)
            .FirstOrDefaultAsync(ct);

        return new AutoInviteView(
            settings.GroupAutoInviteEnabled,
            Math.Max(SettingsRow.MinimumAutoInviteMinutes, settings.GroupAutoInviteMinutesInInstance),
            SettingsRow.MinimumAutoInviteMinutes,
            settings.GroupAutoInviteAgainAfterDays,
            GiveawayRules.Write(GiveawayRules.ReadStored(settings.GroupAutoInviteRules)),
            GiveawayRuleKinds.Asking,
            [.. TrustRanks.LadderRanks.Select(r => r.ToString())],
            groupRoles,
            discordRoles,
            settings.ModerationFactRetentionDays,
            settings.PresenceFactRetentionDays,
            sent,
            last);
    }
}
