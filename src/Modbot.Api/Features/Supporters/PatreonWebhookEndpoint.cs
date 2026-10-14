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
/// Where Patreon sends a message when a membership is made, changed or deleted, and when a charge
/// goes through or fails. Patreon calls it; this step makes no call to Patreon.
/// </summary>
/// <remarks>
/// <para>
/// Unauthenticated, because Patreon has no Modbot account: the signature is the credential. Patreon
/// signs the body with the webhook secret (a hex HMAC-MD5 in <c>X-Patreon-Signature</c>). A message
/// with no signature or the wrong one is refused, and nothing is read from it. Until Patreon is
/// switched on and a secret is saved the address answers 404, as if it were not there.
/// </para>
/// <para>
/// A message that is signed but is not about a patron who counts (a free follower, a member with no
/// tier) is answered 200 and ignored, so Patreon does not send it again.
/// </para>
/// </remarks>
public static class PatreonWebhookEndpoint
{
    public const string Path = "/api/supporters/patreon";

    /// <summary>The most bytes of a message read. A membership message with its tiers is a few thousand.</summary>
    private const int MostBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapPatreonWebhook(this IEndpointRouteBuilder app)
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
                    .Select(s => new { s.PatreonOn, s.PatreonWebhookSecretEncrypted })
                    .FirstOrDefaultAsync(ct);

                var secret = protector.Unprotect(settings?.PatreonWebhookSecretEncrypted);

                if (settings is not { PatreonOn: true } || string.IsNullOrEmpty(secret))
                    return Results.NotFound();

                if (http.Request.ContentLength is > MostBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                // The cap holds for every body, chunked or not: the server stops reading at the limit.
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = MostBytes;

                byte[] body;

                try
                {
                    using var buffer = new MemoryStream();
                    var chunk = new byte[16 * 1024];
                    int read;

                    while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
                    {
                        if (buffer.Length + read > MostBytes)
                            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                        buffer.Write(chunk, 0, read);
                    }

                    body = buffer.ToArray();
                }
                catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                // The signature is checked on the bytes as they arrived, before any of them is read as JSON.
                if (!PatreonRules.IsSignedBy(body, http.Request.Headers[PatreonRules.SignatureHeader].ToString(), secret))
                    return Results.Unauthorized();

                if (PatreonMessage.Parse(body) is not { } message)
                    return Results.BadRequest(new { error = "This is not a Patreon message." });

                var trigger = http.Request.Headers[PatreonRules.EventHeader].ToString();
                var outcome = await intake.TakePatreonAsync(message, string.IsNullOrWhiteSpace(trigger) ? null : trigger, clock.UtcNow, ct);

                if (outcome.Result == SupporterIntake.Taken)
                    signal.Changed();

                return Results.Ok();
            })
            .AllowAnonymous()
            .WithTags("Supporters")
            .WithName("TakePatreonMembership")
            .WithSummary("Take in a Patreon membership message")
            .WithDescription(
                "Patreon calls this when a membership is made, changed or deleted, and when a charge goes through or fails. "
                + "The body is signed with the webhook secret from Patreon (hex HMAC-MD5 in X-Patreon-Signature); a message with "
                + "no signature or the wrong one is refused (401) and nothing is read. An active patron with a tier gets that tier's "
                + "roles; a patron whose charge was declined keeps them until the Patreon setting's number of days after the charge; "
                + "a pledge that ended keeps them until the paid month is up and that number of days more (or for good, when the "
                + "setting is never). Anything else is answered 200 and ignored. Answers 404 while Patreon is off or no secret is saved.")
            .Accepts<object>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        return app;
    }
}
