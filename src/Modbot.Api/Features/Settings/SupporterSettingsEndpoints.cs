using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Supporters;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Supporters;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Supporters, as stored. The verification token and the webhook secret are never returned.</summary>
/// <param name="KofiOn">Modbot takes in Ko-fi's payment messages.</param>
/// <param name="TokenStored">Whether a verification token is saved.</param>
/// <param name="ThanksText">The direct message a new supporter gets, with {name} and {tier} filled in. Empty sends none.</param>
/// <param name="WebhookAddress">The address to give Ko-fi, built from the public address; null until that is set.</param>
/// <param name="MostThanksLength">The longest the message may be.</param>
/// <param name="PatreonOn">Modbot takes in Patreon's membership messages.</param>
/// <param name="PatreonSecretStored">Whether a Patreon webhook secret is saved.</param>
/// <param name="PatreonWebhookAddress">The address to give Patreon, built from the public address; null until that is set.</param>
/// <param name="KofiRolesOffDays">Days after a Ko-fi supporter's paid month ends that the roles come off; -1 is never.</param>
/// <param name="PatreonRolesOffDays">Days after a Patreon supporter's paid month ends (or a declined charge) that the roles come off; -1 is never.</param>
/// <param name="MostRolesOffDays">The most days either setting may give.</param>
public sealed record SupporterSettingsResponse(
    bool KofiOn,
    bool TokenStored,
    string ThanksText,
    string? WebhookAddress,
    int MostThanksLength,
    bool PatreonOn,
    bool PatreonSecretStored,
    string? PatreonWebhookAddress,
    int KofiRolesOffDays,
    int PatreonRolesOffDays,
    int MostRolesOffDays);

/// <summary>A change to Settings → Supporters. A field left out is not changed.</summary>
/// <param name="VerificationToken">The token from Ko-fi's webhook page. Left out or empty keeps the saved one.</param>
/// <param name="PatreonWebhookSecret">The secret from Patreon's webhook page. Left out or empty keeps the saved one.</param>
/// <param name="KofiRolesOffDays">Days after a Ko-fi supporter's paid month ends that the roles come off, 0 to 365, or -1 for never.</param>
/// <param name="PatreonRolesOffDays">Days after a Patreon supporter's paid month ends (or a declined charge) that the roles come off, 0 to 365, or -1 for never.</param>
public sealed record SupporterSettingsUpdate(
    bool? KofiOn,
    string? VerificationToken,
    string? ThanksText,
    bool? PatreonOn = null,
    string? PatreonWebhookSecret = null,
    int? KofiRolesOffDays = null,
    int? PatreonRolesOffDays = null);

/// <summary>
/// Settings → Supporters: whether Ko-fi's payment messages and Patreon's membership messages are taken
/// in, the verification token and the webhook secret that prove they came from there, how many days
/// after the paid month ends each site's supporters lose the roles (or never), and the thank-you
/// message. Needs Manage settings. The token and the secret are kept encrypted and never returned; the
/// change is recorded as a secret changed, never what to. Turning Ko-fi or Patreon on adds Manage Roles
/// to the bot's invite link.
/// </summary>
public static class SupporterSettingsEndpoints
{
    public static IEndpointRouteBuilder MapSupporterSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/supporters")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetSupporterSettings")
            .WithSummary("Get supporter settings")
            .WithDescription(
                "Whether Ko-fi and Patreon are on, whether a verification token or a Patreon webhook secret is saved (never the "
                + "token or the secret), how many days after the paid month ends each site's supporters lose the roles (-1 is never), "
                + "the thank-you message and the addresses to give Ko-fi and Patreon.")
            .Produces<SupporterSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] SupporterSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var token = string.IsNullOrWhiteSpace(body.VerificationToken) ? null : body.VerificationToken.Trim();

                if (token is not null && (token.Length > SupporterLimits.MostTokenLength || token.Any(char.IsWhiteSpace)))
                    return Results.BadRequest(new { error = "This is not a verification token." });

                var secret = string.IsNullOrWhiteSpace(body.PatreonWebhookSecret) ? null : body.PatreonWebhookSecret.Trim();

                if (secret is not null && (secret.Length > SupporterLimits.MostTokenLength || secret.Any(char.IsWhiteSpace)))
                    return Results.BadRequest(new { error = "This is not a webhook secret." });

                var thanks = body.ThanksText?.Trim();

                if (thanks is { Length: > SupporterLimits.MostThanksLength })
                    return Results.BadRequest(new { error = $"The message is longer than {SupporterLimits.MostThanksLength} characters." });

                foreach (var days in new[] { body.KofiRolesOffDays, body.PatreonRolesOffDays })
                {
                    if (days is { } d && d != SupporterLimits.RolesNeverOff && (d < 0 || d > SupporterLimits.MostRolesOffDays))
                        return Results.BadRequest(new { error = $"Give a number of days from 0 to {SupporterLimits.MostRolesOffDays}, or never." });
                }

                var settings = await db.GetSettingsAsync(ct);

                var onAfter = body.KofiOn ?? settings.KofiOn;
                var tokenChanged = token is not null && token != protector.Unprotect(settings.KofiVerificationTokenEncrypted);

                if (onAfter && token is null && string.IsNullOrEmpty(settings.KofiVerificationTokenEncrypted))
                    return Results.BadRequest(new { error = "Save the verification token first." });

                var patreonOnAfter = body.PatreonOn ?? settings.PatreonOn;
                var secretChanged = secret is not null && secret != protector.Unprotect(settings.PatreonWebhookSecretEncrypted);

                if (patreonOnAfter && secret is null && string.IsNullOrEmpty(settings.PatreonWebhookSecretEncrypted))
                    return Results.BadRequest(new { error = "Save the webhook secret first." });

                var kofiDaysAfter = body.KofiRolesOffDays ?? settings.KofiRolesOffDays;
                var patreonDaysAfter = body.PatreonRolesOffDays ?? settings.PatreonRolesOffDays;
                var thanksAfter = thanks is null ? settings.SupporterThanksText : (thanks.Length > 0 ? thanks : null);

                var change = new SettingsChange("supporters")
                    .Field("kofiOn", settings.KofiOn, onAfter)
                    .Secret("kofiVerificationToken", tokenChanged)
                    .Field("patreonOn", settings.PatreonOn, patreonOnAfter)
                    .Secret("patreonWebhookSecret", secretChanged)
                    .Field("kofiRolesOffDays", settings.KofiRolesOffDays, kofiDaysAfter)
                    .Field("patreonRolesOffDays", settings.PatreonRolesOffDays, patreonDaysAfter)
                    .Field("thanksText", settings.SupporterThanksText, thanksAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.KofiOn = onAfter;
                if (tokenChanged)
                    settings.KofiVerificationTokenEncrypted = protector.Protect(token!);
                settings.PatreonOn = patreonOnAfter;
                if (secretChanged)
                    settings.PatreonWebhookSecretEncrypted = protector.Protect(secret!);
                settings.KofiRolesOffDays = kofiDaysAfter;
                settings.PatreonRolesOffDays = patreonDaysAfter;
                settings.SupporterThanksText = thanksAfter;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetSupporterSettings")
            .WithSummary("Update supporter settings")
            .WithDescription(
                "Switch Ko-fi or Patreon on or off, save the verification token from Ko-fi's webhook page or the webhook secret from "
                + "Patreon's (kept encrypted, never returned), set how many days after the paid month ends each site's supporters lose the "
                + "roles (0 to 365, or -1 for never) and set the thank-you message. Neither can be switched on without its "
                + "token or secret. While either is on the bot's invite link asks for Manage Roles, so a bot added earlier has to be "
                + "added again from the new link.")
            .Accepts<SupporterSettingsUpdate>("application/json")
            .Produces<SupporterSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static SupporterSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(
            settings.KofiOn,
            !string.IsNullOrEmpty(settings.KofiVerificationTokenEncrypted),
            settings.SupporterThanksText ?? string.Empty,
            string.IsNullOrWhiteSpace(settings.PublicAddress) ? null : settings.PublicAddress.Trim().TrimEnd('/') + KofiWebhookEndpoint.Path,
            SupporterLimits.MostThanksLength,
            settings.PatreonOn,
            !string.IsNullOrEmpty(settings.PatreonWebhookSecretEncrypted),
            string.IsNullOrWhiteSpace(settings.PublicAddress) ? null : settings.PublicAddress.Trim().TrimEnd('/') + PatreonWebhookEndpoint.Path,
            settings.KofiRolesOffDays,
            settings.PatreonRolesOffDays,
            SupporterLimits.MostRolesOffDays);
}
