using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;
using Modbot.Core.Email;

namespace Modbot.Api.Features.Settings;

public sealed record TestEmailRequest(string To);

/// <param name="Sent">True when the relay accepted it. Not proof it arrived; check the inbox.</param>
public sealed record TestEmailResponse(bool Sent, string? Error);

/// <summary>
/// Settings → Integrations: prove the SMTP settings work before a locked-out moderator finds out
/// they do not (accounts and access design §4.2).
/// </summary>
/// <remarks>
/// Answers 200 with a verdict rather than a status code carrying a mood, the same way the
/// evidence and connection checks do: a relay that refused the message has been successfully
/// diagnosed, and the relay's own sentence is the useful part.
/// </remarks>
public static class EmailSettingsEndpoints
{
    public static IEndpointRouteBuilder MapEmailSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/settings/email/test", async (
                [FromBody] TestEmailRequest body,
                [FromServices] IEmailSender email,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var to = UserEndpoints.Clean(body.To);
                if (to is null || !UserEndpoints.LooksLikeEmail(to))
                    return Results.BadRequest(new { error = "Enter the address the test message should go to." });

                var outcome = await email.SendAsync(
                    new EmailMessage(
                        to,
                        "Modbot test message",
                        "This is a test message from Modbot. If you are reading it, email is set up correctly."),
                    ct);

                return Results.Ok(new TestEmailResponse(outcome.Sent, outcome.Error));
            })
            .WithTags("Settings")
            .WithName("SendTestEmail")
            .WithSummary("Send a test message with the saved SMTP settings")
            .Produces<TestEmailResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }
}
