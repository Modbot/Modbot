using Microsoft.AspNetCore.Mvc;

namespace Modbot.My.Features.TermLists;

/// <summary>Modbot Hub: the curated term lists, served as plain JSON to anyone.</summary>
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
