using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Auth;

/// <summary>
/// Cookie sessions and permission checks for Modbot's own staff accounts.
/// </summary>
/// <remarks>
/// <para>
/// Foundation spec 7.2. <see cref="PasswordHasher{TUser}"/> is registered standalone: Modbot does
/// not use ASP.NET Core Identity, which would bring a user store, role store, sign-in manager,
/// token providers and a two-factor stack for a table holding a dozen staff accounts.
/// </para>
/// <para>
/// The permission bitfield travels in the cookie as a claim. That makes authorisation a comparison
/// on an already-decrypted ticket rather than a database round trip per request -- at the cost
/// that a permission change takes effect on the user's next sign-in. That trade is right here
/// because staff permissions change a few times a year, and the alternative puts a query in front
/// of every request the SPA makes.
/// </para>
/// </remarks>
public static class ModbotAuth
{
    public const string CookieName = "modbot.session";

    /// <summary>The permission bitfield, as an invariant decimal string.</summary>
    public const string PermissionsClaim = "modbot:permissions";

    public static IServiceCollection AddModbotAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPasswordHasher<ModbotUser>, PasswordHasher<ModbotUser>>();
        services.AddScoped<UserAccountService>();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);

                // Modbot's HTTP surface is an API consumed by a SPA. The default cookie handler
                // answers an unauthenticated call with a 302 to a login page, which arrives at
                // fetch() as an opaque 200 of HTML. Status codes are the useful answer.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }

    /// <summary>Builds the principal that is sealed into the session cookie.</summary>
    public static ClaimsPrincipal CreatePrincipal(ModbotUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(
                    PermissionsClaim,
                    ((long)user.Permissions).ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>What the caller holds. <see cref="ModbotPermissions.None"/> when unauthenticated.</summary>
    public static ModbotPermissions PermissionsOf(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst(PermissionsClaim)?.Value;

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            ? (ModbotPermissions)bits
            : ModbotPermissions.None;
    }

    /// <summary>The signed-in account's id, or null.</summary>
    public static Guid? UserIdOf(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : null;
}
