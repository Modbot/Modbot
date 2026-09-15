using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;

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
/// The permission bitfield travels in the cookie as a claim, and <strong>every request checks the
/// account once</strong> (accounts and access design §5). Until 2026-09-13 there was no per-request
/// read, so a permission change waited for the next sign-in and disabling somebody did not end a
/// session already open. That trade was sized for a dozen staff whose permissions change a few
/// times a year, and the case it got wrong -- cutting off a moderator mid-incident -- is the one
/// that matters. The read is a primary-key lookup; the claims are refreshed from it when the
/// account has changed, so authorisation is still a comparison on the ticket.
/// </para>
/// <para>
/// <strong>The default authorisation policy requires the VRChat link</strong> (design §4.3). An
/// account that has not linked can reach only the endpoints mapped with
/// <see cref="SignedInPolicy"/>: who am I, the link itself, and signing out.
/// </para>
/// </remarks>
public static class ModbotAuth
{
    public const string CookieName = "modbot.session";

    /// <summary>The forwarding scheme that picks the key handler or the cookie handler per request.</summary>
    public const string DefaultScheme = "Modbot";

    /// <summary>The permission bitfield, as an invariant decimal string.</summary>
    public const string PermissionsClaim = "modbot:permissions";

    /// <summary>
    /// When this session started, from <see cref="IModbotClock"/>, round-trip formatted.
    /// </summary>
    /// <remarks>
    /// Compared against <see cref="ModbotUser.SessionsValidAfter"/> on every request. Modbot's
    /// own stamp rather than the cookie's <c>IssuedUtc</c> because the latter comes from the
    /// framework's clock, and a comparison between two clocks is not a comparison (spec 4.4).
    /// </remarks>
    public const string SignedInAtClaim = "modbot:signed_in_at";

    /// <summary>"1" once the person has linked their VRChat account, "0" until then.</summary>
    public const string VRChatLinkedClaim = "modbot:vrchat_linked";

    /// <summary>
    /// Signed in, linked or not. For the handful of endpoints an unlinked account must reach to
    /// finish linking. Everything else takes the default policy, which requires the link.
    /// </summary>
    public const string SignedInPolicy = "signed-in";

    public static IServiceCollection AddModbotAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPasswordHasher<ModbotUser>, PasswordHasher<ModbotUser>>();
        services.AddScoped<UserAccountService>();

        // The account slices' own services. Registered here rather than in AddModbotApi because
        // the login endpoint needs them and a host may map login without the rest of the API.
        // AccountFacts needs IFactWriter and EventPartitionMaintainer from AddModbotAnalytics;
        // a host that maps these endpoints must register those too.
        services.AddScoped<Features.Users.AccountFacts>();
        services.AddScoped<Features.Users.OneTimeLinkService>();
        services.AddScoped<Features.Users.ResetLinkDelivery>();
        services.AddSingleton<Features.Auth.Login.LoginSlowdown>();
        services.AddSingleton<Features.Auth.Login.ForgotPasswordSlowdown>();

        // The administrator's email is the User-Agent contact the gate sends VRChat. Plain Add,
        // not TryAdd, so it wins over the developer-contact fallback whichever is registered first.
        services.AddSingleton<AdministratorContact>();
        services.AddSingleton<IOperatorContact>(provider => provider.GetRequiredService<AdministratorContact>());

        // Fallbacks a host or test may leave in place: real waiting, SMTP from the settings row,
        // and a Discord messenger that says Discord is not set up. The host registers the real
        // Discord one before calling this.
        services.TryAddSingleton<IDelayScheduler, RealDelayScheduler>();
        services.TryAddScoped<IMailRelay, SmtpMailRelay>();
        services.TryAddScoped<IDiscordMessenger, NoDiscordMessenger>();

        // Every email goes through the daily limit (design §4.4). Plain Add, not TryAdd: nothing
        // registered earlier can put the relay in its place and skip the limit. A host or test
        // swaps the relay, never this.
        services.AddScoped<IEmailSender, EmailSender>();
        services.TryAddSingleton(new EmailQueueOptions());
        services.TryAddScoped<EmailQueuePass>();

        services.AddScoped<ApiCallers>();

        services
            .AddAuthentication(DefaultScheme)
            // API keys design §3.4: a request carrying a key goes to the key handler, everything
            // else to the cookie handler exactly as it did before keys existed. Sign-in and
            // sign-out name the cookie scheme explicitly, so they never pass through here.
            .AddPolicyScheme(DefaultScheme, DefaultScheme, options =>
                options.ForwardDefaultSelector = context => ApiKeyAuthentication.Carries(context)
                    ? ApiKeyAuthentication.Scheme
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthentication.Scheme, null)
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

                options.Events.OnValidatePrincipal = SessionCheck.RunAsync;
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(SignedInPolicy, policy => policy.RequireAuthenticatedUser());

            // RequireAuthorization() and RequiresFlag both fold this in, so the link is required
            // everywhere nothing says otherwise -- the direction a mistake should fail in.
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new VRChatLinkedRequirement())
                .Build();
        });

        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, VRChatLinkedHandler>();

        return services;
    }

    /// <summary>Builds the principal that is sealed into the session cookie.</summary>
    /// <param name="user">Must have its roles loaded; the permissions claim is their union.</param>
    /// <param name="signedInAt">Now, from <see cref="IModbotClock"/>.</param>
    public static ClaimsPrincipal CreatePrincipal(ModbotUser user, DateTimeOffset signedInAt)
    {
        ArgumentNullException.ThrowIfNull(user);

        return CreatePrincipal(
            user.Id, user.Username, user.EffectivePermissions, user.IsVRChatLinked, signedInAt);
    }

    public static ClaimsPrincipal CreatePrincipal(
        Guid id, string username, ModbotPermissions permissions, bool vrchatLinked, DateTimeOffset signedInAt)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id.ToString()),
                new Claim(ClaimTypes.Name, username),
                new Claim(PermissionsClaim, ((long)permissions).ToString(CultureInfo.InvariantCulture)),
                new Claim(VRChatLinkedClaim, vrchatLinked ? "1" : "0"),
                new Claim(SignedInAtClaim, signedInAt.ToString("o", CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>Starts a session for this account, stamped from the clock.</summary>
    public static Task SignInAsync(HttpContext http, ModbotUser user, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);

        return SignInAsync(http, user, clock.UtcNow);
    }

    /// <summary>
    /// Starts a session stamped with a specific instant. For re-issuing the current session after
    /// a password change at the same instant the cut-off was set, so it survives its own cut-off.
    /// </summary>
    public static Task SignInAsync(HttpContext http, ModbotUser user, DateTimeOffset signedInAt)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(user, signedInAt));
    }

    public static Task SignOutAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
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

    /// <summary>The signed-in account's username, for a record that names who asked.</summary>
    public static string? UsernameOf(ClaimsPrincipal? principal)
        => principal?.FindFirst(ClaimTypes.Name)?.Value is { Length: > 0 } name ? name : null;

    /// <summary>When the session started, or null for a cookie from before the claim existed.</summary>
    public static DateTimeOffset? SignedInAtOf(ClaimsPrincipal? principal)
        => DateTimeOffset.TryParse(
            principal?.FindFirst(SignedInAtClaim)?.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var at)
            ? at
            : null;

    public static bool IsVRChatLinked(ClaimsPrincipal? principal)
        => principal?.FindFirst(VRChatLinkedClaim)?.Value == "1";

    /// <summary>Whether the caller may do what <paramref name="required"/> asks, Administrator included.</summary>
    public static bool Allows(ModbotPermissions held, ModbotPermissions required)
        => held.HasFlag(ModbotPermissions.Administrator) || (held & required) == required;
}

/// <summary>
/// The per-request account read (design §5): is this session still good, and do its claims
/// still match the account.
/// </summary>
internal static class SessionCheck
{
    public static async Task RunAsync(CookieValidatePrincipalContext context)
    {
        var id = ModbotAuth.UserIdOf(context.Principal);
        var signedInAt = ModbotAuth.SignedInAtOf(context.Principal);

        // A cookie with no signed-in-at stamp predates this check. Ending it costs one sign-in;
        // honouring it would leave a session the cut-off cannot reach.
        if (id is null || signedInAt is null)
        {
            await RejectAsync(context);
            return;
        }

        var accounts = context.HttpContext.RequestServices.GetRequiredService<UserAccountService>();
        var state = await accounts.StateAsync(id.Value, context.HttpContext.RequestAborted);

        // Strictly before: a session re-issued at the very instant of a password change survives
        // it, which is what lets "change my password" keep the browser it was typed in.
        if (state is null || state.Value.IsDisabled
            || (state.Value.SessionsValidAfter is { } cutOff && signedInAt.Value < cutOff))
        {
            await RejectAsync(context);
            return;
        }

        var stale = state.Value.Permissions != ModbotAuth.PermissionsOf(context.Principal)
            || state.Value.VRChatLinked != ModbotAuth.IsVRChatLinked(context.Principal);

        if (stale)
        {
            context.ReplacePrincipal(ModbotAuth.CreatePrincipal(
                id.Value,
                context.Principal?.Identity?.Name ?? string.Empty,
                state.Value.Permissions,
                state.Value.VRChatLinked,
                signedInAt.Value));
            context.ShouldRenew = true;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

/// <summary>The person has linked their VRChat account (design §4.3).</summary>
internal sealed class VRChatLinkedRequirement : IAuthorizationRequirement;

internal sealed class VRChatLinkedHandler : AuthorizationHandler<VRChatLinkedRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, VRChatLinkedRequirement requirement)
    {
        if (ModbotAuth.IsVRChatLinked(context.User))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
