using Modbot.Core;
using Modbot.Core.Configuration;
using Modbot.Core.Logging;
using Serilog;

var env = ModbotEnvironment.Read();

// Logging comes up before anything else, so a failure during startup is recorded rather than lost.
Log.Logger = ModbotLogging.Create(new ModbotLogOptions
{
    Debug = env.DebugLogging,
    SeqUrl = env.SeqUrl,
});

try
{
    Log.Information("Modbot starting on port {Port}", env.Port);

    if (env.Validate() is { } problem)
    {
        // One clear sentence naming the variable -- the reader is looking at a hosting dashboard,
        // not a stack trace.
        Log.Fatal("{Problem}", problem);
        return 1;
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.WebHost.UseUrls($"http://0.0.0.0:{env.Port}");

    builder.Services.AddSingleton(env);
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    app.MapHealthChecks("/health/live");
    app.MapGet("/api/version", () => Results.Ok(new
    {
        // Calendar version (spec 2.7.1); apiVersion is separate and bumps only on a breaking
        // change, so one client build works across a span of server releases.
        version = ModbotVersion.Release,
        apiVersion = ModbotVersion.Api,
    }));

    // The SPA is built into wwwroot by the Modbot.Web Vite build.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Modbot failed to start");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Exposed so WebApplicationFactory<Program> can boot the host in tests.
public partial class Program;
