using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Email;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

public sealed record TestEmailRequest(string To);

/// <param name="Sent">True when the relay accepted it. Not proof it arrived; check the inbox.</param>
/// <param name="Queued">Held under the daily email limit rather than sent.</param>
/// <param name="SendsAt">When a queued message should go out. Null when the limit leaves other email no room.</param>
public sealed record TestEmailResponse(bool Sent, string? Error, bool Queued = false, DateTimeOffset? SendsAt = null);

/// <summary>One email on the settings page. Never the body.</summary>
/// <param name="Kind"><c>account</c> or <c>other</c>.</param>
/// <param name="State"><c>queued</c>, <c>sending</c>, <c>failed</c> or <c>expired</c>.</param>
/// <param name="Error">The relay's last refusal, for a message that has one.</param>
public sealed record EmailQueueRow(
    Guid Id,
    string To,
    string Kind,
    DateTimeOffset QueuedAt,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    string? Error);

/// <param name="LimitPer24Hours">The most emails sent in any 24 hours.</param>
/// <param name="MinimumLimit">The lowest limit that can be saved.</param>
/// <param name="SentInLast24Hours">Sends in the rolling 24 hours.</param>
/// <param name="Queued">Messages waiting.</param>
/// <param name="Failed">Messages given up on in the last few days.</param>
/// <param name="NextSendAt">When the next queued message should go out.</param>
/// <param name="Emails">Queued, failed and expired messages, account email first, then oldest first.</param>
public sealed record EmailSettingsView(
    int LimitPer24Hours,
    int MinimumLimit,
    int SentInLast24Hours,
    int Queued,
    int Failed,
    DateTimeOffset? NextSendAt,
    IReadOnlyList<EmailQueueRow> Emails);

public sealed record SetEmailLimitRequest(int LimitPer24Hours);

/// <summary>
/// Settings → Integrations → Email: prove the SMTP settings work, and set and watch the daily
/// email limit (accounts and access design §4.2, §4.4).
/// </summary>
/// <remarks>
/// The test answers 200 with a verdict rather than a status code carrying a mood, the same way the
/// evidence and connection checks do: a relay that refused the message has been successfully
/// diagnosed, and the relay's own sentence is the useful part. The test message is other email,
/// so it can be queued like any other; it never uses the room kept for account email.
/// </remarks>
public static class EmailSettingsEndpoints
{
    /// <summary>How many queued, failed and expired messages the settings page lists.</summary>
    public const int RowsShown = 50;

    public static IEndpointRouteBuilder MapEmailSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/email")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/test", async (
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
                        "This is a test message from Modbot. If you are reading it, email is set up correctly.",
                        EmailKind.Other),
                    ct);

                return Results.Ok(new TestEmailResponse(outcome.Sent, outcome.Error, outcome.Queued, outcome.SendsAt));
            })
            .WithName("SendTestEmail")
            .WithSummary("Send a test message with the saved SMTP settings")
            .WithDescription("Counts against the daily email limit as other email, and is queued when the limit is used up.")
            .Produces<TestEmailResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) => Results.Ok(await ViewAsync(db, clock.UtcNow, ct)))
            .WithName("GetEmailSettings")
            .WithSummary("The daily email limit, how much of it is used, and the email queue")
            .Produces<EmailSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/limit", async (
                [FromBody] SetEmailLimitRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.LimitPer24Hours < EmailLimit.Minimum)
                    return Results.BadRequest(new { error = $"The email limit must be at least {EmailLimit.Minimum}." });

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.EmailLimitPer24Hours;

                if (before != body.LimitPer24Hours)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    settings.EmailLimitPer24Hours = body.LimitPer24Hours;
                    await db.SaveChangesAsync(ct);

                    await facts.RecordAsync(
                        FactType.SettingsChanged,
                        "settings",
                        Actor.Of(http),
                        new JsonObject
                        {
                            ["setting"] = "emailLimitPer24Hours",
                            ["before"] = before,
                            ["after"] = body.LimitPer24Hours,
                        },
                        ct);

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(await ViewAsync(db, clock.UtcNow, ct));
            })
            .WithName("SetEmailLimit")
            .WithSummary("Set the most emails sent in any 24 hours")
            .WithDescription(
                $"At least {EmailLimit.Minimum}. {EmailLimit.KeptForAccountEmails} of the limit are always kept for "
                + "account email -- reset links, invite links, sign-in and security notices -- and other email may "
                + "use only the rest. Email over its share is queued and sent when the 24 hours have room again.")
            .Produces<EmailSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<EmailSettingsView> ViewAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        var summary = await EmailQueueStatus.ReadAsync(db, now, ct);

        var rows = await db.EmailQueue.AsNoTracking()
            .Where(e => e.State != EmailStates.Sent)
            .OrderBy(e => e.State == EmailStates.Queued || e.State == EmailStates.Sending ? 0 : 1)
            .ThenBy(e => e.Kind == EmailKinds.Account ? 0 : 1)
            .ThenBy(e => e.QueuedAt)
            .Take(RowsShown)
            .Select(e => new EmailQueueRow(
                e.Id, e.ToAddress, e.Kind, e.QueuedAt, e.State, e.Attempts, e.NextAttemptAt, e.LastError))
            .ToListAsync(ct);

        return new EmailSettingsView(
            summary.Limit,
            EmailLimit.Minimum,
            summary.SentInLast24Hours,
            summary.Queued,
            summary.Failed,
            summary.NextSendAt,
            rows);
    }
}
