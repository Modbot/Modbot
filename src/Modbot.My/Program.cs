using Modbot.Core.Logging;
using Modbot.My;
using Modbot.My.Configuration;
using Modbot.My.Data;
using Serilog;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.My — my.modbot.co and Modbot Hub.
//
// Composition only. Every endpoint lives in its feature folder under Features/, and MyApp wires
// them together so the tests can build the same app over a test server.
//
// Governing rule (central services spec section 1): Modbot must be completely functional with zero
// contact to this service, forever.
// ─────────────────────────────────────────────────────────────────────────────

// Logging first, so a configuration problem is reported in whatever shape CONSOLE_LOG_MODE asked
// for rather than as a bare line on standard error that a log explorer cannot search.
Log.Logger = ModbotServiceLog.Create("Modbot.My");

try
{
    var environment = MyEnvironment.Read();

    if (environment.DatabaseUrl is null)
    {
        Log.Fatal(
            "{Problem}",
            $"{MyEnvironment.DatabaseUrlVariable} is not set. Modbot.My needs a PostgreSQL connection string; "
            + "the expected form is postgres://user:password@host:5432/database.");
        return 1;
    }

    string connectionString;
    try
    {
        connectionString = DatabaseUrl.ToConnectionString(environment.DatabaseUrl);
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

    MyApp.AddServices(builder.Services, connectionString, environment.RootApiKey, CloudAddress.From(environment));

    var app = builder.Build();

    if (await DatabaseMigrator.ApplyAsync(app.Services, app.Logger) is { } problem)
    {
        app.Logger.LogCritical("{Problem}", problem);
        return 1;
    }

    if (environment.RootApiKey is null)
    {
        app.Logger.LogWarning(
            "{Variable} is not set, so /admin and every endpoint that reads the registry refuse all requests.",
            MyEnvironment.RootApiKeyVariable);
    }

    // One line per request. See ModbotRequestLog for why the policy is shared and the wiring is not.
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = ModbotRequestLog.MessageTemplate;
        options.GetLevel = (context, _, error) => ModbotRequestLog.LevelFor(
            context.Request.Path.Value ?? "", context.Response.StatusCode, error is not null);
    });

    MyApp.MapEndpoints(app);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Modbot.My failed to start");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
