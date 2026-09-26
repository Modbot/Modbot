using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Accounts;

namespace Modbot.Cloud.Features.Registry;

/// <summary>The limits on registering a server and on claiming one, one per process.</summary>
public sealed class RegistryLimits(TimeProvider time)
{
    /// <summary>Enough for a household or a school behind one address; not enough to fill a table.</summary>
    public const int RegistrationsPerHour = 10;

    /// <summary>A link code is 2^40 possibilities; ten guesses an hour gets nowhere.</summary>
    public const int ClaimsPerHour = 10;

    public WindowLimit Registrations { get; } = new(RegistrationsPerHour, TimeSpan.FromHours(1), time);

    public WindowLimit Claims { get; } = new(ClaimsPerHour, TimeSpan.FromHours(1), time);
}

/// <summary>
/// Modbot servers registering themselves and reporting, and accounts claiming them
/// (Cloud accounts and registry spec 3).
/// </summary>
public static class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistry(this IEndpointRouteBuilder app)
    {
        // Open: a server has nothing yet. Every later call carries its secret.
        app.MapPost("/api/v1/servers", RegisterAsync);

        app.MapPost("/api/v1/servers/report", ReportAsync).RequireServer();
        app.MapPost("/api/v1/servers/link-code", SetLinkCodeAsync).RequireServer();

        app.MapGet("/api/v1/servers/mine", (Delegate)MineAsync).RequireAccount();
        app.MapPost("/api/v1/servers/claim", ClaimAsync).RequireAccount();
        app.MapDelete("/api/v1/servers/{serverId:guid}/claim", UnclaimAsync).RequireAccount();

        return app;
    }

    internal static async Task<IResult> RegisterAsync(
        [FromBody] RegisterServerRequest? request,
        [FromServices] CloudContext db,
        [FromServices] RegistryLimits limits,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        var ip = Address(http);
        if (limits.Registrations.TryTake(ip) is { } wait)
            return CloudError.TooMany(http, wait, "Too many registrations from this address.");

        var now = time.GetUtcNow();
        var secret = ServerSecrets.NewSecret();

        var server = new RegisteredServer
        {
            Id = Guid.NewGuid(),
            SecretHash = ServerSecrets.Hash(secret),
            PublicAddress = ServerUrl.TryNormalise(request?.PublicAddress, out var origin) ? origin : null,
            Version = ClientText.Clean(request?.Version, RegisteredServer.MaxVersionLength),
            HostPlatform = ClientText.Clean(request?.HostPlatform, RegisteredServer.MaxPlatformLength),
            RegisteredAt = now,
            LastSeenAt = now,
            IpAddress = ip,
        };

        db.RegisteredServers.Add(server);
        await db.SaveChangesAsync(ct);

        return Results.Json(
            new RegisterServerResponse(server.Id, secret, now), statusCode: StatusCodes.Status201Created);
    }

    internal static async Task<IResult> ReportAsync(
        [FromBody] ServerReportRequest? request,
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        var server = await ServerAccess.RequiredAsync(http);
        var now = time.GetUtcNow();
        var ip = Address(http);

        // Null leaves what is stored alone: a server that has not finished setting up has no group
        // yet, and a later report that does have one must not be able to blank it out by omission.
        if (ServerUrl.TryNormalise(request?.PublicAddress, out var origin))
            server.PublicAddress = origin;

        server.Version = Clean(request?.Version, RegisteredServer.MaxVersionLength) ?? server.Version;
        server.HostPlatform = Clean(request?.HostPlatform, RegisteredServer.MaxPlatformLength) ?? server.HostPlatform;
        server.GroupId = Clean(request?.GroupId, RegisteredServer.MaxGroupIdLength) ?? server.GroupId;
        server.GroupName = Clean(request?.GroupName, RegisteredServer.MaxGroupNameLength) ?? server.GroupName;
        server.GroupDescription =
            Clean(request?.GroupDescription, RegisteredServer.MaxGroupDescriptionLength) ?? server.GroupDescription;
        server.GroupIconUrl = Url(request?.GroupIconUrl) ?? server.GroupIconUrl;
        server.GroupBannerUrl = Url(request?.GroupBannerUrl) ?? server.GroupBannerUrl;

        server.DiscordConnected = request?.DiscordConnected ?? server.DiscordConnected;
        server.TermListsImported = request?.TermListsImported?.Take(200).ToList() ?? server.TermListsImported;
        server.RateLimitColdStops = request?.RateLimitColdStops ?? server.RateLimitColdStops;
        server.WafBlocks = request?.WafBlocks ?? server.WafBlocks;
        server.AiModerationEnabled = request?.AiModerationEnabled ?? server.AiModerationEnabled;

        server.LastReportAt = now;
        server.LastSeenAt = now;
        server.IpAddress = ip;

        db.ServerReports.Add(new ServerReport
        {
            ServerId = server.Id,
            ReportedAt = now,
            PublicAddress = server.PublicAddress,
            Version = server.Version,
            HostPlatform = server.HostPlatform,
            GroupId = server.GroupId,
            GroupName = server.GroupName,
            DiscordConnected = server.DiscordConnected,
            TermListsImported = server.TermListsImported,
            RateLimitColdStops = server.RateLimitColdStops,
            WafBlocks = server.WafBlocks,
            AiModerationEnabled = server.AiModerationEnabled,
            IpAddress = ip,
        });

        await db.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    /// <summary>
    /// The server tells Cloud the hash of the code it is showing its owner.
    /// </summary>
    /// <remarks>
    /// The proof arrives on a connection the server itself opened, authenticated with the secret only
    /// that server holds. Cloud never calls a Modbot server back: an address a stranger supplied is
    /// not somewhere a service should make requests to.
    /// </remarks>
    internal static async Task<IResult> SetLinkCodeAsync(
        [FromBody] LinkCodeRequest? request,
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        var server = await ServerAccess.RequiredAsync(http);

        var hash = request?.CodeHash?.Trim().ToLowerInvariant();
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
            return Results.BadRequest(new { error = "codeHash must be a SHA-256 as 64 hex characters." });

        var now = time.GetUtcNow();

        // Any code somebody else's server happens to be waiting on, and any expired one, is cleared
        // first, so the unique index cannot refuse a legitimate code.
        await db.RegisteredServers
            .Where(s => s.LinkCodeHash != null && (s.LinkCodeHash == hash || s.LinkCodeExpiresAt <= now))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LinkCodeHash, (string?)null)
                                      .SetProperty(s => s.LinkCodeExpiresAt, (DateTimeOffset?)null), ct);

        server.LinkCodeHash = hash;
        server.LinkCodeExpiresAt = now + ServerSecrets.LinkCodeLifetime;
        server.LastSeenAt = now;

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> MineAsync(HttpContext http)
    {
        var account = await AccountAccess.RequiredAsync(http);
        var db = http.RequestServices.GetRequiredService<CloudContext>();

        var rows = await db.RegisteredServers
            .AsNoTracking()
            .Where(s => s.AccountId == account.Id)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(http.RequestAborted);

        return Results.Ok(new { items = rows.Select(ServerView.From).ToList() });
    }

    internal static async Task<IResult> ClaimAsync(
        [FromBody] ClaimServerRequest? request,
        [FromServices] CloudContext db,
        [FromServices] RegistryLimits limits,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        var account = await AccountAccess.RequiredAsync(http);

        // Per address and per account, so neither a busy network nor one signed-in person can work
        // through the code space.
        if (limits.Claims.TryTake(Address(http)) is { } byAddress)
            return CloudError.TooMany(http, byAddress, "Too many attempts. Try again later.");

        if (limits.Claims.TryTake(account.Id.ToString()) is { } byAccount)
            return CloudError.TooMany(http, byAccount, "Too many attempts. Try again later.");

        if (ServerSecrets.CleanLinkCode(request?.Code) is not { } code)
            return Results.BadRequest(new { error = "That is not a link code." });

        var now = time.GetUtcNow();
        var hash = ServerSecrets.Hash(code);

        var server = await db.RegisteredServers
            .FirstOrDefaultAsync(s => s.LinkCodeHash == hash && s.LinkCodeExpiresAt > now, ct);

        if (server is null)
            return Results.BadRequest(new { error = "That code is no longer good. Ask Modbot for a new one." });

        // Single use, whatever happens next.
        server.LinkCodeHash = null;
        server.LinkCodeExpiresAt = null;

        if (server.AccountId is { } held && held != account.Id)
        {
            await db.SaveChangesAsync(ct);
            return Results.Conflict(new { error = "Another account holds that server." });
        }

        server.AccountId = account.Id;
        server.ClaimedAt ??= now;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ServerView.From(server));
    }

    internal static async Task<IResult> UnclaimAsync(
        [FromRoute] Guid serverId,
        [FromServices] CloudContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var account = await AccountAccess.RequiredAsync(http);

        var updated = await db.RegisteredServers
            .Where(s => s.Id == serverId && s.AccountId == account.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.AccountId, (Guid?)null)
                                      .SetProperty(s => s.ClaimedAt, (DateTimeOffset?)null), ct);

        return updated == 0
            ? Results.NotFound(new { error = "You do not hold a server with that id." })
            : Results.NoContent();
    }

    private static string? Clean(string? value, int maxLength) => ClientText.Clean(value, maxLength);

    /// <summary>A group's picture, which VRChat serves over https. Anything else is left out.</summary>
    private static string? Url(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsoluteUri.Length <= RegisteredServer.MaxUrlLength
            ? uri.AbsoluteUri
            : null;

    private static string Address(HttpContext http) => ClientAddress.From(http)?.ToString() ?? "unknown";
}
