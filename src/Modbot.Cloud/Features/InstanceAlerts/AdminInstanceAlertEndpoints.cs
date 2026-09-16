using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Mail;

namespace Modbot.Cloud.Features.InstanceAlerts;

/// <param name="On">Whether Cloud emails about this deployment.</param>
/// <param name="Email">Where the emails go.</param>
/// <param name="SilentAfterMinutes">Minutes of hearing nothing before that counts as a problem.</param>
/// <param name="ErrorsAnHour">Errors in an hour before that counts as a problem. 0 turns it off.</param>
/// <param name="QuietHours">How long Cloud stays quiet after an email about this deployment.</param>
/// <param name="Problem">Whether something is wrong right now.</param>
/// <param name="Since">When it started.</param>
/// <param name="Detail">What is wrong.</param>
/// <param name="LastSentAt">When the last email went.</param>
/// <param name="LastError">Why the last email could not be sent.</param>
/// <param name="MailConfigured">Whether Cloud can send email at all.</param>
public sealed record InstanceAlertView(
    bool On,
    string Email,
    int SilentAfterMinutes,
    int ErrorsAnHour,
    int QuietHours,
    bool Problem,
    DateTimeOffset? Since,
    string? Detail,
    DateTimeOffset? LastSentAt,
    string? LastError,
    bool MailConfigured);

/// <param name="On">Whether Cloud emails about this deployment.</param>
/// <param name="Email">Where the emails go.</param>
/// <param name="SilentAfterMinutes">5 to 10080.</param>
/// <param name="ErrorsAnHour">0 to 1000000. 0 turns the error check off.</param>
/// <param name="QuietHours">0 to 168.</param>
public sealed record InstanceAlertUpdate(
    bool On,
    string? Email,
    int SilentAfterMinutes,
    int ErrorsAnHour,
    int QuietHours);

/// <summary>
/// Cloud admin: watching one Modbot deployment from outside.
/// </summary>
/// <remarks>
/// Behind the admin sign-in, like everything else that reads a deployment's data. When Cloud has
/// accounts, the owner of the deployment sets this for themselves and the address defaults to
/// theirs — the same one place the log viewer's owner check lives.
/// </remarks>
public static class AdminInstanceAlertEndpoints
{
    public static IEndpointRouteBuilder MapAdminInstanceAlerts(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapGet("/installs/{installId:guid}/alerts", ReadAsync);
        admin.MapPut("/installs/{installId:guid}/alerts", SaveAsync);

        return app;
    }

    internal static async Task<IResult> ReadAsync(
        [FromRoute] Guid installId,
        [FromServices] CloudContext cloud,
        [FromServices] ICloudMailer mailer,
        CancellationToken ct)
    {
        var alert = await cloud.InstanceAlerts.AsNoTracking().SingleOrDefaultAsync(a => a.InstallId == installId, ct)
                    ?? new InstanceAlert { InstallId = installId };

        return Results.Ok(View(alert, mailer));
    }

    internal static async Task<IResult> SaveAsync(
        [FromRoute] Guid installId,
        [FromBody] InstanceAlertUpdate? request,
        [FromServices] CloudContext cloud,
        [FromServices] ICloudMailer mailer,
        CancellationToken ct)
    {
        if (request is null)
            return Results.Json(new { error = "Nothing to save." }, statusCode: StatusCodes.Status400BadRequest);

        if (request.SilentAfterMinutes is < 5 or > 10_080)
            return Results.Json(new { error = "Silence is 5 minutes to 7 days." }, statusCode: StatusCodes.Status400BadRequest);

        if (request.ErrorsAnHour is < 0 or > 1_000_000)
            return Results.Json(new { error = "That is not a number of errors." }, statusCode: StatusCodes.Status400BadRequest);

        if (request.QuietHours is < 0 or > 168)
            return Results.Json(new { error = "Quiet time is 0 to 168 hours." }, statusCode: StatusCodes.Status400BadRequest);

        var email = ClientText.Clean(request.Email, InstanceAlert.MaxEmailLength) ?? "";

        if (request.On && !email.Contains('@', StringComparison.Ordinal))
            return Results.Json(new { error = "An address is needed to send to." }, statusCode: StatusCodes.Status400BadRequest);

        if (!await cloud.Installs.AnyAsync(i => i.Id == installId, ct))
            return Results.Json(new { error = "Install not found." }, statusCode: StatusCodes.Status404NotFound);

        var alert = await cloud.InstanceAlerts.SingleOrDefaultAsync(a => a.InstallId == installId, ct);

        if (alert is null)
        {
            alert = new InstanceAlert { InstallId = installId };
            cloud.InstanceAlerts.Add(alert);
        }

        // Turning it off clears what it was saying, so turning it back on later does not open with
        // a recovery email about a problem nobody was told about.
        if (alert.On && !request.On)
        {
            alert.Problem = false;
            alert.Since = null;
            alert.Detail = null;
            alert.LastSentAt = null;
        }

        alert.On = request.On;
        alert.Email = email;
        alert.SilentAfterMinutes = request.SilentAfterMinutes;
        alert.ErrorsAnHour = request.ErrorsAnHour;
        alert.QuietHours = request.QuietHours;

        await cloud.SaveChangesAsync(ct);

        return Results.Ok(View(alert, mailer));
    }

    private static InstanceAlertView View(InstanceAlert alert, ICloudMailer mailer) => new(
        alert.On,
        alert.Email,
        alert.SilentAfterMinutes,
        alert.ErrorsAnHour,
        alert.QuietHours,
        alert.Problem,
        alert.Since,
        alert.Detail,
        alert.LastSentAt,
        alert.LastError,
        mailer.CanSend);
}
