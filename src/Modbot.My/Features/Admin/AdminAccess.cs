using Modbot.My.Auth;
using Modbot.My.Data;

namespace Modbot.My.Features.Admin;

/// <summary>
/// Everything that reads or changes the registry: an <c>/admin</c> session cookie, or
/// <c>Authorization: Bearer &lt;ROOT_API_KEY&gt;</c> for scripts.
/// </summary>
public static class AdminAccess
{
    private const string BearerPrefix = "Bearer ";

    /// <remarks>
    /// A missing key, a wrong key, a lapsed session and a server with no key configured all get the
    /// same 401, so the response says nothing about which it was.
    /// </remarks>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (await IsAdminAsync(http))
                return await next(context);

            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Json(new { error = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized);
        });

    internal static async Task<bool> IsAdminAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var key = services.GetRequiredService<RootApiKey>();

        // With no ROOT_API_KEY, admin is closed to everyone, sessions included.
        if (!key.IsConfigured)
            return false;

        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return key.Matches(header[BearerPrefix.Length..].Trim());

        if (!http.Request.Cookies.TryGetValue(AdminSessions.CookieName, out var token))
            return false;

        return await services.GetRequiredService<AdminSessions>()
            .IsValidAsync(services.GetRequiredService<MyContext>(), token, http.RequestAborted);
    }
}
