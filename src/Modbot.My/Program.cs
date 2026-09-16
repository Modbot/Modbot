using Modbot.Core.Logging;
using Modbot.My;
using Modbot.My.Configuration;
using Serilog;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.My — my.modbot.co.
//
// Composition only. Every endpoint lives in its feature folder under Features/, and MyApp wires
// them together so the tests can build the same app over a test server.
//
// No database (central services spec 2.1.1). Everything the page shows comes from Modbot Cloud at
// MODBOT_CLOUD_PROXY_URL, read with MODBOT_CLOUD_API_KEY, which never reaches a browser.
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

    if (environment.CloudApiKey is null)
    {
        Log.Fatal(
            "{Problem}",
            $"{MyEnvironment.CloudApiKeyVariable} is not set. my.modbot.co reads everything from Modbot "
            + "Cloud and cannot do anything without the key Cloud accepts.");
        return 1;
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://0.0.0.0:{environment.Port}");

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger);

    var cloud = CloudAddress.From(environment);
    MyApp.AddServices(builder.Services, cloud);

    var app = builder.Build();

    app.Logger.LogInformation("Reading from Modbot Cloud at {Endpoint}", cloud.Endpoint);

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
