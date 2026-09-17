using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Auth;

/// <summary>Making, recognising and hashing API keys (API keys design §3.1).</summary>
public static class ApiKeySecrets
{
    /// <summary>Every key starts with this, which is how a request is recognised as carrying one.</summary>
    public const string Prefix = "mbk_";

    /// <summary>How much of a key is stored in the clear, prefix included.</summary>
    public const int StartLength = 12;

    public static string NewKey() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lowercase hex SHA-256 of the whole key, as stored.</summary>
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public static string StartOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key[..Math.Min(StartLength, key.Length)];
    }

    public static bool LooksLikeKey(string? token)
        => token is { Length: > 16 } && token.StartsWith(Prefix, StringComparison.Ordinal);

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What a key may do on a request: its own permissions, capped by its account's (§3.2).</summary>
public static class ApiKeyPermissions
{
    /// <remarks>
    /// Administrator on the account means the key's own set stands. Administrator on the key alone
    /// means "everything the account has" -- which is less than everything once the account has
    /// lost Administrator. Otherwise the key may do what both hold.
    /// </remarks>
    public static ModbotPermissions Cap(ModbotPermissions key, ModbotPermissions account)
    {
        if (account.HasFlag(ModbotPermissions.Administrator))
            return key;

        if (key.HasFlag(ModbotPermissions.Administrator))
            return account;

        return key & account;
    }
}

/// <summary>Who is calling, and what they may do right now.</summary>
/// <param name="UserId">The account -- for a key, the account that made it.</param>
/// <param name="ApiKeyId">The key, or null for a person's own session.</param>
/// <param name="Permissions">For a key, already capped by the account (§3.2).</param>
public sealed record ApiCaller(Guid UserId, string Username, Guid? ApiKeyId, ModbotPermissions Permissions);

/// <summary>
/// Resolves a key, a key id or an account to the caller it stands for, reading the database every
/// time so a revoked key or a disabled account stops working on the next request.
/// </summary>
public sealed class ApiCallers
{
    /// <summary>A key's last-used time is written at most this often (§3.5).</summary>
    public static readonly TimeSpan LastUsedEvery = TimeSpan.FromMinutes(1);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public ApiCallers(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>The caller a key stands for, or null. Records that the key was used.</summary>
    public async Task<ApiCaller?> ForKeyAsync(string key, CancellationToken ct)
    {
        if (!ApiKeySecrets.LooksLikeKey(key))
            return null;

        var hash = ApiKeySecrets.Hash(key);
        var row = await _db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash, ct);

        return row is null ? null : await ForKeyRowAsync(row, touch: true, ct);
    }

    public async Task<ApiCaller?> ForKeyIdAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
        return row is null ? null : await ForKeyRowAsync(row, touch: false, ct);
    }

    /// <summary>A person's own access: enabled, linked, and the union of their roles.</summary>
    public async Task<ApiCaller?> ForUserAsync(Guid userId, CancellationToken ct)
    {
        var account = await AccountAsync(userId, ct);
        return account is null ? null : new ApiCaller(account.Value.Id, account.Value.Username, null, account.Value.Permissions);
    }

    /// <summary>The caller behind an authenticated principal, read again now.</summary>
    public Task<ApiCaller?> ForPrincipalAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (ApiKeyAuthentication.KeyIdOf(principal) is { } keyId)
            return ForKeyIdAsync(keyId, ct);

        return ModbotAuth.UserIdOf(principal) is { } userId
            ? ForUserAsync(userId, ct)
            : Task.FromResult<ApiCaller?>(null);
    }

    private async Task<ApiCaller?> ForKeyRowAsync(ApiKey key, bool touch, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (!key.IsUsable(now))
            return null;

        var account = await AccountAsync(key.CreatedByUserId, ct);
        if (account is null)
            return null;

        if (touch && (key.LastUsedAt is null || now - key.LastUsedAt.Value >= LastUsedEvery))
        {
            await _db.ApiKeys
                .Where(k => k.Id == key.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedAt, now), ct);
        }

        return new ApiCaller(
            account.Value.Id,
            account.Value.Username,
            key.Id,
            ApiKeyPermissions.Cap(key.Permissions, account.Value.Permissions));
    }

    private async Task<(Guid Id, string Username, ModbotPermissions Permissions)?> AccountAsync(
        Guid id, CancellationToken ct)
    {
        var row = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.IsDisabled,
                Linked = u.VRChatUserId != null && u.VRChatUserId != "",
                Permissions = u.Roles.Select(r => r.Role.Permissions).ToList(),
            })
            .FirstOrDefaultAsync(ct);

        // An unlinked account can do nothing but finish linking (accounts and access §4.3), and a
        // program cannot link a VRChat account, so its keys do nothing at all.
        if (row is null || row.IsDisabled || !row.Linked)
            return null;

        return (row.Id, row.Username, ModbotRole.Union(row.Permissions));
    }
}

/// <summary>
/// The <c>Authorization: Bearer mbk_…</c> door onto the existing API (API keys design §3.4).
/// </summary>
/// <remarks>
/// <para>
/// A forwarding scheme is the default: a request carrying a key goes to
/// <see cref="ApiKeyAuthenticationHandler"/>, anything else to the cookie handler exactly as
/// before. The key handler builds the same claims a session carries, so <c>RequiresFlag</c>, the
/// default policy and every hand-written permission check work unchanged.
/// </para>
/// </remarks>
public static class ApiKeyAuthentication
{
    public const string Scheme = "ApiKey";

    /// <summary>The key a principal authenticated with. Absent on a session.</summary>
    public const string KeyIdClaim = "modbot:api_key_id";

    /// <summary>The bearer token on the request, or null.</summary>
    public static string? BearerOf(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>Whether the request carries something shaped like an API key.</summary>
    public static bool Carries(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ApiKeySecrets.LooksLikeKey(BearerOf(context.Request));
    }

    public static Guid? KeyIdOf(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(KeyIdClaim)?.Value, out var id) ? id : null;

    /// <summary>
    /// The endpoints that belong to a person, not a program (§3.3). A key sent to one of these is
    /// refused whatever permissions it carries.
    /// </summary>
    /// <remarks>
    /// <c>/api/auth</c> is a person's own account -- a key must not be able to change its owner's
    /// password or sign them out -- except "whose key is this". <c>/api/onboarding</c> is the setup
    /// wizard. Pairing codes are a moderator enrolling their own machine. The companion's
    /// routes take device tokens, and the two are never interchangeable.
    /// </remarks>
    public static bool KeysMayNotUse(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.Path;

        if (path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase))
        {
            var me = path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)
                     && HttpMethods.IsGet(request.Method);
            return !me;
        }

        if (path.StartsWithSegments("/api/onboarding", StringComparison.OrdinalIgnoreCase))
            return true;

        // Pairing a companion is a moderator's own decision on their own machine (M3), and a
        // key minting pairing codes would turn one credential into another.
        if (path.StartsWithSegments("/api/companion-devices/pairing-code", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsClientPath(path);
    }

    /// <summary><c>/api/v{number}/companion…</c>.</summary>
    private static bool IsClientPath(PathString path)
    {
        var segments = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length >= 3
            && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Length > 1
            && (segments[1][0] is 'v' or 'V')
            && int.TryParse(segments[1].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && string.Equals(segments[2], "companion", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The principal for a key: a session's claims, for the key's account, plus the key's id.</summary>
    public static ClaimsPrincipal CreatePrincipal(ApiCaller caller, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, caller.UserId.ToString()),
                new Claim(ClaimTypes.Name, caller.Username),
                new Claim(ModbotAuth.PermissionsClaim, ((long)caller.Permissions).ToString(CultureInfo.InvariantCulture)),
                new Claim(ModbotAuth.VRChatLinkedClaim, "1"),
                new Claim(ModbotAuth.SignedInAtClaim, now.ToString("o", CultureInfo.InvariantCulture)),
                new Claim(KeyIdClaim, caller.ApiKeyId?.ToString() ?? string.Empty),
            ],
            Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }
}

internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ApiKeyAuthentication.BearerOf(Request);
        if (!ApiKeySecrets.LooksLikeKey(token))
            return AuthenticateResult.NoResult();

        // One sentence for every refusal: whether a key exists, was revoked or expired is not
        // something to tell whoever is holding it.
        if (ApiKeyAuthentication.KeysMayNotUse(Request))
            return AuthenticateResult.Fail("API keys cannot be used here.");

        var callers = Context.RequestServices.GetRequiredService<ApiCallers>();
        var caller = await callers.ForKeyAsync(token!, Context.RequestAborted);

        if (caller is null)
            return AuthenticateResult.Fail("The API key is not valid.");

        var clock = Context.RequestServices.GetRequiredService<IModbotClock>();
        var principal = ApiKeyAuthentication.CreatePrincipal(caller, clock.UtcNow);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
