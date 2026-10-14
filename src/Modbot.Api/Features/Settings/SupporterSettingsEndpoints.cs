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

/// <summary>Settings → Supporters, as stored. The verification token is never returned.</summary>
/// <param name="KofiOn">Modbot takes in Ko-fi's payment messages.</param>
/// <param name="TokenStored">Whether a verification token is saved.</param>
/// <param name="ThanksText">The direct message a new supporter gets, with {name} and {tier} filled in. Empty sends none.</param>
/// <param name="WebhookAddress">The address to give Ko-fi, built from the public address; null until that is set.</param>
/// <param name="MostThanksLength">The longest the message may be.</param>
public sealed record SupporterSettingsResponse(
    bool KofiOn,
    bool TokenStored,
    string ThanksText,
    string? WebhookAddress,
    int MostThanksLength);

/// <summary>A change to Settings → Supporters. A field left out is not changed.</summary>
/// <param name="VerificationToken">The token from Ko-fi's webhook page. Left out or empty keeps the saved one.</param>
public sealed record SupporterSettingsUpdate(bool? KofiOn, string? VerificationToken, string? ThanksText);

/// <summary>
/// Settings → Supporters: whether Ko-fi's payment messages are taken in, the verification token that
/// proves they came from Ko-fi, and the thank-you message. Needs Manage settings. The token is kept
/// encrypted and never returned; the change is recorded as a secret changed, never what to. Turning
/// Ko-fi on adds Manage Roles to the bot's invite link.
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
            .WithDescription("Whether Ko-fi is on, whether a verification token is saved (never the token), the thank-you message and the address to give Ko-fi.")
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

                var thanks = body.ThanksText?.Trim();

                if (thanks is { Length: > SupporterLimits.MostThanksLength })
                    return Results.BadRequest(new { error = $"The message is longer than {SupporterLimits.MostThanksLength} characters." });

                var settings = await db.GetSettingsAsync(ct);

                var onAfter = body.KofiOn ?? settings.KofiOn;
                var tokenChanged = token is not null && token != protector.Unprotect(settings.KofiVerificationTokenEncrypted);

                if (onAfter && token is null && string.IsNullOrEmpty(settings.KofiVerificationTokenEncrypted))
                    return Results.BadRequest(new { error = "Save the verification token first." });

                var thanksAfter = thanks is null ? settings.SupporterThanksText : (thanks.Length > 0 ? thanks : null);

                var change = new SettingsChange("supporters")
                    .Field("kofiOn", settings.KofiOn, onAfter)
                    .Secret("kofiVerificationToken", tokenChanged)
                    .Field("thanksText", settings.SupporterThanksText, thanksAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.KofiOn = onAfter;
                if (tokenChanged)
                    settings.KofiVerificationTokenEncrypted = protector.Protect(token!);
                settings.SupporterThanksText = thanksAfter;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetSupporterSettings")
            .WithSummary("Update supporter settings")
            .WithDescription(
                "Switch Ko-fi on or off, save the verification token from Ko-fi's webhook page (kept encrypted, never returned) "
                + "and set the thank-you message. Ko-fi cannot be switched on without a token. While Ko-fi is on the bot's "
                + "invite link asks for Manage Roles, so a bot added earlier has to be added again from the new link.")
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
            SupporterLimits.MostThanksLength);
}
