using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Modbot.My.Cloud;
using Modbot.My.Common;

namespace Modbot.My.Features.Visits;

public sealed record LocalRegisterRequest(string? Url);

/// <summary>
/// The app's own save after it renders, and the instances Cloud has seen from the caller's address.
/// </summary>
/// <remarks>
/// <para>
/// Both go straight to Modbot Cloud; my.modbot.co keeps nothing (central services spec 2.1.1). The
/// visitor's address is worked out here, by the rule in spec 2.3.1, and passed on — Cloud sees
/// my.modbot.co as its caller and cannot work it out for itself.
/// </para>
/// <para>
/// Both are limited per address, so that one address cannot make my.modbot.co hammer Cloud.
/// </para>
/// <para>
/// Both answer 503 when Cloud did not take part. A save is only a save once Cloud has it, and an
/// empty list is only the truth when Cloud said so; the app keeps the address to send later, and
/// the list it last had, until then.
/// </para>
/// </remarks>
public static class VisitEndpoints
{
    public static IEndpointRouteBuilder MapVisits(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/local-register", LocalRegisterAsync);

        // Open, and only ever about the address asking. Nothing in the request names another.
        app.MapGet("/api/my-instances", MyInstancesAsync);

        return app;
    }

    internal static async Task<IResult> LocalRegisterAsync(
        [FromBody] LocalRegisterRequest request,
        [FromServices] CloudClient cloud,
        [FromServices] ServerLookup lookup,
        [FromServices] SiteLimits limits,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!InstanceUrl.TryNormalise(request.Url, out var url))
            return Results.BadRequest(new { error = "url must be an absolute https URL." });

        var address = ClientAddress.From(http)?.ToString();

        if (limits.Saves.TryTake(address ?? "unknown") is { } wait)
            return TooMany(http, wait);

        var noted = await cloud.RecordVisitAsync(address, url, ct);

        // What the address says it is, asked here rather than believed from the link, and never a
        // reason to fail the save: an address with no group is what this has always stored
        // (register details spec 2.2). The page load usually asked already, and this reads that
        // answer rather than asking again.
        await lookup.SendDetailsAsync(url, ct);

        return noted ? Results.NoContent() : CloudUnavailable(http);
    }

    internal static async Task<IResult> MyInstancesAsync(
        [FromServices] CloudClient cloud,
        [FromServices] SiteLimits limits,
        HttpContext http,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        var address = ClientAddress.From(http)?.ToString();

        if (limits.Reads.TryTake(address ?? "unknown") is { } wait)
            return TooMany(http, wait);

        var instances = await cloud.KnownInstancesAsync(address, ct);
        return instances is null ? CloudUnavailable(http) : Results.Ok(instances);
    }

    /// <summary>Roughly when Cloud might be back. The app backs off on its own; this is for anything else.</summary>
    private const int CloudRetrySeconds = 30;

    private static IResult CloudUnavailable(HttpContext http)
    {
        http.Response.Headers.RetryAfter = CloudRetrySeconds.ToString(CultureInfo.InvariantCulture);

        return Results.Json(
            new { error = "Modbot Cloud could not be reached." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult TooMany(HttpContext http, TimeSpan wait)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return Results.Json(
            new { error = "Too many requests from this address." }, statusCode: StatusCodes.Status429TooManyRequests);
    }
}
