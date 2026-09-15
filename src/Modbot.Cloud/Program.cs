using Modbot.Cloud;
using Modbot.Cloud.Configuration;
using Modbot.Cloud.Data;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.Cloud — cloud.modbot.co.
//
// Composition only. Every endpoint lives in its feature folder under Features/, and CloudApp wires
// them together so the tests can build the same app over a test server.
//
// Two databases: DATABASE_URL for installs, sessions and settings; DATABASE_ENGINE_URL for the
// presence events clients back up (cloud event backup spec 4.1). Both are required.
// ─────────────────────────────────────────────────────────────────────────────

var environment = CloudEnvironment.Read();

if (environment.Problems() is { Count: > 0 } problems)
{
    foreach (var problem in problems)
        Console.Error.WriteLine(problem);

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
    Console.Error.WriteLine(e.Message);
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{environment.Port}");

CloudApp.AddServices(builder.Services, connectionString, engineConnectionString, environment.RootApiKey);

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

CloudApp.MapEndpoints(app);

await app.RunAsync();
return 0;
