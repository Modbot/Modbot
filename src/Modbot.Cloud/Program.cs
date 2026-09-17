using Modbot.Cloud;
using Modbot.Cloud.Configuration;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Mail;
using Modbot.Core.Logging;
using Serilog;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.Cloud — cloud.modbot.co.
//
// Composition only. Every endpoint lives in its feature folder under Features/, and CloudApp wires
// them together so the tests can build the same app over a test server.
//
// Two databases: DATABASE_URL for installs, sessions and settings; DATABASE_ENGINE_URL for the
// presence events clients back up (cloud event backup spec 4.1). Both are required.
// ─────────────────────────────────────────────────────────────────────────────

// Logging first, so a configuration problem is reported in whatever shape CONSOLE_LOG_MODE asked
// for rather than as a bare line on standard error that a log explorer cannot search.
Log.Logger = ModbotServiceLog.Create("Modbot.Cloud");

try
{
    var environment = CloudEnvironment.Read();

    if (environment.Problems() is { Count: > 0 } problems)
    {
        foreach (var problem in problems)
            Log.Fatal("{Problem}", problem);

        return 1;
    }

    string connectionString, engineConnectionString;
    try
    {
        connectionString = DatabaseUrl.ToConnectionString(environment.DatabaseUrl!, CloudEnvironment.DatabaseUrlVariable);
        engineConnectionString = DatabaseUrl.ToConnectionString(environment.EngineDatabaseUrl!, CloudEnvironment.EngineDatabaseUrlVariable);
    }
    catch (FormatException e)
    {
        Log.Fatal("{Problem}", e.Message);
        return 1;
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://0.0.0.0:{environment.Port}");

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger);

    CloudApp.AddServices(
        builder.Services,
        connectionString,
        engineConnectionString,
        environment.RootApiKey,
        environment.InstancesApiKey,
        environment.ProxyApiKey,
        new MailSettings(environment.ResendApiKey, environment.MailFrom, environment.PublicAddress),
        gitHubToken: environment.GitHubToken,
        gitHubRepository: environment.GitHubRepository);

    var app = builder.Build();

    if (await CloudApp.PrepareAsync(app.Services, app.Logger) is { } startupProblem)
    {
        app.Logger.LogCritical("{Problem}", startupProblem);
        return 1;
    }

    if (environment.RootApiKey is null)
    {
        app.Logger.LogWarning(
            "{Variable} is not set, so /admin refuses all requests.",
            CloudEnvironment.RootApiKeyVariable);
    }

    if (environment.ProxyApiKey is null)
    {
        app.Logger.LogWarning(
            "{Variable} is not set, so my.modbot.co and the landing page cannot read anything.",
            CloudEnvironment.ProxyApiKeyVariable);
    }

    if (environment.ResendApiKey is null)
    {
        app.Logger.LogWarning(
            "{Variable} is not set, so Modbot Cloud sends no mail and nobody can register an account.",
            CloudEnvironment.ResendApiKeyVariable);
    }

    // One line per request. See ModbotRequestLog for why the policy is shared and the wiring is not.
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = ModbotRequestLog.MessageTemplate;
        options.GetLevel = (context, _, error) => ModbotRequestLog.LevelFor(
            context.Request.Path.Value ?? "", context.Response.StatusCode, error is not null);
    });

    CloudApp.MapEndpoints(app);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Modbot Cloud failed to start");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
