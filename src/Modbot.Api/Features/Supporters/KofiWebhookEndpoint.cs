using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Supporters;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Supporters;

/// <summary>
/// Where Ko-fi sends a message when somebody pays. Ko-fi calls it; Modbot makes no call to Ko-fi.
/// </summary>
/// <remarks>
/// <para>
/// Unauthenticated, because Ko-fi has no Modbot account: the verification token in the message is
/// the credential. A message whose token does not match the one saved in Settings is refused, and
/// nothing is read from it. Until Ko-fi is switched on and a token is saved the address answers
/// 404, as if it were not there.
/// </para>
/// <para>
/// Ko-fi puts the message in a form field called <c>data</c>; a plain JSON body is read too. A
/// payment that is not a monthly one (a gift, a shop order) is answered 200 and ignored, so Ko-fi
/// does not send it again.
/// </para>
/// </remarks>
public static class KofiWebhookEndpoint
{
    public const string Path = "/api/supporters/kofi";

    /// <summary>The most bytes of a message read. A payment message is a few hundred.</summary>
    private const long MostBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapKofiWebhook(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(Path, async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] SupporterIntake intake,
                [FromServices] SupporterSignal signal,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => new { s.KofiOn, s.KofiVerificationTokenEncrypted })
                    .FirstOrDefaultAsync(ct);

                var stored = protector.Unprotect(settings?.KofiVerificationTokenEncrypted);

                if (settings is not { KofiOn: true } || string.IsNullOrEmpty(stored))
                    return Results.NotFound();

                if (http.Request.ContentLength is > MostBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                // The cap holds for every body, chunked or not: the server stops reading at the limit.
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = MostBytes;

                string? json;

                try
                {
                    if (http.Request.HasFormContentType)
                    {
                        var form = await http.Request.ReadFormAsync(ct);
                        json = form["data"].ToString();
                    }
                    else
                    {
                        using var reader = new StreamReader(http.Request.Body);
                        var buffer = new char[MostBytes];
                        var read = await reader.ReadBlockAsync(buffer, ct);
                        json = new string(buffer, 0, read);
                    }
                }
                catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                if (KofiPayment.Parse(json) is not { } payment)
                    return Results.BadRequest(new { error = "This is not a Ko-fi message." });

                if (!SupporterRules.SameSecret(payment.VerificationToken, stored))
                    return Results.Unauthorized();

                var outcome = await intake.TakeKofiAsync(payment, clock.UtcNow, ct);

                if (outcome.Result == SupporterIntake.Taken)
                    signal.Changed();

                return Results.Ok();
            })
            .AllowAnonymous()
            .WithTags("Supporters")
            .WithName("TakeKofiPayment")
            .WithSummary("Take in a Ko-fi payment")
            .WithDescription(
                "Ko-fi calls this when somebody pays. The message carries the verification token from Ko-fi's "
                + "webhook page; one that does not match the token saved in Settings is refused (401). A monthly "
                + "payment with a tier name counts; anything else is answered 200 and ignored. The same payment "
                + "twice counts once. Answers 404 while Ko-fi is off or no token is saved.")
            .Accepts<object>("application/x-www-form-urlencoded", "application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        return app;
    }
}
