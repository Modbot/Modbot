using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Health.Alerts;

/// <param name="Check">The check's name, which never changes.</param>
/// <param name="Label">What to call it on the screen.</param>
/// <param name="On">Whether it emails anybody.</param>
/// <param name="Problem">Whether it is failing right now.</param>
/// <param name="Since">When it started failing.</param>
/// <param name="Detail">What is wrong, in one sentence.</param>
public sealed record HealthWatchView(
    string Check,
    string Label,
    bool On,
    bool Problem,
    DateTimeOffset? Since,
    string? Detail);

/// <param name="UserId">The staff account.</param>
/// <param name="Username">Its name.</param>
/// <param name="Email">Its address, or null when it has none — such an account gets no alerts.</param>
/// <param name="Chosen">Whether it gets the alerts.</param>
public sealed record HealthRecipientView(Guid UserId, string Username, string? Email, bool Chosen);

/// <param name="QuietHours">How long a check stays quiet after an email about it.</param>
/// <param name="StorageWarnGb">The database size the storage check fires at. 0 turns it off.</param>
/// <param name="EmailConfigured">Whether this Modbot can send email at all.</param>
/// <param name="LastCheckedAt">When the checks last ran.</param>
public sealed record HealthAlertView(
    int QuietHours,
    double StorageWarnGb,
    bool EmailConfigured,
    DateTimeOffset? LastCheckedAt,
    IReadOnlyList<HealthWatchView> Watches,
    IReadOnlyList<HealthRecipientView> Recipients);

/// <param name="QuietHours">0 to 168.</param>
/// <param name="StorageWarnGb">0 turns the storage check off.</param>
/// <param name="ChecksOn">The checks that email somebody. Everything else is off.</param>
/// <param name="RecipientUserIds">The staff accounts that get the emails.</param>
public sealed record HealthAlertUpdate(
    int QuietHours,
    double StorageWarnGb,
    IReadOnlyList<string> ChecksOn,
    IReadOnlyList<Guid> RecipientUserIds);

/// <summary>
/// Setting up the emails Modbot sends about its own health.
/// </summary>
/// <remarks>
/// Reading takes <see cref="ModbotPermissions.ViewOperationalLog"/>, the same permission the Health
/// page takes, because the state of each check is the same information. Changing takes
/// <see cref="ModbotPermissions.ManageSettings"/>: who gets email about a deployment is the
/// operator's decision, not a moderator's.
/// </remarks>
public static class HealthAlertEndpoints
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    public static IEndpointRouteBuilder MapHealthAlerts(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/health/alerts").WithTags("Health").RequireAuthorization();

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] Modbot.Core.Email.IEmailSender email,
                CancellationToken ct) =>
            {
                return Results.Ok(await ReadAsync(db, email, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetHealthAlerts")
            .WithSummary("What Modbot watches about itself, who is emailed, and what is wrong now")
            .Produces<HealthAlertView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                [FromBody] HealthAlertUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] Modbot.Core.Email.IEmailSender email,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.QuietHours is < 0 or > HealthAlertSettings.MaxQuietHours)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Quiet time is 0 to {HealthAlertSettings.MaxQuietHours} hours.",
                    });
                }

                if (body.StorageWarnGb is < 0 or > 1_000_000)
                    return Results.BadRequest(new { error = "That is not a database size." });

                var unknown = body.ChecksOn.FirstOrDefault(c => !HealthChecks.IsKnown(c));
                if (unknown is not null)
                    return Results.BadRequest(new { error = $"Modbot does not watch \"{unknown}\"." });

                var settings = await db.HealthAlertSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings is null)
                {
                    settings = new HealthAlertSettings();
                    db.HealthAlertSettings.Add(settings);
                }

                settings.QuietHours = body.QuietHours;
                settings.StorageWarnBytes = (long)(body.StorageWarnGb * Gigabyte);

                var on = body.ChecksOn.ToHashSet(StringComparer.Ordinal);
                var watches = await db.HealthWatches.ToDictionaryAsync(w => w.Check, ct);

                foreach (var check in HealthChecks.All)
                {
                    if (!watches.TryGetValue(check, out var watch))
                    {
                        watch = new HealthWatch { Check = check };
                        db.HealthWatches.Add(watch);
                    }

                    var wanted = on.Contains(check);

                    // Turning a check off clears what it was saying, so turning it back on later
                    // does not open with a recovery email about a problem nobody was told about.
                    if (watch.On && !wanted)
                    {
                        watch.Problem = false;
                        watch.Since = null;
                        watch.Detail = null;
                        watch.LastSentAt = null;
                    }

                    watch.On = wanted;
                }

                var chosen = body.RecipientUserIds.ToHashSet();
                var existing = await db.HealthAlertRecipients.ToListAsync(ct);

                db.HealthAlertRecipients.RemoveRange(existing.Where(r => !chosen.Contains(r.UserId)));

                var already = existing.Select(r => r.UserId).ToHashSet();
                var real = await db.Users.Where(u => chosen.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);

                foreach (var id in real.Where(id => !already.Contains(id)))
                    db.HealthAlertRecipients.Add(new HealthAlertRecipient { UserId = id });

                await db.SaveChangesAsync(ct);

                return Results.Ok(await ReadAsync(db, email, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("SetHealthAlerts")
            .WithSummary("Choose what Modbot watches about itself and who is emailed")
            .Produces<HealthAlertView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<HealthAlertView> ReadAsync(
        ModbotContext db, Modbot.Core.Email.IEmailSender email, CancellationToken ct)
    {
        var settings = await db.HealthAlertSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct)
                       ?? new HealthAlertSettings();

        var watches = await db.HealthWatches.AsNoTracking().ToDictionaryAsync(w => w.Check, ct);

        var chosen = await db.HealthAlertRecipients.AsNoTracking()
            .Select(r => r.UserId)
            .ToListAsync(ct);

        var users = await db.Users.AsNoTracking()
            .Where(u => !u.IsDisabled)
            .OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.Email })
            .ToListAsync(ct);

        return new HealthAlertView(
            settings.QuietHours,
            settings.StorageWarnBytes / (double)Gigabyte,
            await email.IsConfiguredAsync(ct),
            settings.LastCheckedAt,
            [
                .. HealthChecks.All.Select(check =>
                {
                    watches.TryGetValue(check, out var watch);

                    return new HealthWatchView(
                        check,
                        HealthChecks.LabelOf(check),
                        watch?.On ?? false,
                        watch?.Problem ?? false,
                        watch?.Since,
                        watch?.Detail);
                }),
            ],
            [
                .. users.Select(u => new HealthRecipientView(
                    u.Id, u.Username, u.Email, chosen.Contains(u.Id))),
            ]);
    }
}
