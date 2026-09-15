using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Modbot.Cloud.Auth;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Admin;

/// <summary>Signing in to <c>/admin</c> with <c>ROOT_API_KEY</c>, and signing out.</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/login", LoginAsync);
        app.MapPost("/api/admin/logout", LogoutAsync);
        app.MapGet("/api/admin/session", () => Results.Ok(new { signedIn = true })).RequireAdmin();

        return app;
    }

    internal static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest request,
        [FromServices] RootApiKey key,
        [FromServices] AdminSessions sessions,
        [FromServices] LoginAttempts attempts,
        [FromServices] CloudContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var ip = ClientAddress.From(http);

        // Checked before the key, so a blocked address learns nothing from a right guess.
        if (attempts.WaitFor(ip) is { } wait)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!key.Matches(request.Key))
        {
            attempts.Failed(ip);
            return Results.Json(new { error = "Wrong key." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        attempts.Succeeded(ip);

        var (token, expiresAt) = await sessions.StartAsync(db, ct);
        http.Response.Cookies.Append(AdminSessions.CookieName, token, AdminSessions.CookieOptions(expiresAt));

        return Results.NoContent();
    }

    internal static async Task<IResult> LogoutAsync(
        [FromServices] AdminSessions sessions,
        [FromServices] CloudContext db,
        HttpContext http,
        CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(AdminSessions.CookieName, out var token))
            await sessions.EndAsync(db, token, ct);

        http.Response.Cookies.Delete(AdminSessions.CookieName, AdminSessions.CookieOptions(DateTimeOffset.UnixEpoch));
        return Results.NoContent();
    }
}

public sealed record LoginRequest(string? Key);
