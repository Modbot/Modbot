using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Installs;

namespace Modbot.Cloud.Features.PublicInstances;

/// <summary>What a Modbot server sends: its group, and every instance of that group anyone can join.</summary>
public sealed record PublicInstancesReportRequest(
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl,
    [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl,
    [property: JsonPropertyName("instances")] IReadOnlyList<PublicInstanceRequest>? Instances,
    /// <summary>The field's name before 2026-09-17. Read so an older Modbot's report still lands.</summary>
    [property: JsonPropertyName("rooms")] IReadOnlyList<PublicInstanceRequest>? Rooms = null);

/// <inheritdoc cref="PublicInstancesReportRequest"/>
public sealed record PublicInstanceRequest(
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("worldId")] string? WorldId,
    [property: JsonPropertyName("worldName")] string? WorldName,
    [property: JsonPropertyName("worldImageUrl")] string? WorldImageUrl,
    [property: JsonPropertyName("joinLink")] string? JoinLink,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("openedAt")] DateTimeOffset? OpenedAt);

/// <summary>One group on the feed, with the instances it has open.</summary>
public sealed record PublicInstancesGroupView(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl,
    [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl,
    [property: JsonPropertyName("groupUrl")] string GroupUrl,
    [property: JsonPropertyName("reportedAt")] DateTimeOffset ReportedAt,
    [property: JsonPropertyName("instances")] IReadOnlyList<PublicInstanceView> Instances);

/// <inheritdoc cref="PublicInstancesGroupView"/>
public sealed record PublicInstanceView(
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("worldId")] string WorldId,
    [property: JsonPropertyName("worldName")] string? WorldName,
    [property: JsonPropertyName("worldImageUrl")] string? WorldImageUrl,
    [property: JsonPropertyName("joinLink")] string? JoinLink,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("openedAt")] DateTimeOffset OpenedAt);

/// <summary>The whole feed.</summary>
public sealed record PublicInstancesFeed(
    [property: JsonPropertyName("groups")] IReadOnlyList<PublicInstancesGroupView> Groups);

/// <summary>The write limit for public instances reports, one per process.</summary>
public sealed class PublicInstancesLimit(TimeProvider time)
{
    /// <summary>A server reports every five minutes and on each change; this is far above that.</summary>
    public const int ReportsPerHour = 240;

    /// <summary>New servers from one address in an hour. Enough for a household, not enough to fill a table.</summary>
    public const int NewServersPerHour = 5;

    public WindowLimit Reports { get; } = new(ReportsPerHour, TimeSpan.FromHours(1), time);

    public WindowLimit NewServers { get; } = new(NewServersPerHour, TimeSpan.FromHours(1), time);
}

/// <summary>
/// The instances a group has open to everyone: reported by each group's own Modbot, read by modbot.co.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the one part of Cloud meant to end up on a public page</strong>, and the payload
/// is shaped so that it can be. There is no field for a head count, a member count, a person's name
/// or a person's id, so nothing here can name anybody. An instance that is limited to group members, or
/// to members and their friends, is filtered out by the reporting Modbot and never arrives.
/// </para>
/// <para>
/// This does not make the instance registry public (central services spec §4.4). A group is here
/// only because its operator left the setting on, and what is here is a group's name, its pictures
/// and a join link anyone in its Discord already has — not a deployment's address.
/// </para>
/// </remarks>
public static class PublicInstancesEndpoints
{
    /// <summary>How long after its last report a server's instances stop being served.</summary>
    /// <remarks>
    /// Four times the reporting interval. A Modbot that is switched off, redeployed or cut off from
    /// Cloud takes its instances off the page within this long without anybody doing anything.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);

    /// <summary>How long a silent server's rows are kept before they are deleted.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(7);

    /// <summary>The most groups one read returns.</summary>
    public const int MaxGroups = 500;

    /// <summary>The most instances one report may carry. Beyond this the report is refused.</summary>
    public const int MaxInstancesPerReport = 200;

    public static IEndpointRouteBuilder MapPublicInstances(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Both paths, because a Modbot server built before the word changed still reports to the
        // old one, and its group must not fall off modbot.co for being a version behind.
        foreach (var path in new[] { "/api/v1/public-instances", "/api/v1/public-rooms" })
        {
            app.MapPut(path, ReportAsync);
            app.MapDelete(path, StopAsync);
            app.MapGet(path, ReadAsync);
        }

        return app;
    }

    internal static async Task<IResult> ReportAsync(
        [FromBody] PublicInstancesReportRequest? request,
        [FromServices] CloudContext db,
        [FromServices] PublicInstancesLimit limits,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        if (!InstallSecrets.TryRead(http.Request.Headers.Authorization, out var serverId, out var secret))
            return Unauthorized();

        var groupId = ClientText.Clean(request?.GroupId, InstancesServer.MaxGroupIdLength);

        if (groupId is null)
            return CloudError.Result(StatusCodes.Status400BadRequest, "no_group", "The report names no group.");

        var instances = request?.Instances ?? request?.Rooms ?? [];

        if (instances.Count > MaxInstancesPerReport)
        {
            return CloudError.Result(
                StatusCodes.Status400BadRequest, "too_many_instances", "That is more instances than one group can have open.");
        }

        if (limits.Reports.TryTake(serverId.ToString("D")) is { } wait)
            return CloudError.TooMany(http, wait, "Too many reports from this server.");

        var now = time.GetUtcNow();
        var server = await db.InstancesServers.FirstOrDefaultAsync(s => s.Id == serverId, ct);

        if (server is null)
        {
            var ip = ClientAddress.From(http)?.ToString() ?? "unknown";
            if (limits.NewServers.TryTake(ip) is { } newWait)
                return CloudError.TooMany(http, newWait, "Too many new servers from this address.");

            // One group, one row. A group already being reported by a server that is still alive is
            // not taken over -- otherwise anybody could claim any group's listing by reporting it.
            // A row that has gone quiet is taken over, so a redeployed Modbot with a fresh database
            // gets its own group back rather than being locked out for a week.
            var claimed = await db.InstancesServers.FirstOrDefaultAsync(s => s.GroupId == groupId, ct);

            if (claimed is not null)
            {
                if (now - claimed.LastReportedAt < StaleAfter)
                {
                    return CloudError.Result(
                        StatusCodes.Status409Conflict,
                        "group_taken",
                        "Another Modbot is already reporting this group's instances.");
                }

                await ForgetServerAsync(db, claimed.Id, ct);
            }

            server = new InstancesServer
            {
                Id = serverId,
                SecretHash = InstallSecrets.Hash(secret),
                GroupId = groupId,
                FirstReportedAt = now,
            };

            db.InstancesServers.Add(server);
        }
        else if (!InstallSecrets.Matches(server.SecretHash, secret))
        {
            return Unauthorized();
        }
        else if (!string.Equals(server.GroupId, groupId, StringComparison.Ordinal))
        {
            // The same Modbot now manages a different group. Its old group's instances are not this
            // group's instances, so they go.
            var claimed = await db.InstancesServers
                .FirstOrDefaultAsync(s => s.GroupId == groupId && s.Id != serverId, ct);

            if (claimed is not null && now - claimed.LastReportedAt < StaleAfter)
            {
                return CloudError.Result(
                    StatusCodes.Status409Conflict,
                    "group_taken",
                    "Another Modbot is already reporting this group's instances.");
            }

            if (claimed is not null)
                await ForgetServerAsync(db, claimed.Id, ct);

            server.GroupId = groupId;
        }

        server.GroupName = ClientText.Clean(request?.GroupName, InstancesServer.MaxNameLength);
        server.GroupIconUrl = Picture(request?.GroupIconUrl);
        server.GroupBannerUrl = Picture(request?.GroupBannerUrl);
        server.LastReportedAt = now;

        await ReplaceInstancesAsync(db, server.Id, instances, now, ct);

        await db.SaveChangesAsync(ct);

        // Housekeeping on the write path: a server that stopped reporting a week ago is gone for
        // good, and this is the only request Cloud can count on happening.
        var forget = now - ForgetAfter;

        var silent = await db.InstancesServers
            .AsNoTracking()
            .Where(s => s.LastReportedAt < forget)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var id in silent)
            await ForgetServerAsync(db, id, ct);

        return Results.NoContent();
    }

    internal static async Task<IResult> StopAsync(
        [FromServices] CloudContext db,
        HttpContext http,
        CancellationToken ct)
    {
        if (!InstallSecrets.TryRead(http.Request.Headers.Authorization, out var serverId, out var secret))
            return Unauthorized();

        var server = await db.InstancesServers.FirstOrDefaultAsync(s => s.Id == serverId, ct);

        // Already gone is the outcome the caller asked for.
        if (server is null)
            return Results.NoContent();

        if (!InstallSecrets.Matches(server.SecretHash, secret))
            return Unauthorized();

        // Detached first: the row is deleted by the statement below, and a tracked copy would make
        // the next SaveChanges try to delete it a second time.
        db.Entry(server).State = EntityState.Detached;
        await ForgetServerAsync(db, serverId, ct);

        return Results.NoContent();
    }

    internal static async Task<IResult> ReadAsync(
        [FromServices] CloudContext db,
        [FromServices] InstancesApiKey key,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        if (!key.Allows(http.Request.Headers.Authorization))
        {
            return CloudError.Result(
                StatusCodes.Status401Unauthorized, "unauthorized", "This feed needs an API key.");
        }

        var fresh = time.GetUtcNow() - StaleAfter;

        var servers = await db.InstancesServers
            .AsNoTracking()
            .Where(s => s.LastReportedAt >= fresh)
            .OrderByDescending(s => s.LastReportedAt)
            .Take(MaxGroups)
            .ToListAsync(ct);

        var ids = servers.Select(s => s.Id).ToList();

        var instances = await db.PublicInstances
            .AsNoTracking()
            .Where(r => ids.Contains(r.ServerId))
            .OrderBy(r => r.OpenedAt)
            .ToListAsync(ct);

        var byServer = instances.GroupBy(r => r.ServerId).ToDictionary(g => g.Key, g => g.ToList());

        var groups = servers
            .Select(s => new PublicInstancesGroupView(
                s.GroupId,
                s.GroupName,
                s.GroupIconUrl,
                s.GroupBannerUrl,
                $"https://vrchat.com/home/group/{Uri.EscapeDataString(s.GroupId)}",
                s.LastReportedAt,
                byServer.GetValueOrDefault(s.Id, [])
                    .Select(r => new PublicInstanceView(
                        r.Location, r.WorldId, r.WorldName, r.WorldImageUrl, r.JoinLink, r.Region, r.OpenedAt))
                    .ToList()))
            .ToList();

        // Never cached by anything in between: the key is in the request, and a shared cache holding
        // the answer would serve it to somebody who did not send one.
        http.Response.Headers.CacheControl = "private, no-store";

        return Results.Ok(new PublicInstancesFeed(groups));
    }

    /// <summary>Deletes a server and its instances. There is no foreign key between the two tables.</summary>
    private static async Task ForgetServerAsync(CloudContext db, Guid serverId, CancellationToken ct)
    {
        await db.PublicInstances.Where(r => r.ServerId == serverId).ExecuteDeleteAsync(ct);
        await db.InstancesServers.Where(s => s.Id == serverId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Makes the stored instances the reported instances: what is there is updated, what is not is deleted.
    /// </summary>
    private static async Task ReplaceInstancesAsync(
        CloudContext db,
        Guid serverId,
        IReadOnlyList<PublicInstanceRequest> reported,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = await db.PublicInstances.Where(r => r.ServerId == serverId).ToListAsync(ct);
        var byLocation = existing.ToDictionary(r => r.Location, StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sent in reported)
        {
            var location = ClientText.Clean(sent.Location, PublicInstance.MaxLocationLength);
            var worldId = ClientText.Clean(sent.WorldId, PublicInstance.MaxLocationLength);

            if (location is null || worldId is null || !kept.Add(location))
                continue;

            if (!byLocation.TryGetValue(location, out var instance))
            {
                instance = new PublicInstance { Id = Guid.NewGuid(), ServerId = serverId, Location = location };
                db.PublicInstances.Add(instance);
            }

            instance.WorldId = worldId;
            instance.WorldName = ClientText.Clean(sent.WorldName, InstancesServer.MaxNameLength);
            instance.WorldImageUrl = Picture(sent.WorldImageUrl);
            instance.JoinLink = Link(sent.JoinLink);
            instance.Region = ClientText.Clean(sent.Region, PublicInstance.MaxRegionLength);
            instance.OpenedAt = sent.OpenedAt ?? now;
            instance.ReportedAt = now;
        }

        foreach (var instance in existing.Where(r => !kept.Contains(r.Location)))
            db.PublicInstances.Remove(instance);
    }

    /// <summary>
    /// An address, or null for anything that is not a plain <c>https</c> URL of a workable length.
    /// </summary>
    /// <remarks>
    /// Checked again here, however careful the reporting Modbot was. These values go into an
    /// <c>img src</c> and an <c>href</c> on a public page, and a <c>javascript:</c> that reached the
    /// table would be a scripting hole in every reader of the feed at once.
    /// </remarks>
    private static string? Picture(string? url) => Link(url);

    private static string? Link(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && parsed.AbsoluteUri.Length <= InstancesServer.MaxUrlLength
            ? parsed.AbsoluteUri
            : null;

    private static IResult Unauthorized() =>
        CloudError.Result(StatusCodes.Status401Unauthorized, "unauthorized", "That server id and secret were not accepted.");
}
