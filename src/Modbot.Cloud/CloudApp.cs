using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Cloud.Auth;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Mail;
using Modbot.Cloud.Features.AdminInstalls;
using Modbot.Cloud.Features.Health;
using Modbot.Cloud.Features.InstanceLogs;
using Modbot.Cloud.Features.Installs;
using Modbot.Cloud.Features.EventBackup;
using Modbot.Cloud.Features.Pages;
using Modbot.Cloud.Features.PublicRooms;
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
    /// <param name="roomsApiKey">Reads the public rooms feed. Null leaves only the root key.</param>
    /// <param name="proxyApiKey">
    /// What my.modbot.co and the landing page send. Null closes <c>/api/v1/site</c> to everyone.
    /// </param>
    /// <param name="mail">Where Cloud's mail goes out through. Without a key it sends nothing.</param>
    /// <param name="runDailyUpkeep">False in tests, which run retention themselves against a fake clock.</param>
    public static void AddServices(
        IServiceCollection services,
        string connectionString,
        string engineConnectionString,
        string? rootApiKey,
        string? roomsApiKey = null,
        string? proxyApiKey = null,
        MailSettings? mail = null,
        bool runDailyUpkeep = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineConnectionString);

        // TryAdd, so a test that registered its own clock first keeps it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<CloudContext>(o => o.UseNpgsql(connectionString));
        services.AddDbContext<EngineContext>(o => EngineContext.Use(o, engineConnectionString));

        var root = new RootApiKey(rootApiKey);
        services.AddSingleton(root);
        services.AddSingleton(new RoomsApiKey(roomsApiKey, root));
        services.AddSingleton(new ProxyApiKey(proxyApiKey));
        services.AddSingleton<AppPage>();
        services.AddSingleton<AdminSessions>();
        services.AddSingleton<LoginAttempts>();
        services.AddSingleton<RegistrationLimit>();
        services.AddSingleton<EventBackupLimits>();
        services.AddSingleton<PublicRoomsLimit>();
        services.AddSingleton<InstanceLogLimits>();

        // Accounts. The hasher is Identity's, standalone: Cloud wants the hash function and none of
        // the rest of Identity, the same way a Modbot server uses it for its own staff accounts.
        services.AddSingleton(mail ?? new MailSettings(null, null, new Uri(Configuration.CloudEnvironment.DefaultPublicUrl)));
        services.AddHttpClient();
        services.TryAddSingleton<ICloudMailer, ResendMailer>();
        services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
        services.AddSingleton<AccountSessions>();
        services.AddSingleton<AccountTokens>();
        services.AddSingleton<AccountLimits>();

        services.AddScoped<EventBatchWriter>();
        services.AddScoped<RetentionPruner>();

        // The log feed Modbot deployments send: the writer, the months it writes into, and the
        // retention that drops whole months of them.
        services.AddScoped<LogBatchWriter>();
        services.AddScoped<LogPartitionMaintainer>();
        services.AddScoped<LogRetention>();

        if (runDailyUpkeep)
            services.AddHostedService<DailyUpkeepService>();
    }

    /// <summary>
    /// Migrates both databases before anything is served. Returns one sentence describing the problem,
    /// or null when Cloud is ready.
    /// </summary>
    public static Task<string?> PrepareAsync(IServiceProvider services, ILogger log, CancellationToken ct = default) =>
        DatabaseMigrator.ApplyAsync(services, log, ct);

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
        app.MapAdminInstalls();
        app.MapTime();
        app.MapInstalls();
        app.MapEventBackup();
        app.MapPublicRooms();
        app.MapInstanceLogs();
        app.MapAdminLogs();
        app.MapAccounts();
    }
}
