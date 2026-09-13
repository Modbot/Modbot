using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Auth.Login;
using Modbot.Api.Features.Auth.Logout;
using Modbot.Api.Features.Auth.Me;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Features.Health;
using Modbot.Api.Features.Settings;
using Modbot.Api.Features.Onboarding.Complete;
using Modbot.Api.Features.Onboarding.CreateAdmin;
using Modbot.Api.Features.Onboarding.Integrations;
using Modbot.Api.Features.Onboarding.SelectGroup;
using Modbot.Api.Features.Onboarding.Status;
using Modbot.Api.Features.Onboarding.TestConnection;
using Modbot.Api.Features.Onboarding.VerifyVRChat;
using Modbot.Core;

namespace Modbot.Api;

/// <summary>
/// Registers Modbot's HTTP surface and its OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// The document is generated from the endpoints themselves, so it cannot drift from what the server
/// actually serves — which is the whole reason for generating rather than hand-writing it. It feeds
/// three consumers: the documentation site, generated API clients, and anyone integrating against a
/// deployment through an <c>ApiKey</c>.
/// </para>
/// <para>
/// Feature slices live under <c>Features/</c> and register themselves here (spec 2.8). A slice owns
/// its endpoint, contracts, validation and handler in one folder.
/// </para>
/// </remarks>
public static class ApiSurface
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddModbotApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new()
                {
                    Title = "Modbot API",
                    Version = ModbotVersion.Release,
                    Description =
                        "Cached VRChat group data, moderation history and analytics for a single "
                        + "group.\n\n"
                        + "This is one self-hosted deployment's API. There is no central Modbot "
                        + "service and no shared endpoint — you are talking to somebody's own "
                        + "server.\n\n"
                        + $"API version {ModbotVersion.Api} (minimum supported "
                        + $"{ModbotVersion.ApiMinimum}). The API version is a plain integer, "
                        + "separate from the calendar release version, and increments only on a "
                        + "breaking change.",
                };
                return Task.CompletedTask;
            });
        });

        return services;
    }

    public static IEndpointRouteBuilder MapModbotApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Meta");

        api.MapGet("/version", () => Results.Ok(new VersionResponse(
                ModbotVersion.Release,
                ModbotVersion.Api,
                ModbotVersion.ApiMinimum)))
            .WithName("GetVersion")
            .WithSummary("Server and API version")
            .WithDescription(
                "Unauthenticated so a client can negotiate compatibility before it holds "
                + "credentials. Clients compare their own supported range against "
                + "apiVersionMinimum..apiVersion and use the highest both support.")
            .Produces<VersionResponse>();

        // Feature slices map themselves (spec 2.8); this list is the only place that knows they
        // all exist, and adding a feature is adding one line here plus one folder.
        app.MapLogin();
        app.MapLogout();
        app.MapMe();
        app.MapDataSettings();
        app.MapSyncSettings();

        // The read surface over the fact log and the daily totals derived from it. Sync health resolves
        // SyncDiagnostics optionally, so a host that maps the API without registering the
        // producers still starts and still answers -- it reports that nothing is syncing here
        // rather than failing to map.
        app.MapAuditLog();
        app.MapAnalytics();
        app.MapSyncHealth();
        app.MapEvidence();

        // Onboarding (spec 7.1). Each step is its own slice because each one is independently
        // re-runnable from settings later -- they are not stages of a single transaction, and
        // modelling them as one endpoint with a step counter would make the "re-run just the
        // connection check" case the awkward one instead of the ordinary one.
        app.MapOnboardingStatus();
        app.MapCreateAdmin();
        app.MapVerifyVRChat();
        app.MapTestConnection();
        app.MapSelectGroup();
        app.MapIntegrations();
        app.MapCompleteOnboarding();

        return app;
    }
}

/// <param name="Version">Calendar release version, <c>YYYY.M.PATCH</c>.</param>
/// <param name="ApiVersion">Highest API version this server speaks.</param>
/// <param name="ApiVersionMinimum">Lowest API version this server still speaks.</param>
public sealed record VersionResponse(string Version, int ApiVersion, int ApiVersionMinimum);
