using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Mail;

namespace Modbot.Cloud.Features.InstanceAlerts;

/// <param name="On">Whether Cloud emails about this deployment.</param>
/// <param name="Email">Where the emails go. Empty means the account that claimed this server.</param>
/// <param name="SilentAfterMinutes">Minutes of hearing nothing before that counts as a problem.</param>
/// <param name="ErrorsAnHour">Errors in an hour before that counts as a problem. 0 turns it off.</param>
/// <param name="QuietHours">How long Cloud stays quiet after an email about this deployment.</param>
/// <param name="Problem">Whether something is wrong right now.</param>
/// <param name="Since">When it started.</param>
/// <param name="Detail">What is wrong.</param>
/// <param name="LastSentAt">When the last email went.</param>
/// <param name="LastError">Why the last email could not be sent.</param>
/// <param name="MailConfigured">Whether Cloud can send email at all.</param>
/// <param name="SendsTo">The address the next email would actually go to. Null when there is none.</param>
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
    bool MailConfigured,
    string? SendsTo);

/// <param name="On">Whether Cloud emails about this deployment.</param>
/// <param name="Email">Where the emails go. Empty means the account that claimed this server.</param>
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
/// Watching one Modbot deployment from outside: whether Cloud emails about it, and who.
/// </summary>
/// <remarks>
/// A Cloud administrator, or the account that claimed the server — the same rule the log viewer
/// uses, decided in the same way. An owner never has to type their own address: leaving it empty
/// sends to the account's.
/// </remarks>
public static class AdminInstanceAlertEndpoints
{
    public static IEndpointRouteBuilder MapAdminInstanceAlerts(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/admin/servers/{serverId:guid}/alerts", ReadAsync);
        app.MapPut("/api/admin/servers/{serverId:guid}/alerts", SaveAsync);

        return app;
    }

    /// <summary>An administrator, or the account that claimed this server.</summary>
    private static async Task<bool> MayEditAsync(HttpContext http, Guid serverId)
    {
        if (await AdminAccess.IsAdminAsync(http))
            return true;

        if (await AccountAccess.ReadAsync(http) is not { } account)
            return false;

        var cloud = http.RequestServices.GetRequiredService<CloudContext>();

        return await cloud.RegisteredServers
            .AnyAsync(s => s.Id == serverId && s.AccountId == account.Id, http.RequestAborted);
    }

    internal static async Task<IResult> ReadAsync(
        [FromRoute] Guid serverId,
        [FromServices] CloudContext cloud,
        [FromServices] ICloudMailer mailer,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await MayEditAsync(http, serverId))
            return Refused(http);

        var alert = await cloud.InstanceAlerts.AsNoTracking().SingleOrDefaultAsync(a => a.ServerId == serverId, ct)
                    ?? new InstanceAlert { ServerId = serverId };

        return Results.Ok(View(alert, mailer, await OwnerAddressAsync(cloud, serverId, ct)));
    }

    internal static async Task<IResult> SaveAsync(
        [FromRoute] Guid serverId,
        [FromBody] InstanceAlertUpdate? request,
        [FromServices] CloudContext cloud,
        [FromServices] ICloudMailer mailer,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await MayEditAsync(http, serverId))
            return Refused(http);

        if (request is null)
            return Results.Json(new { error = "Nothing to save." }, statusCode: StatusCodes.Status400BadRequest);

        if (request.SilentAfterMinutes is < 5 or > 10_080)
            return Results.Json(new { error = "Silence is 5 minutes to 7 days." }, statusCode: StatusCodes.Status400BadRequest);

        if (request.ErrorsAnHour is < 0 or > 1_000_000)
            return Results.Json(new { error = "That is not a number of errors." }, statusCode: StatusCodes.Status400BadRequest);

        if (request.QuietHours is < 0 or > 168)
            return Results.Json(new { error = "Quiet time is 0 to 168 hours." }, statusCode: StatusCodes.Status400BadRequest);

        var email = ClientText.Clean(request.Email, InstanceAlert.MaxEmailLength) ?? "";

        if (email.Length > 0 && !email.Contains('@', StringComparison.Ordinal))
            return Results.Json(new { error = "That is not an address." }, statusCode: StatusCodes.Status400BadRequest);

        if (!await cloud.RegisteredServers.AnyAsync(s => s.Id == serverId, ct))
            return Results.Json(new { error = "Server not found." }, statusCode: StatusCodes.Status404NotFound);

        var owner = await OwnerAddressAsync(cloud, serverId, ct);

        // Turning it on with nowhere to send is the one refusal: an alert nobody receives is worse
        // than no alert, because it looks set up.
        if (request.On && email.Length == 0 && string.IsNullOrWhiteSpace(owner))
        {
            return Results.Json(
                new { error = "Nobody has claimed this server, so an address is needed." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var alert = await cloud.InstanceAlerts.SingleOrDefaultAsync(a => a.ServerId == serverId, ct);

        if (alert is null)
        {
            alert = new InstanceAlert { ServerId = serverId };
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

        return Results.Ok(View(alert, mailer, owner));
    }

    /// <summary>The address on the account that claimed this server, or null while nobody has.</summary>
    private static Task<string?> OwnerAddressAsync(CloudContext cloud, Guid serverId, CancellationToken ct) =>
        cloud.RegisteredServers.AsNoTracking()
            .Where(s => s.Id == serverId && s.AccountId != null)
            .Join(cloud.Accounts.AsNoTracking(), s => s.AccountId, a => a.Id, (_, a) => a.Email)
            .FirstOrDefaultAsync(ct);

    private static IResult Refused(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";

        return Results.Json(new { error = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static InstanceAlertView View(InstanceAlert alert, ICloudMailer mailer, string? owner) => new(
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
        mailer.CanSend,
        string.IsNullOrWhiteSpace(alert.Email) ? owner : alert.Email);
}
