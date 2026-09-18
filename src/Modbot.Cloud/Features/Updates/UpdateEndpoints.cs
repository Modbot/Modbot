using Microsoft.AspNetCore.Mvc;

namespace Modbot.Cloud.Features.Updates;

/// <summary>
/// What the newest release of each thing Modbot ships is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Unauthenticated, and nothing about the caller is required, recorded or logged</strong>
/// beyond the one request line every request gets. No install id, no deployment id, no version, no
/// account. The answer is the same for everybody, which is what lets one cached copy serve the
/// world and is also the property the client documentation has always promised about checking for
/// updates. Moving the check from GitHub to Cloud must not quietly change it.
/// </para>
/// <para>
/// <strong>This is not a Cloud feature.</strong> A Modbot with <c>MODBOT_CLOUD_DISABLED</c> set
/// still asks here, because knowing that a newer version exists is not reporting, analytics or
/// account linking — it is the thing every piece of software does. The server has its own switch
/// for an operator who wants no outbound calls at all.
/// </para>
/// <para>
/// <c>releases.{channel}.json</c> is the client's own feed, in the shape Velopack's web source
/// reads. It is the file the release workflow published, with each package's name replaced by the
/// address it downloads from, so the packages themselves never pass through Cloud.
/// </para>
/// </remarks>
public static class UpdateEndpoints
{
    /// <summary>
    /// Browsers and proxies may hold the answer for this long. Short against
    /// <see cref="LatestReleases.RefreshEvery"/>, so a deployment behind a cache still hears about
    /// a release the same day, and long enough that a retry loop somewhere cannot become traffic.
    /// </summary>
    public const int CacheSeconds = 300;

    public static IEndpointRouteBuilder MapUpdates(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/updates", async (
            [FromServices] LatestReleases releases,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await releases.ReadAsync(ct) is not { } answer)
                return NothingKnownYet();

            Cache(http);
            return Results.Ok(new { releases = answer.Releases, checkedAt = answer.At });
        });

        app.MapGet("/api/v1/updates/{name}", async (
            string name,
            [FromServices] LatestReleases releases,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await releases.ReadAsync(ct) is not { } answer)
                return NothingKnownYet();

            var release = answer.Releases
                .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

            if (release is null)
                return Results.NotFound(new { error = $"Modbot does not publish a \"{name}\"." });

            Cache(http);
            return Results.Ok(release);
        });

        // The client's update feed. The file name carries the channel, the way Velopack's web
        // source asks for it; its query string (arch, os, rid, the caller's own version) is
        // ignored, so nothing about the caller reaches the answer or the log.
        app.MapGet("/api/v1/updates/companion/{file}", async (
            string file,
            [FromServices] LatestReleases releases,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (LatestReleases.ChannelOf(file) is not { } channel)
                return Results.NotFound();

            if (await releases.ReadAsync(ct) is not { } answer)
                return NothingKnownYet();

            if (!answer.CompanionFeeds.TryGetValue(channel, out var feed))
                return Results.NotFound();

            Cache(http);
            return Results.Text(feed, "application/json");
        });

        return app;
    }

    /// <summary>
    /// A cold cache that could not be filled. An error rather than an empty list, because an empty
    /// list reads as "you are up to date" and that would be a lie.
    /// </summary>
    private static IResult NothingKnownYet() => Results.Json(
        new { error = "Modbot Cloud does not know the newest release yet." },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static void Cache(HttpContext http) =>
        http.Response.Headers.CacheControl = $"public, max-age={CacheSeconds}";
}
