using Microsoft.AspNetCore.Mvc;
using Modbot.My.Configuration;

namespace Modbot.My.Features.TermLists;

/// <summary>
/// The three term list routes, kept as permanent redirects to Modbot Cloud.
/// </summary>
/// <remarks>
/// <para>
/// The lists themselves moved to Cloud on 2026-09-16. These routes stay because things are already
/// pointed at them — an older Modbot, somebody's script — and a link that has been published should
/// not simply stop working.
/// </para>
/// <para>
/// A redirect rather than a proxy, so there is one copy of the lists in one place and a bug here can
/// never serve a stale list. <c>308</c> rather than <c>301</c> because it keeps the method and the
/// body, and every HTTP client Modbot uses follows one.
/// </para>
/// </remarks>
public static class TermListRedirects
{
    public static IEndpointRouteBuilder MapTermLists(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/termlists/index.json", ([FromServices] CloudAddress cloud) => To(cloud, "index.json"));
        app.MapGet("/termlists/_schema.json", ([FromServices] CloudAddress cloud) => To(cloud, "_schema.json"));

        app.MapGet("/termlists/{file}", ([FromRoute] string file, [FromServices] CloudAddress cloud) =>
            IsListFile(file) ? To(cloud, file) : Results.NotFound());

        return app;
    }

    /// <summary>
    /// A list file name is lower-case letters, digits and underscores with a <c>.json</c> tail.
    /// Nothing else reaches a redirect, so no path a caller invented can be pointed anywhere.
    /// </summary>
    internal static bool IsListFile(string? file) =>
        file is { Length: > 5 and <= 105 }
        && file.EndsWith(".json", StringComparison.Ordinal)
        && file[..^5].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static IResult To(CloudAddress cloud, string file) =>
        Results.Redirect(new Uri(cloud.Endpoint, $"termlists/{file}").ToString(), permanent: true, preserveMethod: true);
}
