using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.My.Auth;
using Modbot.My.Data;
using Modbot.My.Features.Health;
using Modbot.My.Features.Instances;
using Modbot.My.Features.Pages;
using Modbot.My.Features.RegisterPage;
using Modbot.My.Features.Stats;
using Modbot.My.Features.TermLists;

namespace Modbot.My;

/// <summary>
/// Wires the features together. Program.cs and the test host both call this, so the app under test
/// is the app that ships.
/// </summary>
public static class MyApp
{
    public static void AddServices(IServiceCollection services, string connectionString, string? rootApiKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // TryAdd, so a test that registered its own clock first keeps it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<MyContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton(new RootApiKey(rootApiKey));
        services.AddSingleton<SelectorPage>();
        services.AddSingleton<TermListCatalog>();

        services.AddCors(o => o.AddDefaultPolicy(policy => policy
            // Term lists are public data fetched by self-hosted deployments at arbitrary origins.
            // The registry reads that share this policy are protected by the root API key, not by
            // the origin, and carry no cookies.
            .AllowAnyOrigin().AllowAnyHeader().WithMethods("GET")));
    }

    public static void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapHealth();
        app.MapPages();
        app.MapRegisterPage();
        app.MapInstances();
        app.MapStats();
        app.MapTermLists();
    }
}
