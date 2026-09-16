using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Auth;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Registry;

namespace Modbot.Cloud.Features.Site;

/// <param name="Address">The visitor's address, as my.modbot.co worked it out. Null records no list.</param>
/// <param name="Url">The Modbot address that was opened.</param>
public sealed record RecordVisitRequest(
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("url")] string? Url);

/// <param name="GroupName">The group, when a registered server reports this address. Null otherwise.</param>
public sealed record VisitedInstance(
    [property: JsonPropertyName("instanceUrl")] string InstanceUrl,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("visits")] int Visits,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl);

public sealed record VisitedInstances(
    [property: JsonPropertyName("items")] IReadOnlyList<VisitedInstance> Items);

/// <param name="Registered">Modbot servers that registered themselves.</param>
/// <param name="ActiveLast30Days">Of those, seen in the last 30 days.</param>
public sealed record SiteCounts(
    [property: JsonPropertyName("registered")] int Registered,
    [property: JsonPropertyName("activeLast30Days")] int ActiveLast30Days);

/// <summary>
/// What my.modbot.co and the landing page are allowed to read and save
/// (Cloud accounts and registry spec 3.5).
/// </summary>
/// <remarks>
/// <para>
/// Exactly what those two pages need and nothing else. <strong>Nothing here lists servers and
/// nothing here takes a search term</strong>, because a list of every Modbot deployment is a map of
/// VRChat moderation infrastructure (central services spec 4.4).
/// </para>
/// <para>
/// The visitor's address comes in the request rather than from the connection: my.modbot.co is the
/// caller, and its own address is not the visitor's. It works the visitor's address out by the rule
/// in central services spec 2.3.1 and passes it on.
/// </para>
/// </remarks>
public static class SiteEndpoints
{
    public static IEndpointRouteBuilder MapSite(this IEndpointRouteBuilder app)
    {
        var site = app.MapGroup("/api/v1/site").RequireProxyKey();

        site.MapPost("/visits", RecordVisitAsync);
        site.MapGet("/visits", VisitsAsync);
        site.MapGet("/counts", CountsAsync);

        return app;
    }

    internal static async Task<IResult> RecordVisitAsync(
        [FromBody] RecordVisitRequest? request,
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (!InstanceUrl.TryNormalise(request?.Url, out var url))
            return Results.BadRequest(new { error = "url must be an absolute https URL." });

        await InstanceVisits.RecordAsync(db, Address(request?.Address), url, time.GetUtcNow(), ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> VisitsAsync(
        [FromQuery] string? address,
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (Address(address) is not { } ip)
            return Results.Ok(new VisitedInstances([]));

        var since = time.GetUtcNow() - InstanceVisits.HistoryReach;

        var items = await db.VisitorInstances
            .AsNoTracking()
            .Where(v => v.IpAddress == ip && v.LastSeenAt >= since)
            .OrderByDescending(v => v.LastSeenAt)
            .ThenBy(v => v.InstanceUrl)
            .Take(InstanceVisits.HistoryLimit)
            .Select(v => new VisitedInstance(
                v.InstanceUrl,
                v.FirstSeenAt,
                v.LastSeenAt,
                v.Visits,
                db.RegisteredServers
                    .Where(s => s.PublicAddress == v.InstanceUrl)
                    .OrderByDescending(s => s.LastSeenAt)
                    .Select(s => s.GroupName)
                    .FirstOrDefault(),
                db.RegisteredServers
                    .Where(s => s.PublicAddress == v.InstanceUrl)
                    .OrderByDescending(s => s.LastSeenAt)
                    .Select(s => s.GroupIconUrl)
                    .FirstOrDefault()))
            .ToListAsync(ct);

        return Results.Ok(new VisitedInstances(items));
    }

    internal static async Task<IResult> CountsAsync(
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-30);

        return Results.Ok(new SiteCounts(
            await db.RegisteredServers.CountAsync(ct),
            await db.RegisteredServers.CountAsync(s => s.LastSeenAt >= since, ct)));
    }

    /// <summary>
    /// The address as text, or null when it is not one. Parsed and written back out so that one
    /// address is always stored one way, whatever shape the caller sent.
    /// </summary>
    private static string? Address(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > ClientAddress.MaxLength)
            return null;

        if (!IPAddress.TryParse(raw.Trim(), out var parsed))
            return null;

        return (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString();
    }
}
