using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Cloud.Auth;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Health;
using Modbot.Cloud.Features.Installs;
using Modbot.Cloud.Features.LogBackup;
using Modbot.Cloud.Features.Pages;
using Modbot.Cloud.Features.Retention;
using Modbot.Cloud.Features.Time;

namespace Modbot.Cloud;

/// <summary>
/// Wires the features together. Program.cs and the test host both call this, so the app under test
/// is the app that ships.
/// </summary>
public static class CloudApp
{
    /// <param name="services">The container.</param>
    /// <param name="connectionString">Cloud's main database.</param>
    /// <param name="engineConnectionString">The event storage database.</param>
    /// <param name="rootApiKey">Unlocks admin. Null closes it to everyone.</param>
    /// <param name="runDailyUpkeep">False in tests, which run upkeep themselves against a fake clock.</param>
    public static void AddServices(
        IServiceCollection services,
        string connectionString,
        string engineConnectionString,
        string? rootApiKey,
        bool runDailyUpkeep = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineConnectionString);

        // TryAdd, so a test that registered its own clock first keeps it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<CloudContext>(o => o.UseNpgsql(connectionString));
        services.AddDbContext<EngineContext>(o => EngineContext.Use(o, engineConnectionString));

        services.AddSingleton(new RootApiKey(rootApiKey));
        services.AddSingleton<AppPage>();
        services.AddSingleton<AdminSessions>();
        services.AddSingleton<LoginAttempts>();
        services.AddSingleton<RegistrationLimit>();
        services.AddSingleton<LogBackupLimits>();

        services.AddScoped<LogBatchWriter>();
        services.AddScoped<PartitionMaintainer>();
        services.AddScoped<RetentionPruner>();

        if (runDailyUpkeep)
            services.AddHostedService<DailyUpkeepService>();
    }

    /// <summary>
    /// Migrates both databases and makes this month's partitions, before anything is served. Returns
    /// one sentence describing the problem, or null when Cloud is ready.
    /// </summary>
    public static async Task<string?> PrepareAsync(IServiceProvider services, ILogger log, CancellationToken ct = default)
    {
        if (await DatabaseMigrator.ApplyAsync(services, log, ct) is { } problem)
            return problem;

        await using var scope = services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<PartitionMaintainer>().EnsureAsync(ct);
        if (created.Count > 0)
            log.LogInformation("Created partitions {Partitions}", created);

        return null;
    }

    public static void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Static files run before routing; index.html is only served through its routes.
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

        app.MapHealth();
        app.MapPages();
        app.MapAdmin();
        app.MapTime();
        app.MapInstalls();
        app.MapLogBackup();
    }
}
