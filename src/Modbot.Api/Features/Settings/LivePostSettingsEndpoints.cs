using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Posts → Live posts, as stored.</summary>
/// <param name="Scope">Which instances get a post: <c>off</c>, <c>calendar</c>, <c>any</c> or <c>modbot</c>.</param>
/// <param name="Auto">Post straight away, with no draft and no message to the host.</param>
public sealed record LivePostSettingsView(string Scope, bool Auto);

/// <summary>Changes to the live post settings. Every part left out keeps what is stored.</summary>
/// <param name="Scope"><c>off</c>, <c>calendar</c>, <c>any</c> or <c>modbot</c>.</param>
/// <param name="Auto">True to post straight away; false to make a draft for a host to approve.</param>
public sealed record LivePostSettingsUpdate(string? Scope = null, bool? Auto = null);

/// <summary>
/// The "we're live" group post settings: which instances get one, and whether it is a draft for a host
/// to approve or is posted straight away. Needs Manage settings, as the Posts settings do. Every change
/// is recorded as a settings change.
/// </summary>
public static class LivePostSettingsEndpoints
{
    public static IEndpointRouteBuilder MapLivePostSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/live-posts")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetLivePostSettings")
            .WithSummary("Get live post settings")
            .WithDescription("Which instances get a \"we're live\" group post, and whether it is a draft or is posted at once.")
            .Produces<LivePostSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] LivePostSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.Scope is not null && !LivePostScopes.IsKnown(body.Scope))
                    return Results.BadRequest(new { error = "Pick which instances get a post." });

                var settings = await db.GetSettingsAsync(ct);

                var scopeAfter = body.Scope ?? settings.LivePostScope;
                var autoAfter = body.Auto ?? settings.LivePostAuto;

                var change = new SettingsChange("livePosts")
                    .Field("scope", settings.LivePostScope, scopeAfter)
                    .Field("auto", settings.LivePostAuto, autoAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Turning it on never posts about the instances already running.
                if (scopeAfter != LivePostScopes.Off && settings.LivePostScope == LivePostScopes.Off)
                    settings.LivePostFrom = clock.UtcNow;

                settings.LivePostScope = scopeAfter;
                settings.LivePostAuto = autoAfter;
                await db.SaveChangesAsync(ct);

                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetLivePostSettings")
            .WithSummary("Update live post settings")
            .WithDescription(
                "Pick which instances get a \"we're live\" group post (`scope`: `off`, `calendar`, `any` or "
                + "`modbot`) and whether it is posted straight away (`auto`) or made as a draft in Marketing "
                + "with a message to the host. Turning it on never posts about instances already running. "
                + "An event that has its own \"announce in VRChat\" box ticked keeps posting for itself.")
            .Accepts<LivePostSettingsUpdate>("application/json")
            .Produces<LivePostSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static LivePostSettingsView View(Core.Data.Entities.Settings settings) =>
        new(settings.LivePostScope, settings.LivePostAuto);
}
