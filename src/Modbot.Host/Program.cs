using Modbot.Api;
using Scalar.AspNetCore;
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
    builder.Services.AddModbotApi();

    var app = builder.Build();

    app.MapHealthChecks("/health/live");
    app.MapModbotApi();

    // The OpenAPI document is generated from the endpoints, so it cannot drift from what is
    // actually served. Scalar renders it for humans; the raw JSON feeds client generation.
    app.MapOpenApi("/api/openapi/{documentName}.json");
    app.MapScalarApiReference("/api/reference", options => options
        .WithTitle("Modbot API")
        .WithOpenApiRoutePattern("/api/openapi/{documentName}.json"));

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
