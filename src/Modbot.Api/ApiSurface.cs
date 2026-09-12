using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

        return app;
    }
}

/// <param name="Version">Calendar release version, <c>YYYY.M.PATCH</c>.</param>
/// <param name="ApiVersion">Highest API version this server speaks.</param>
/// <param name="ApiVersionMinimum">Lowest API version this server still speaks.</param>
public sealed record VersionResponse(string Version, int ApiVersion, int ApiVersionMinimum);
