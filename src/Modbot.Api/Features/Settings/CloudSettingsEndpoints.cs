using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Cloud;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="Disabled">True when <c>MODBOT_CLOUD_DISABLED</c> is set: nothing is sent.</param>
/// <param name="Endpoint">The Modbot Cloud this server talks to.</param>
/// <param name="Registered">Whether Cloud has given this server an id.</param>
/// <param name="LastReportAt">When the last report was sent, or null before the first.</param>
/// <param name="LastReportOk">Whether Cloud took it, or null before the first.</param>
/// <param name="LastReportProblem">What went wrong last time, or null.</param>
/// <param name="ReportOn">
/// The switch: whether this server sends its usage report every six hours. Not the same as
/// <paramref name="Disabled"/>, which beats it.
/// </param>
public sealed record CloudStatusView(
    bool Disabled,
    string Endpoint,
    bool Registered,
    DateTimeOffset? LastReportAt,
    bool? LastReportOk,
    string? LastReportProblem,
    bool ReportOn);

/// <param name="ReportOn">True to send the usage report, false to stop sending it.</param>
public sealed record SetCloudReportRequest(bool ReportOn);

/// <param name="Code">Eight characters to type into Modbot Cloud. Shown once and never stored here.</param>
/// <param name="ExpiresInMinutes">How long it lasts.</param>
public sealed record LinkCodeView(string Code, int ExpiresInMinutes);

/// <summary>
/// What this server tells Modbot Cloud, and the code that links it to a Cloud account
/// (Cloud accounts and registry spec 3.3).
/// </summary>
/// <remarks>
/// <para>
/// The code is made here and shown to the owner, who types it into Cloud. Cloud is told only the
/// code's SHA-256, over the connection this server opened with the secret only it holds — so Cloud
/// never has to call a Modbot server back at an address a stranger supplied.
/// </para>
/// <para>
/// Only somebody who can already sign in to this Modbot and change its settings can see the code.
/// That is the proof of ownership; there is nothing else to check.
/// </para>
/// </remarks>
public static class CloudSettingsEndpoints
{
    public const int LinkCodeMinutes = 15;

    public static IEndpointRouteBuilder MapCloudSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/cloud")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                HttpContext http,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                var cloud = http.RequestServices.GetService<ModbotCloudAddress>() ?? ModbotCloudAddress.Default;

                return Results.Ok(View(settings, cloud));
            })
            .WithName("GetCloudStatus")
            .WithSummary("Get Cloud status")
            .WithDescription(
                "Whether this server reports to Modbot Cloud, and how the last report went. "
                + "`reportOn` is the usage report switch; `disabled` is `MODBOT_CLOUD_DISABLED`, "
                + "which stops the report whatever the switch says.")
            .Produces<CloudStatusView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetCloudReportRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var cloud = http.RequestServices.GetService<ModbotCloudAddress>() ?? ModbotCloudAddress.Default;
                var before = settings.SendUsageReport;

                if (before == body.ReportOn)
                    return Results.Ok(View(settings, cloud));

                settings.SendUsageReport = body.ReportOn;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "sendUsageReport",
                        ["before"] = before,
                        ["after"] = body.ReportOn,
                    },
                    ct);

                return Results.Ok(View(settings, cloud));
            })
            .WithName("SetCloudReport")
            .WithSummary("Set the usage report")
            .WithDescription(
                "Turn the usage report to Modbot Cloud on or off. It is on when Modbot is installed. "
                + "Off stops the report every six hours; the switches for sending Modbot's log and "
                + "for listing public instances are separate. Writes a settings-changed entry to "
                + "the audit log.")
            .Produces<CloudStatusView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/link-code", async (
                [FromServices] ServerReporter reporter,
                HttpContext http,
                CancellationToken ct) =>
            {
                var cloud = http.RequestServices.GetService<ModbotCloudAddress>() ?? ModbotCloudAddress.Default;

                if (cloud.Disabled)
                {
                    return Results.BadRequest(new
                    {
                        error = "This server is set not to talk to Modbot Cloud.",
                    });
                }

                var (code, problem) = await reporter.NewLinkCodeAsync(ct);

                return code is null
                    ? Results.Json(
                        new { error = problem ?? "Modbot Cloud did not answer." },
                        statusCode: StatusCodes.Status502BadGateway)
                    : Results.Ok(new LinkCodeView(code, LinkCodeMinutes));
            })
            .WithName("CreateCloudLinkCode")
            .WithSummary("Create a link code")
            .WithDescription(
                "A code to type into Modbot Cloud to claim this server. "
                + "The code is shown once and is not stored here. Modbot Cloud is told only its "
                + "SHA-256, over this server's own authenticated connection, so Cloud never calls "
                + "back to an address it was given.")
            .Produces<LinkCodeView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status502BadGateway);

        return app;
    }

    private static CloudStatusView View(Core.Data.Entities.Settings settings, ModbotCloudAddress cloud) => new(
        cloud.Disabled,
        cloud.Endpoint.Host,
        settings.CloudServerId is { Length: > 0 },
        settings.CloudLastReportAt,
        settings.CloudLastReportOk,
        settings.CloudLastReportProblem,
        settings.SendUsageReport);
}
