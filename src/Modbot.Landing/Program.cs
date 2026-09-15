using Modbot.Landing;
using Modbot.Landing.Configuration;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.Landing — modbot.co, the public page that says what Modbot is.
//
// Composition only. Every endpoint lives in its feature folder under Features/, and LandingApp
// wires them together so the tests build the same app over a test server. There is no database
// and nothing to configure beyond the port: the page is built once by Web/ into wwwroot.
// ─────────────────────────────────────────────────────────────────────────────

var environment = LandingEnvironment.Read();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{environment.Port}");

LandingApp.AddServices(builder.Services);

var app = builder.Build();

LandingApp.MapEndpoints(app);

await app.RunAsync();
