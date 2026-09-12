using Modbot.My;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot.My — my.modbot.co and Modbot Hub.
//
// Two jobs, both static by design:
//   1. The instance selector. A page that remembers which Modbot deployments you
//      use, in YOUR browser's localStorage. No backend, no database, no accounts.
//   2. Modbot Hub — serving curated term lists as plain files.
//
// Governing rule (central services spec section 1): Modbot must be completely
// functional with zero contact to this service, forever. Both jobs here have
// working manual alternatives, and that is a permanent constraint.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") is { } p && int.TryParse(p, out var parsed)
    ? parsed : 8080;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<TermListCatalog>();
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    // Term lists are public data fetched by self-hosted Modbot deployments at
    // arbitrary origins, so the read surface is open. There is nothing here to
    // protect: no accounts, no user data, nothing writable.
    .AllowAnyOrigin().AllowAnyHeader().WithMethods("GET")));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Ok());

// ── Modbot Hub ───────────────────────────────────────────────────────────────

app.MapGet("/termlists/index.json", (TermListCatalog catalog) =>
    catalog.Index is null
        ? Results.NotFound()
        : Results.Content(catalog.Index, "application/json"));

app.MapGet("/termlists/{id}.json", (string id, TermListCatalog catalog) =>
    catalog.Get(id) is { } body
        ? Results.Content(body, "application/json")
        : Results.NotFound());

app.MapGet("/termlists/_schema.json", (TermListCatalog catalog) =>
    catalog.Schema is null
        ? Results.NotFound()
        : Results.Content(catalog.Schema, "application/json"));

app.Run();
