using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.My.Cloud;
using Modbot.My.Common;
using Modbot.My.Configuration;
using Modbot.My.Features.Health;
using Modbot.My.Features.Pages;
using Modbot.My.Features.TermLists;
using Modbot.My.Features.Visits;

namespace Modbot.My;

/// <summary>
/// Wires the features together. Program.cs and the test host both call this, so the app under test
/// is the app that ships.
/// </summary>
/// <remarks>
/// There is no database here. Everything my.modbot.co shows comes from Modbot Cloud through
/// <see cref="CloudClient"/> (central services spec 2.1.1).
/// </remarks>
public static class MyApp
{
    public static void AddServices(IServiceCollection services, CloudAddress cloud)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cloud);

        // TryAdd, so a test that registered its own clock first keeps it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(cloud);
        services.AddSingleton<AppPage>();
        services.AddSingleton<SiteLimits>();
        services.AddHttpClient(CloudClient.HttpClientName);
        services.AddSingleton<CloudClient>();

        // Asking a Modbot address what it is. Its own client, because this one calls somewhere a
        // stranger named: no redirect is followed, and it only ever connects to an address out on
        // the public internet.
        services.AddHttpClient(ServerLookup.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = PublicAddresses.ConnectAsync,
                AutomaticDecompression = DecompressionMethods.All,
            });

        services.AddSingleton<ServerLookup>();

        services.AddCors(o => o.AddDefaultPolicy(policy => policy
            // The term list routes are public and are fetched by self-hosted deployments at
            // arbitrary origins. They are redirects to Cloud now, and nothing else here is read
            // cross-origin; the policy does not allow credentials, so no cookie travels under it.
            .AllowAnyOrigin().AllowAnyHeader().WithMethods("GET")));
    }

    public static void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Static files run before routing. The page fallback matches every path, and the static
        // files middleware stands aside for any request routing has already given an endpoint.
        // index.html is only ever served through its routes, so /index.html is a 404 like any other
        // path the app does not own.
        app.UseWhen(
            context => !context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase),
            branch => branch.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = file =>
                {
                    // Vite names every file under assets/ by its content, so one never changes.
                    if (file.Context.Request.Path.StartsWithSegments("/assets"))
                        file.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                },
            }));

        app.UseRouting();
        app.UseCors();

        app.MapHealth();
        app.MapPages();
        app.MapVisits();
        app.MapTermLists();
    }
}
