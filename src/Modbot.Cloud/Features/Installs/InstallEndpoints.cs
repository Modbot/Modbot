using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Installs;

/// <param name="ClientVersion">The client's release, for the admin list.</param>
/// <param name="Platform">Always <c>windows</c> today.</param>
public sealed record RegisterInstallRequest(
    [property: JsonPropertyName("clientVersion")] string? ClientVersion,
    [property: JsonPropertyName("platform")] string? Platform);

/// <param name="Secret">Shown once, here. Cloud keeps only its hash.</param>
/// <param name="ServerTime">Cloud's clock, so the client's first batch can already be corrected.</param>
public sealed record RegisterInstallResponse(
    [property: JsonPropertyName("installId")] Guid InstallId,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime);

/// <summary>The registration limit, one per process. Registration is the one unauthenticated write.</summary>
public sealed class RegistrationLimit(TimeProvider time)
{
    /// <summary>Enough for a household or a school behind one address; not enough to fill a table.</summary>
    public const int PerHour = 10;

    public WindowLimit Limit { get; } = new(PerHour, TimeSpan.FromHours(1), time);
}

/// <summary>
/// <c>POST /api/v1/installs</c>: a companion gets an install id and a secret.
/// </summary>
/// <remarks>
/// <para>
/// Unauthenticated, because the client has nothing yet. Limited to
/// <see cref="RegistrationLimit.PerHour"/> an hour per IP address. The address is used for the
/// limit and not stored (cloud event backup spec 3.2).
/// </para>
/// <para>
/// Takes the client's version and platform and nothing else: no machine name, no account, no
/// VRChat id.
/// </para>
/// </remarks>
public static class InstallEndpoints
{
    public static IEndpointRouteBuilder MapInstalls(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/installs", RegisterAsync);
        return app;
    }

    internal static async Task<IResult> RegisterAsync(
        [FromBody] RegisterInstallRequest? request,
        [FromServices] CloudContext db,
        [FromServices] RegistrationLimit limit,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        var ip = ClientAddress.From(http)?.ToString() ?? "unknown";
        if (limit.Limit.TryTake(ip) is { } wait)
            return CloudError.TooMany(http, wait, "Too many registrations from this address.");

        var now = time.GetUtcNow();
        var secret = InstallSecrets.NewSecret();
        var install = new Install
        {
            Id = Guid.NewGuid(),
            SecretHash = InstallSecrets.Hash(secret),
            ClientVersion = ClientText.Clean(request?.ClientVersion, Install.MaxVersionLength) ?? "unknown",
            Platform = ClientText.Clean(request?.Platform, Install.MaxPlatformLength) ?? "unknown",
            RegisteredAt = now,
            LastSeenAt = now,
        };

        db.Installs.Add(install);
        await db.SaveChangesAsync(ct);

        return Results.Json(new RegisterInstallResponse(install.Id, secret, now), statusCode: StatusCodes.Status201Created);
    }
}
