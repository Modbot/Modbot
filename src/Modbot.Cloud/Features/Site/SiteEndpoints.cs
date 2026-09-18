using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Auth;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Registry;
using Npgsql;

namespace Modbot.Cloud.Features.Site;

/// <param name="Address">The visitor's address, as my.modbot.co worked it out. Null records no list.</param>
/// <param name="Url">The Modbot address that was opened.</param>
public sealed record RecordVisitRequest(
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("url")] string? Url);

/// <summary>
/// What my.modbot.co learned about a Modbot address by asking that address itself, on the register
/// page (register details spec 2).
/// </summary>
/// <remarks>
/// <strong>Never the other way round.</strong> The details are what the server answered, not what
/// the link that opened the page claimed; my.modbot.co asks before it sends anything here.
/// <see cref="OwnerEmail"/> is stored for the maintainer and is returned by nothing under
/// <c>/api/v1/site</c>.
/// </remarks>
public sealed record RecordServerRequest(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl,
    [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl,
    [property: JsonPropertyName("ownerEmail")] string? OwnerEmail);

/// <param name="GroupName">
/// The group: what a registered server reported, or failing that what a register visit learned by
/// asking the address. Null when neither knows.
/// </param>
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
        site.MapPost("/servers", RecordServerAsync);
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

        var rows = await db.VisitorInstances
            .AsNoTracking()
            .Where(v => v.IpAddress == ip && v.LastSeenAt >= since)
            .OrderByDescending(v => v.LastSeenAt)
            .ThenBy(v => v.InstanceUrl)
            .Take(InstanceVisits.HistoryLimit)
            .Select(v => new
            {
                v.InstanceUrl,
                v.FirstSeenAt,
                v.LastSeenAt,
                v.Visits,
                ReportedName = db.RegisteredServers
                    .Where(s => s.PublicAddress == v.InstanceUrl)
                    .OrderByDescending(s => s.LastSeenAt)
                    .Select(s => s.GroupName)
                    .FirstOrDefault(),
                ReportedIcon = db.RegisteredServers
                    .Where(s => s.PublicAddress == v.InstanceUrl)
                    .OrderByDescending(s => s.LastSeenAt)
                    .Select(s => s.GroupIconUrl)
                    .FirstOrDefault(),
                VisitedName = db.VisitedServers
                    .Where(s => s.InstanceUrl == v.InstanceUrl)
                    .Select(s => s.GroupName)
                    .FirstOrDefault(),
                VisitedIcon = db.VisitedServers
                    .Where(s => s.InstanceUrl == v.InstanceUrl)
                    .Select(s => s.GroupIconUrl)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // The registry first, field by field: what a server reported came from the server itself
        // with its secret, and what a visit learned came from asking an address a link named
        // (register details spec 3.2). Nothing here carries the owner's address.
        var items = rows
            .Select(r => new VisitedInstance(
                r.InstanceUrl,
                r.FirstSeenAt,
                r.LastSeenAt,
                r.Visits,
                r.ReportedName ?? r.VisitedName,
                r.ReportedIcon ?? r.VisitedIcon))
            .ToList();

        return Results.Ok(new VisitedInstances(items));
    }

    /// <summary>
    /// Saves what my.modbot.co learned about a Modbot address by asking it (register details spec 2).
    /// </summary>
    /// <remarks>
    /// Its own endpoint rather than a block on a visit, because the two are different facts with
    /// different rules: a visit is one page view by one visitor, counted once per five minutes and
    /// saved even when nothing else about the address is known, while this is what the address says
    /// about itself and is simply replaced by the newest answer (register details spec 3.1).
    /// </remarks>
    internal static async Task<IResult> RecordServerAsync(
        [FromBody] RecordServerRequest? request,
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (!InstanceUrl.TryNormalise(request?.Url, out var url))
            return Results.BadRequest(new { error = "url must be an absolute https URL." });

        var now = time.GetUtcNow();
        var row = await db.VisitedServers.FirstOrDefaultAsync(s => s.InstanceUrl == url, ct);

        if (row is null)
        {
            row = new VisitedServer { InstanceUrl = url, FirstSeenAt = now };
            db.VisitedServers.Add(row);
        }

        // Null leaves what is stored alone, the same rule a server's own report follows: a Modbot
        // that has not chosen a group yet must not blank out what an earlier visit learned.
        row.GroupId = ClientText.Clean(request?.GroupId, RegisteredServer.MaxGroupIdLength) ?? row.GroupId;
        row.GroupName = ClientText.Clean(request?.GroupName, RegisteredServer.MaxGroupNameLength) ?? row.GroupName;
        row.GroupIconUrl = PictureUrl(request?.GroupIconUrl) ?? row.GroupIconUrl;
        row.GroupBannerUrl = PictureUrl(request?.GroupBannerUrl) ?? row.GroupBannerUrl;
        row.OwnerEmail = EmailAddress.TryNormalise(request?.OwnerEmail, out var email) ? email : row.OwnerEmail;
        row.LastSeenAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The two saves of one page view arrived together. The other one stored the same answer,
            // so this one has nothing to add.
            db.ChangeTracker.Clear();
        }

        return Results.NoContent();
    }

    /// <summary>A group's picture, which VRChat serves over https. Anything else is left out.</summary>
    private static string? PictureUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsoluteUri.Length <= RegisteredServer.MaxUrlLength
            ? uri.AbsoluteUri
            : null;

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
