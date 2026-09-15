using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Common;
using Modbot.My.Data;

namespace Modbot.My.Features.Visits;

/// <summary>
/// The app's own save after it renders, and the instances seen from the caller's IP address.
/// </summary>
public static class VisitEndpoints
{
    public static IEndpointRouteBuilder MapVisits(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/local-register", LocalRegisterAsync);

        // Open, and only ever about the address asking. Nothing in the request names another.
        app.MapGet("/api/my-instances", MyInstancesAsync);

        return app;
    }

    internal static async Task<IResult> LocalRegisterAsync(
        [FromBody] LocalRegisterRequest request,
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        if (!InstanceUrl.TryNormalise(request.Url, out var url))
            return Results.BadRequest(new { error = "url must be an absolute https URL." });

        await InstanceVisits.RecordAsync(db, ClientAddress.From(http), url, time.GetUtcNow(), ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> MyInstancesAsync(
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        var ip = ClientAddress.From(http)?.ToString();
        if (ip is null)
            return Results.Ok(new MyInstances([]));

        var since = time.GetUtcNow() - InstanceVisits.HistoryReach;

        var items = await db.VisitorInstances
            .AsNoTracking()
            .Where(v => v.IpAddress == ip && v.LastSeenAt >= since)
            .OrderByDescending(v => v.LastSeenAt)
            .ThenBy(v => v.InstanceUrl)
            .Take(InstanceVisits.HistoryLimit)
            .Select(v => new MyInstance(v.InstanceUrl, v.FirstSeenAt, v.LastSeenAt, v.Visits))
            .ToListAsync(ct);

        return Results.Ok(new MyInstances(items));
    }
}

public sealed record LocalRegisterRequest(string? Url);

public sealed record MyInstance(string InstanceUrl, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, int Visits);

public sealed record MyInstances(IReadOnlyList<MyInstance> Items);
