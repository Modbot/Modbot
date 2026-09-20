using Microsoft.AspNetCore.Mvc;

namespace Modbot.Landing.Features.Setup;

/// <summary>
/// <c>/get.sh</c> serves the install script and <c>/docker-compose.yml</c> serves the Compose file
/// it downloads. Both as plain text, so opening either in a browser shows it.
/// </summary>
public static class SetupEndpoints
{
    public const string ScriptPath = "/" + SetupFiles.ScriptFile;
    public const string ComposePath = "/" + SetupFiles.ComposeFile;

    /// <summary>
    /// The two are served from one build and change together, so neither may be cached longer than
    /// the other: a script pinned to a Compose file it no longer matches would set Modbot up wrong.
    /// </summary>
    public const string Cache = "no-cache";

    public static IEndpointRouteBuilder MapSetup(this IEndpointRouteBuilder app)
    {
        foreach (var file in SetupFiles.All)
        {
            // HEAD as well, because curl and wget check a URL before fetching it.
            app.MapMethods("/" + file, [HttpMethods.Get, HttpMethods.Head],
                ([FromServices] SetupFiles files, HttpContext http) => Serve(files, file, http));
        }

        return app;
    }

    internal static IResult Serve(SetupFiles files, string file, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.Response.Headers.CacheControl = Cache;

        // A '#' so that the body of a failed answer is a comment in both files' own languages, and
        // a person who piped this to sh without asking curl to fail on an error runs nothing.
        return files.Find(file) is { } text
            ? Text(text, StatusCodes.Status200OK)
            : Text($"# {file} has not been built.\n", StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Text(string text, int status) =>
        Results.Content(text, "text/plain; charset=utf-8", statusCode: status);
}
