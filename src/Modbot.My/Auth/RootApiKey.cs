using System.Security.Cryptography;
using System.Text;

namespace Modbot.My.Auth;

/// <summary>
/// The one secret that unlocks reading the registry, from <c>ROOT_API_KEY</c>.
/// </summary>
/// <remarks>
/// The registry is a list of Modbot deployments, which is a map of VRChat moderation servers
/// (central services spec 4.4). Nothing that reads it is ever public.
/// </remarks>
public sealed class RootApiKey
{
    private readonly byte[]? _hash;

    public RootApiKey(string? key) =>
        _hash = string.IsNullOrEmpty(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public bool IsConfigured => _hash is not null;

    /// <summary>
    /// Compares hashes, so the comparison takes the same time whatever the length or content of the
    /// guess. With no key configured, nothing matches.
    /// </summary>
    public bool Matches(string? candidate)
    {
        if (_hash is null || string.IsNullOrEmpty(candidate))
            return false;

        return CryptographicOperations.FixedTimeEquals(_hash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
    }
}

public static class RootApiKeyEndpoints
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Refuses the request unless it carries <c>Authorization: Bearer &lt;ROOT_API_KEY&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A missing key, a wrong key and a server with no key configured all get the same 401, so the
    /// response says nothing about which one it was.
    /// </remarks>
    public static TBuilder RequireRootApiKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var key = http.RequestServices.GetRequiredService<RootApiKey>();
            var header = http.Request.Headers.Authorization.ToString();

            var candidate = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? header[BearerPrefix.Length..].Trim()
                : null;

            if (key.Matches(candidate))
                return await next(context);

            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Json(new { error = "A valid root API key is required." }, statusCode: StatusCodes.Status401Unauthorized);
        });
}
