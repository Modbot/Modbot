using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modbot.Core.Configuration;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Auth;

/// <summary>
/// Serves every request as the demo administrator.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the demo's entire security model.</strong> There is no sign-in page, no session
/// and no account to hold: anybody who opens the site is an administrator with every permission, and
/// a demo deployment must therefore never hold real data about real people (demo mode design §3).
/// </para>
/// <para>
/// It is reachable only through <see cref="ModbotAuth.DefaultScheme"/>'s forwarder, which picks it
/// only while <see cref="DemoMode.IsOn"/> — and that is true only when <c>MODBOT_DEMO</c> was set
/// <em>and</em> the deployment was never set up for real. The handler asks the same question again
/// itself, through <see cref="DemoAuthentication.MayServeEveryoneAsAdministrator"/>, and returns
/// nobody when the answer is no — so a mistake in the forwarder is a request with no session rather
/// than a stranger with every permission.
/// </para>
/// </remarks>
internal sealed class DemoAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly DemoMode _demo;
    private readonly IModbotClock _clock;

    public DemoAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DemoMode demo,
        IModbotClock clock)
        : base(options, logger, encoder)
    {
        _demo = demo;
        _clock = clock;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!DemoAuthentication.MayServeEveryoneAsAdministrator(_demo))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = ModbotAuth.CreatePrincipal(
            DemoMode.AdministratorId,
            DemoMode.AdministratorUsername,
            ModbotPermissions.Administrator,
            vrchatLinked: true,
            _clock.UtcNow);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, DemoAuthentication.Scheme)));
    }
}

/// <summary>The demo sign-in scheme, and the single check that guards it.</summary>
public static class DemoAuthentication
{
    public const string Scheme = "Demo";

    /// <summary>
    /// Whether every visitor may be served as an administrator. The only answer that matters.
    /// </summary>
    /// <remarks>
    /// One method, called from one place, so there is exactly one line in Modbot that can decide
    /// to stop checking who somebody is. <see cref="DemoMode.Decided"/> is part of it: a demo that
    /// has not been decided yet is not a demo, so a request that somehow arrives before startup
    /// finished gets nothing rather than everything.
    /// </remarks>
    public static bool MayServeEveryoneAsAdministrator(DemoMode? demo)
        => demo is { Decided: true } && demo.IsOn;
}

/// <summary>
/// Refuses anything the demo cannot do: sign in, send mail, pair a client, or reach VRChat.
/// </summary>
/// <remarks>
/// The outward-facing half is handled at its own chokepoints — the VRChat gate, the mail relay and
/// the Discord messenger are all replaced in a demo, so nothing anywhere can reach either service.
/// What is left is the handful of paths that exist to get a person <em>into</em> a deployment, which
/// a demo has no use for and should not pretend to offer (demo mode design §3.2).
/// </remarks>
public static class DemoRefusals
{
    /// <summary>What the refused endpoints answer with.</summary>
    public const string Message = "Off in the demo.";

    private static readonly string[] Paths =
    [
        "/api/auth/login",
        "/api/auth/logout",
        "/api/auth/forgot-password",
        "/api/auth/vrchat-link",
        "/api/invites",
        "/api/join",
        "/api/reset",
        "/api/client-devices/pairing-code",
        "/api/settings/email/test",
    ];

    /// <summary>True when this path is one a demo refuses.</summary>
    public static bool Refuses(PathString path)
    {
        var value = path.Value;

        if (string.IsNullOrEmpty(value))
            return false;

        foreach (var refused in Paths)
        {
            if (value.StartsWith(refused, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // A staff account's reset link, which would send mail.
        if (value.StartsWith("/api/users/", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("/reset-link", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Companion pairing. The rest of the client surface -- reporting presence, reading
        // alerts -- is left alone, so a client somebody points at a demo still works.
        return value.Contains("/client/pair", StringComparison.OrdinalIgnoreCase);
    }
}
