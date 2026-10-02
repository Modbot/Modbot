using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Security;

namespace Modbot.Api.Features.Settings;

/// <summary>Account linking settings, as stored, with the two links built from them.</summary>
/// <param name="ClientSecretStored">Whether a client secret is stored. The secret itself is never returned.</param>
/// <param name="RedirectUrl">What to add under OAuth2 → Redirects in the Developer Portal. Null without a public address.</param>
/// <param name="InviteUrl">The bot invite link. Null without a client id.</param>
/// <param name="MeCommand">Members can use <c>/me</c>. Always false in a demo.</param>
/// <param name="Available">The client id, the secret and the public address are all set.</param>
public sealed record DiscordLinkingSettingsResponse(
    string? ClientId,
    bool ClientSecretStored,
    string? RedirectUrl,
    string? InviteUrl,
    bool PromptNewMembers,
    string? BackupChannelId,
    string? LinkedRoleId,
    string? EighteenPlusRoleId,
    bool MeCommand,
    bool Available);

/// <param name="ClientSecret">A new secret. Null or empty keeps the stored one, unless the client id changed.</param>
/// <param name="RemoveClientSecret">Forget the stored secret.</param>
/// <param name="BackupChannelId">Null or empty for none.</param>
/// <param name="LinkedRoleId">Null or empty for none.</param>
/// <param name="EighteenPlusRoleId">Null or empty for none.</param>
/// <param name="MeCommand">Members can use <c>/me</c>. Null keeps what is stored.</param>
public sealed record DiscordLinkingSettingsUpdate(
    string? ClientId,
    string? ClientSecret,
    bool RemoveClientSecret,
    bool PromptNewMembers,
    string? BackupChannelId,
    string? LinkedRoleId,
    string? EighteenPlusRoleId,
    bool? MeCommand = null);

/// <summary>
/// Settings → Discord → Account linking (Discord account linking design §4).
/// </summary>
/// <remarks>
/// <para>
/// The secret follows the AI key's rule: stored encrypted, never returned, and forgotten when the
/// client id changes without a new one, so it is only ever sent to Discord with the id it was saved
/// for.
/// </para>
/// <para>
/// A role the bot has read and cannot assign is refused here rather than found out on the first
/// link (M5 §3.3). A role id the bot has not read is accepted, so a deployment whose bot has never
/// connected can still be set up.
/// </para>
/// <para>
/// <c>/me</c> is switched here because it rests on the link: it answers from the member's link and
/// offers the link page to a member who has none. It cannot be switched on in a demo, which serves
/// everyone as an administrator and never runs the bot (Discord /me design §5).
/// </para>
/// </remarks>
public static class DiscordLinkingSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordLinkingSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-linking").WithTags("Settings");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] DemoMode? demo,
                CancellationToken ct) => Results.Ok(View(
                    await db.GetSettingsAsync(ct),
                    DemoAuthentication.MayServeEveryoneAsAdministrator(demo))))
            .WithName("GetDiscordLinkingSettings")
            .WithSummary("Get linking settings")
            .WithDescription("Account linking settings. The client secret is never returned.")
            .Produces<DiscordLinkingSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordLinkingSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] DemoMode? demo,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.MeCommand == true && DemoAuthentication.MayServeEveryoneAsAdministrator(demo))
                    return Results.BadRequest(new { error = "/me cannot be turned on in the demo." });

                var settings = await db.GetSettingsAsync(ct);

                var clientId = Blank(body.ClientId);
                var linkedRole = Blank(body.LinkedRoleId);
                var eighteenPlusRole = Blank(body.EighteenPlusRoleId);

                if (linkedRole is not null && linkedRole == eighteenPlusRole)
                    return Results.BadRequest(new { error = "The 18+ role must be a different role from the linked role." });

                foreach (var roleId in new[] { linkedRole, eighteenPlusRole }.OfType<string>())
                {
                    var role = await db.DiscordRoles.AsNoTracking()
                        .FirstOrDefaultAsync(r => r.RoleId == roleId && r.RemovedAt == null, ct);

                    if (role is { BotCanAssign: false })
                        return Results.BadRequest(new { error = $"The bot cannot assign {role.Name}." });

                    // A list giving the same role would take it from linked members who are not in
                    // the list (roles from lists design §4).
                    if (await db.DiscordListRoles.AsNoTracking().AnyAsync(p => p.DiscordRoleId == roleId, ct))
                        return Results.BadRequest(new { error = $"A list already gives {role?.Name ?? "that role"}." });
                }

                var newSecret = Blank(body.ClientSecret);
                var secretBefore = settings.DiscordOAuthClientSecretEncrypted;

                if (newSecret is not null)
                    settings.DiscordOAuthClientSecretEncrypted = protector.Protect(newSecret);
                else if (body.RemoveClientSecret || !string.Equals(settings.DiscordOAuthClientId, clientId, StringComparison.Ordinal))
                    settings.DiscordOAuthClientSecretEncrypted = null;

                var backupChannel = Blank(body.BackupChannelId);
                var meCommand = body.MeCommand ?? settings.DiscordMeCommand;

                // The client secret is a secret: the entry says it changed and never what it was.
                var change = new SettingsChange("discordLinking")
                    .Field("clientId", settings.DiscordOAuthClientId, clientId)
                    .Secret("clientSecret", !string.Equals(secretBefore, settings.DiscordOAuthClientSecretEncrypted, StringComparison.Ordinal))
                    .Field("promptNewMembers", settings.DiscordLinkPromptNewMembers, body.PromptNewMembers)
                    .Field("backupChannelId", settings.DiscordLinkBackupChannelId, backupChannel)
                    .Field("linkedRoleId", settings.DiscordLinkedRoleId, linkedRole)
                    .Field("eighteenPlusRoleId", settings.DiscordEighteenPlusRoleId, eighteenPlusRole)
                    .Field("meCommand", settings.DiscordMeCommand, meCommand);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordOAuthClientId = clientId;
                settings.DiscordLinkPromptNewMembers = body.PromptNewMembers;
                settings.DiscordLinkBackupChannelId = backupChannel;
                settings.DiscordLinkedRoleId = linkedRole;
                settings.DiscordEighteenPlusRoleId = eighteenPlusRole;
                settings.DiscordMeCommand = meCommand;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, DemoAuthentication.MayServeEveryoneAsAdministrator(demo)));
            })
            .WithName("SetDiscordLinkingSettings")
            .WithSummary("Update linking settings")
            .WithDescription("Save the account linking settings.")
            .Produces<DiscordLinkingSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    /// <param name="demo">This is a demo, where <c>/me</c> is always off whatever is stored.</param>
    private static DiscordLinkingSettingsResponse View(Core.Data.Entities.Settings settings, bool demo) => new(
        settings.DiscordOAuthClientId,
        settings.DiscordOAuthClientSecretEncrypted is not null,
        DiscordInvite.RedirectUrlFor(settings.PublicAddress),
        DiscordInvite.LinkFor(settings),
        settings.DiscordLinkPromptNewMembers,
        settings.DiscordLinkBackupChannelId,
        settings.DiscordLinkedRoleId,
        settings.DiscordEighteenPlusRoleId,
        settings.DiscordMeCommand && !demo,
        !string.IsNullOrWhiteSpace(settings.DiscordOAuthClientId)
            && settings.DiscordOAuthClientSecretEncrypted is not null
            && DiscordInvite.RedirectUrlFor(settings.PublicAddress) is not null);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
