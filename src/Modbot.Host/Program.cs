using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Modbot.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Modbot.Api;
using Modbot.Api.Auth;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Api.Features.Client;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Logging;
using Modbot.Evidence;
using Modbot.Evidence.Upload;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.Host.Data;
using Modbot.Host.Health;
using Modbot.Host.Startup;
using Scalar.AspNetCore;
using Serilog;

var env = ModbotEnvironment.Read();

// Logging comes up before anything else, so a failure during startup is recorded rather than lost.
// The clock is constructed here rather than resolved from DI: logging must exist before the
// container does, and SystemModbotClock is the only implementation permitted to read the machine.
var clock = new SystemModbotClock();

// Where the logs would go, and whether anything written there survives a redeploy. The platform
// supplies a default; the marker file supplies evidence, and evidence wins -- so a volume mounted
// on a platform assumed ephemeral is treated as ephemeral for exactly one boot and correctly from
// the next restart onwards. See PersistenceProbe.
var platform = HostPlatform.Detect();
var bootId = Guid.NewGuid().ToString("n");

var persistence = PersistenceProbe.Probe(ModbotLogOptions.DefaultDirectory, bootId);

// Log files are written regardless of what the probe found. The probe reports; it does not
// decide. A managed platform is only evidence that the disk *might* be discarded -- Railway,
// Fly.io and Render all support volumes -- and silently withholding the files from an operator
// who mounted one takes away the artefact they went looking for, in the situation where they
// most need it. The warning below is the whole intervention.
var writeLogFiles = !persistence.IsUnwritable;

Log.Logger = ModbotLogging.Create(
    new ModbotLogOptions
    {
        Debug = env.DebugLogging,
        SeqUrl = env.SeqUrl,
        WriteFiles = writeLogFiles,
    },
    clock);

try
{
    Log.Information("Modbot starting on port {Port}", env.Port);
    Log.Information(
        "Host looks like {Platform}{Evidence}. {Persistence}",
        platform.Name,
        platform.Evidence is null ? "" : $" (from {platform.Evidence})",
        persistence.Explanation);

    if (!writeLogFiles)
    {
        Log.Error(
            "Log files are not being written. {Explanation} Console and Seq are unaffected.",
            persistence.Explanation);
    }
    else if (platform.AssumeEphemeralFilesystem
             && persistence.Evidence != PersistenceEvidence.SurvivedRestart)
    {
        // A warning, never a decision. If a volume is mounted this is a false alarm that the next
        // restart clears by itself, and the cost of being wrong in this direction is one log
        // line -- against losing the files entirely in the other.
        Log.Warning(
            "Log files are being written to {Directory}, but {Platform} discards the container "
            + "filesystem on redeploy unless a volume is mounted there. If you have not mounted "
            + "one, these files will not survive. {Seq}",
            ModbotLogOptions.DefaultDirectory,
            platform.Name,
            env.SeqUrl is null
                ? "Set SEQ_URL for a durable copy."
                : "SEQ_URL is set, so a durable copy is being shipped there.");
    }

    if (env.Validate() is { } problem)
    {
        // One clear sentence naming the variable -- the reader is looking at a hosting dashboard,
        // not a stack trace.
        Log.Fatal("{Problem}", problem);
        return 1;
    }

    string connectionString;
    try
    {
        connectionString = DatabaseUrl.ToConnectionString(env.DatabaseUrl!);
    }
    catch (FormatException e)
    {
        Log.Fatal("{Problem}", e.Message);
        return 1;
    }

    var builder = WebApplication.CreateBuilder(args);

    // Serilog is attached as a logging *provider* rather than through UseSerilog(), which replaces
    // ILoggerFactory outright and so silently discards every log filter. Without filtering,
    // ASP.NET Core narrates four Information lines per request -- and the container's health probe
    // is a request every thirty seconds, which is eleven thousand lines a day of nothing in the
    // stream section 4.4.1 calls "the application record". MODBOT_DEBUG_LOGGING restores them.
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger);

    if (!env.DebugLogging)
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

    builder.WebHost.UseUrls($"http://0.0.0.0:{env.Port}");

    builder.Services.AddSingleton(env);
    builder.Services.AddSingleton<IModbotClock>(clock);

    // What startup worked out about its surroundings, so the settings page reports what this
    // process is actually doing rather than re-deriving it and possibly disagreeing with it.
    builder.Services.AddSingleton(new DeploymentInfo(
        platform, persistence.Evidence, writeLogFiles, persistence.Explanation));

    builder.Services.AddDbContext<ModbotContext>(options => options
        .UseNpgsql(connectionString)
        // EF logs every statement it runs at Information, and reports both the missing
        // __EFMigrationsHistory table on a first boot and every connection attempt against a
        // database that is not up yet as an Error. All of it is correct for a developer and wrong
        // for the operator this log is written for: a successful first deploy should not open with
        // a red line, a retry that is about to succeed should not look like a failure, and the
        // application record should not be buried under SQL. Demoted to Debug, which
        // MODBOT_DEBUG_LOGGING turns back on. Nothing is lost -- whatever actually goes wrong is
        // reported a line later in Modbot's own words, by DatabaseMigrator.
        .ConfigureWarnings(warnings => warnings
            .Log(
                (RelationalEventId.CommandExecuted, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug),
                (RelationalEventId.ConnectionError, LogLevel.Debug))));

    // The key lives in the database and is created on first boot, so the protector cannot be
    // constructed until the schema exists. Resolution blocks once; the warm-up below makes that
    // once happen during startup rather than inside somebody's first request.
    builder.Services.AddSingleton<ISecretProtector>(services =>
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return AesGcmSecretProtector.CreateAsync(db).GetAwaiter().GetResult();
    });

    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>(
            DatabaseHealthCheck.Name,
            tags: [DatabaseHealthCheck.ReadyTag]);

    builder.Services.AddModbotAuth();

    // Persist the data protection key ring in Postgres. Without this the keys live in memory and
    // are regenerated on every start, so every redeploy or container restart silently signs out
    // every moderator -- on a platform that restarts routinely, that is not an edge case.
    builder.Services
        .AddDataProtection()
        .SetApplicationName("Modbot")
        .PersistKeysToDbContext<ModbotContext>();
    builder.Services.AddModbotAnalytics();

    // The gate and everything it refuses to work without (spec 4.1). Registered here rather than
    // inside Modbot.Api because composition is the host's job (spec 2.5), and registered
    // unconditionally because it reads its credentials from the database -- an unconfigured
    // deployment gets a gate in the Unconfigured state, which is exactly what the onboarding
    // wizard needs to be able to ask.
    builder.Services.AddModbotVRChat();

    // The producers. Until this line existed, the three maintenance services above -- partitions,
    // rollups, retention -- kept an empty fact log in perfect order, because nothing in Modbot
    // had ever written a fact.
    //
    // After AddModbotVRChat, because the audit-log and group-info jobs resolve IVRChatGate from
    // it and IFactWriter from AddModbotAnalytics. Safe on a fresh deployment: both read
    // Settings.ManagedGroupId, report NotConfigured and issue no requests at all until onboarding
    // has chosen a group.
    builder.Services.AddModbotVRChatSync();

    // Evidence storage (evidence design §6). Registered with its defaults, which means
    // EvidenceBackend.None: the store is built and tested but there is no settings screen to
    // configure it from yet, so a deployment has not chosen a backend and the upload pipeline
    // refuses rather than pretending. Wiring it now keeps the DI graph honest and means the
    // remaining work is binding Settings onto EvidenceOptions plus the endpoints.
    builder.Services.AddModbotEvidence();

    // The Postgres side of it (§7). Modbot.Evidence owns no migrations by design, so it declares
    // IEvidenceMetadata and the table lives here.
    builder.Services.AddScoped<IEvidenceMetadata, DatabaseEvidenceMetadata>();

    // The client and overlay surface (M3 §4, client protocol §3-§6): pairing, batched ingest,
    // overlay reads and the alert long poll.
    //
    // Registered by the host rather than inside AddModbotApi, because it depends on IFactWriter
    // from AddModbotAnalytics and AddModbotApi cannot guarantee that ordering -- a caller that
    // wires the API without the analytics substrate gets endpoints whose parameters cannot be
    // resolved, and minimal APIs report that by throwing while mapping routes, taking every
    // other endpoint in the host down with it. Composition is the host's job (spec 2.5).
    builder.Services.AddClientApi();

    builder.Services.AddModbotApi();

    var app = builder.Build();

    // Spec 8.2: migrations run here, before the first request, because a self-hosted appliance
    // must not require the operator to run a migration command. The partition maintainer starts
    // with the rest of the hosted services immediately afterwards, so the fact log has somewhere
    // to write from the first fact.
    if (await DatabaseMigrator.ApplyAsync(app.Services, connectionString, Log.Logger) is { } failure)
    {
        Log.Fatal("{Problem}", failure);
        return 1;
    }

    _ = app.Services.GetRequiredService<ISecretProtector>();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapModbotHealthChecks();
    app.MapModbotApi();

    // Mapped by the host for the same reason it is registered here: these endpoints resolve
    // IFactWriter, and a caller that maps the API without the analytics substrate would get a
    // route-mapping exception rather than a missing endpoint.
    app.MapClientApi();

    // The OpenAPI document is generated from the endpoints, so it cannot drift from what is
    // actually served. Scalar renders it for humans; the raw JSON feeds client generation.
    app.MapOpenApi("/api/openapi/{documentName}.json");
    app.MapScalarApiReference("/api/reference", options => options
        .WithTitle("Modbot API")
        .WithOpenApiRoutePattern("/api/openapi/{documentName}.json"));

    // The SPA is built into wwwroot by the Modbot.Web Vite build.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Client-side routing needs every non-API path to return the app shell. Without this a
    // refresh on /setup -- which is where spec 7.1 sends a fresh deployment, so it is the very
    // first URL anybody sees -- returns a 404 from the static file middleware.
    app.MapFallbackToFile("index.html");

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
