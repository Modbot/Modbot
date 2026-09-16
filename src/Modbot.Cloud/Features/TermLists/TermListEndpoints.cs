using Microsoft.AspNetCore.Mvc;

namespace Modbot.Cloud.Features.TermLists;

/// <summary>
/// The curated term lists, served as plain JSON to anyone.
/// </summary>
/// <remarks>
/// <para>
/// Moved here from my.modbot.co on 2026-09-16, with the routes and the file shapes unchanged, so a
/// subscriber pointed at Cloud sees exactly what it saw before. my.modbot.co keeps the same three
/// routes as permanent redirects to these.
/// </para>
/// <para>
/// Public, and needing no account: these are data a moderator's software needs in order to
/// moderate, and a sign-up in front of them would make a Modbot that cannot work until somebody
/// registers (Cloud accounts and registry spec 4).
/// </para>
/// </remarks>
public static class TermListEndpoints
{
    public static IEndpointRouteBuilder MapTermLists(this IEndpointRouteBuilder app)
    {
        app.MapGet("/termlists/index.json", ([FromServices] TermListCatalog catalog) =>
            catalog.Index is null ? Results.NotFound() : Results.Content(catalog.Index, "application/json"));

        app.MapGet("/termlists/_schema.json", ([FromServices] TermListCatalog catalog) =>
            catalog.Schema is null ? Results.NotFound() : Results.Content(catalog.Schema, "application/json"));

        app.MapGet("/termlists/{id}.json", ([FromRoute] string id, [FromServices] TermListCatalog catalog) =>
            catalog.Get(id) is { } body ? Results.Content(body, "application/json") : Results.NotFound());

        return app;
    }
}
