using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Modbot.Api.Features.DiscordLink;

/// <summary>A "Sign in with Discord" on its way: what the callback must see again.</summary>
public sealed record SignInAttempt(string State, string Verifier, DateTimeOffset CreatedAt);

/// <summary>
/// The link page's own session: who signed in with Discord, and a VRChat code handed out before
/// they did.
/// </summary>
public sealed record LinkSession(
    DateTimeOffset IssuedAt,
    string? DiscordUserId = null,
    string? DiscordUsername = null,
    string? PendingVRChatUserId = null,
    string? PendingCode = null,
    DateTimeOffset? PendingExpiresAt = null)
{
    public bool SignedIn => !string.IsNullOrEmpty(DiscordUserId);
}

/// <summary>
/// The two cookies the link page uses (Discord account linking design §3.2), encrypted with Data
/// Protection so their contents can be neither read nor changed in the browser.
/// </summary>
/// <remarks>
/// <para>
/// Both carry the time they were made, stamped from <c>IModbotClock</c> by the caller, and are
/// refused once older than their lifetime. The cookie's own expiry is set too, but only as
/// housekeeping: it comes from the machine clock, and a comparison between two clocks is not a
/// comparison (foundation §4.4).
/// </para>
/// <para>
/// <c>SameSite=Lax</c>, because the sign-in callback is a top-level navigation back from
/// discord.com and a Strict cookie would not be sent with it. Scoped to the link endpoints' path,
/// so no other request carries them.
/// </para>
/// </remarks>
public sealed class LinkCookies
{
    public const string SignInCookie = "modbot.link-signin";
    public const string SessionCookie = "modbot.link";
    public const string CookiePath = "/api/discord-link";

    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    private readonly IDataProtector _protector;

    public LinkCookies(IDataProtectionProvider protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _protector = protection.CreateProtector("Modbot.DiscordLink.v1");
    }

    public void WriteSignIn(HttpResponse response, SignInAttempt attempt)
        => Write(response, SignInCookie, attempt, SignInLifetime);

    /// <summary>The attempt, if the cookie is there, intact and young enough. Always deletes the cookie: an attempt is good once.</summary>
    public SignInAttempt? TakeSignIn(HttpContext http, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(http);

        var attempt = Read<SignInAttempt>(http.Request, SignInCookie);
        Delete(http.Response, SignInCookie);

        return attempt is not null && now - attempt.CreatedAt >= TimeSpan.Zero && now - attempt.CreatedAt < SignInLifetime
            ? attempt
            : null;
    }

    public void WriteSession(HttpResponse response, LinkSession session)
        => Write(response, SessionCookie, session, SessionLifetime);

    /// <summary>The session, or null when there is none or it is too old.</summary>
    public LinkSession? ReadSession(HttpRequest request, DateTimeOffset now)
    {
        var session = Read<LinkSession>(request, SessionCookie);

        return session is not null && now - session.IssuedAt >= TimeSpan.Zero && now - session.IssuedAt < SessionLifetime
            ? session
            : null;
    }

    public static void DeleteSession(HttpResponse response) => Delete(response, SessionCookie);

    /// <summary>Whether two values are equal, taking the same time whatever they hold.</summary>
    public static bool Same(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));

    private void Write<T>(HttpResponse response, string name, T value, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(name, _protector.Protect(JsonSerializer.Serialize(value)), Options(lifetime));
    }

    private T? Read<T>(HttpRequest request, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Cookies.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(_protector.Unprotect(raw));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            // Tampered with, from an old key, or not ours: as good as no cookie.
            return null;
        }
    }

    private static void Delete(HttpResponse response, string name)
        => response.Cookies.Delete(name, Options(TimeSpan.Zero));

    private static CookieOptions Options(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        MaxAge = lifetime > TimeSpan.Zero ? lifetime : null,
        IsEssential = true,
    };
}
