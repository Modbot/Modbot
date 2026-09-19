using Modbot.Core.Logging;
using Modbot.Landing;
using Modbot.Landing.Configuration;
using Serilog;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.Landing — modbot.co, the public page that says what Modbot is.
//
// Composition only. Every endpoint lives in its feature folder under Features/, and LandingApp
// wires them together so the tests build the same app over a test server. There is no database:
// the pages are built once by Web/ into wwwroot, and everything else is an environment variable,
// listed in this project's README.
// ─────────────────────────────────────────────────────────────────────────────

Log.Logger = ModbotServiceLog.Create("Modbot.Landing");

try
{
    var environment = LandingEnvironment.Read();

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://0.0.0.0:{environment.Port}");

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger);

    LandingApp.AddServices(builder.Services, environment);

    var app = builder.Build();

    // One line per request. See ModbotRequestLog for why the policy is shared and the wiring is not.
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = ModbotRequestLog.MessageTemplate;
        options.GetLevel = (context, _, error) => ModbotRequestLog.LevelFor(
            context.Request.Path.Value ?? "", context.Response.StatusCode, error is not null);
    });

    LandingApp.MapEndpoints(app);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Modbot.Landing failed to start");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
