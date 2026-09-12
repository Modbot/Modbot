using Modbot.My;
using Modbot.My.Registry;

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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TermListCatalog>();
builder.Services.AddSingleton<IInstanceRegistry, InMemoryInstanceRegistry>();
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

// The selector is one static page handling three routes client-side. Serve the
// same file for each so a deep link works on first load, not only after
// navigating from the root.
var selectorPage = File.ReadAllText(Path.Combine(app.Environment.WebRootPath, "index.html"));
app.MapGet("/instanceredirect", () => Results.Content(selectorPage, "text/html; charset=utf-8"));

// The register page records the instance URL server-side as a BACKUP registry, so deployments
// that never self-register (analytics off, or simply never called) are still counted. It captures
// the URL and nothing else -- no analytics, no group, no operator.
app.MapGet("/register", async (string? modbotInstanceUrl, IInstanceRegistry registry, CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(modbotInstanceUrl)
        && Uri.TryCreate(modbotInstanceUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps)
    {
        await registry.NoteRegisterPageVisitAsync(uri.GetLeftPart(UriPartial.Authority), ct);
    }

    return Results.Content(selectorPage, "text/html; charset=utf-8");
});

// ── Instance registry ────────────────────────────────────────────────────────
// Deployments register themselves so the project can count them and know which
// versions are live. Central services spec section 4.
//
// There is deliberately NO endpoint that lists instances. A directory of Modbot
// deployments is a map of VRChat moderation infrastructure, which is precisely
// what someone probing for a group's moderation server would want (section 4.3).

app.MapPost("/api/instances/register", async (RegisterRequest req, IInstanceRegistry registry, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.InstanceId) || string.IsNullOrWhiteSpace(req.InstanceUrl))
        return Results.BadRequest(new { error = "instanceId and instanceUrl are required." });

    if (!Uri.TryCreate(req.InstanceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        return Results.BadRequest(new { error = "instanceUrl must be an absolute https URL." });

    var record = await registry.RegisterAsync(req.InstanceId, req.InstanceUrl, req.Version, ct);
    return Results.Ok(new { registered = true, record.InstanceId, record.RegisteredAt });
});

app.MapPost("/api/instances/{instanceId}/usage", async (
    string instanceId, UsageReport report, IInstanceRegistry registry, CancellationToken ct) =>
    await registry.ReportUsageAsync(instanceId, report, ct)
        ? Results.Accepted()
        // An unregistered id is 404 rather than an implicit create: usage reporting
        // should never be the thing that enrols a deployment.
        : Results.NotFound(new { error = "Unknown instanceId. Register first." }));

// Aggregates only -- never the underlying rows.
app.MapGet("/api/stats", async (IInstanceRegistry registry, CancellationToken ct) =>
    Results.Ok(await registry.GetTotalsAsync(ct)));

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

/// <param name="InstanceId">Random UUID the deployment assigned itself on first boot.</param>
internal sealed record RegisterRequest(string InstanceId, string InstanceUrl, string? Version);
