using Modbot.My;
using Modbot.My.Configuration;
using Modbot.My.Data;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.My — my.modbot.co and Modbot Hub.
//
// Composition only. Every endpoint lives in its feature folder under Features/, and MyApp wires
// them together so the tests can build the same app over a test server.
//
// Governing rule (central services spec section 1): Modbot must be completely functional with zero
// contact to this service, forever.
// ─────────────────────────────────────────────────────────────────────────────

var environment = MyEnvironment.Read();

if (environment.DatabaseUrl is null)
{
    Console.Error.WriteLine(
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
    Console.Error.WriteLine(e.Message);
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{environment.Port}");

MyApp.AddServices(builder.Services, connectionString, environment.RootApiKey);

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

MyApp.MapEndpoints(app);

await app.RunAsync();
return 0;
