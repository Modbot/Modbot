using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.My.Auth;
using Modbot.My.Configuration;
using Modbot.My.Data;
using Modbot.My.Features.Admin;
using Modbot.My.Features.Health;
using Modbot.My.Features.Instances;
using Modbot.My.Features.Pages;
using Modbot.My.Features.RegisterPage;
using Modbot.My.Features.Stats;
using Modbot.My.Features.TermLists;
using Modbot.My.Features.Visits;

namespace Modbot.My;

/// <summary>
/// Wires the features together. Program.cs and the test host both call this, so the app under test
/// is the app that ships.
/// </summary>
public static class MyApp
{
    public static void AddServices(
        IServiceCollection services,
        string connectionString,
        string? rootApiKey,
        CloudAddress cloud)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // TryAdd, so a test that registered its own clock first keeps it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<MyContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton(new RootApiKey(rootApiKey));
        services.AddSingleton(cloud);
        services.AddSingleton<AppPage>();
        services.AddSingleton<AdminSessions>();
        services.AddSingleton<LoginAttempts>();

        services.AddCors(o => o.AddDefaultPolicy(policy => policy
            // Term lists are public data fetched by self-hosted deployments at arbitrary origins.
            // The registry reads that share this policy need the root API key as a header; the admin
            // cookie is never sent cross-origin, because the policy does not allow credentials.
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
        app.MapAdmin();
        app.MapRegisterPage();
        app.MapInstances();
        app.MapStats();
        app.MapTermLists();
    }
}
