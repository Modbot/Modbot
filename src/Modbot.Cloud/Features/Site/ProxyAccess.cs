using Modbot.Cloud.Auth;

namespace Modbot.Cloud.Features.Site;

/// <summary>
/// Everything under <c>/api/v1/site</c>: <c>Authorization: Bearer &lt;PROXY_API_KEY&gt;</c>, and
/// nothing else.
/// </summary>
/// <remarks>
/// Not the admin session and not <c>ROOT_API_KEY</c>. A key that lives in another deployment's
/// environment must not also open <c>/admin</c>, and the only way to be sure of that is for the two
/// to be different secrets checked by different code.
/// </remarks>
public static class ProxyAccess
{
    private const string BearerPrefix = "Bearer ";

    public static TBuilder RequireProxyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var key = http.RequestServices.GetRequiredService<ProxyApiKey>();
            var header = http.Request.Headers.Authorization.ToString();

            // A missing key, a wrong key and a server with no key configured all get the same 401.
            if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                || !key.Matches(header[BearerPrefix.Length..].Trim()))
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { error = "Not allowed." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return await next(context);
        });
}
