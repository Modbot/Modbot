using System.Net;
using System.Text.Json;
using File = System.IO.File;
using Serilog;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.Explore;

/// <summary>
/// Builds an authenticated <see cref="IVRChat"/>, reusing a cached session where possible.
/// </summary>
/// <remarks>
/// Session reuse is not an optimisation here, it is politeness. Without it every `dotnet run`
/// performs a full login, which hammers the auth endpoint and is exactly the behaviour the specs
/// spend §4.3 trying to avoid. Modbot itself persists the cookie in Settings for the same reason;
/// this is the scratch-tool equivalent.
/// </remarks>
public static class Session
{
    private sealed record Cached(string Auth, string TwoFactorAuth, DateTimeOffset SavedAt);

    private static readonly string CachePath =
        Path.Combine(AppContext.BaseDirectory, ".session.json");

    public static async Task<IVRChat?> CreateAsync(
        string email, string password, string? totpSecret, CancellationToken ct = default)
    {
        var cached = ReadCache();

        var builder = new VRChatClientBuilder()
            .WithUsername(email)
            .WithPassword(password)
            // Required -- VRChat rejects requests without a descriptive User-Agent.
            .WithApplication("ModbotExplore", "2026.9.0", "https://github.com/Modbot/Modbot");

        if (!string.IsNullOrWhiteSpace(totpSecret))
            builder = builder.WithTwoFactorSecret(totpSecret);

        if (cached is not null)
            builder = builder.WithAuthCookie(cached.Auth, cached.TwoFactorAuth);

        var vrchat = builder.Build();

        // With a cached cookie, verify it rather than assuming: an expired session otherwise
        // surfaces as a confusing 401 on whatever command runs next.
        if (cached is not null)
        {
            var probe = await vrchat.Authentication.GetCurrentUserWithHttpInfoAsync(ct);
            if ((int)probe.StatusCode is >= 200 and < 300)
            {
                Log.Information("Reused cached session (saved {Age:0.0}h ago)",
                    (DateTimeOffset.UtcNow - cached.SavedAt).TotalHours);
                return vrchat;
            }

            Log.Warning("Cached session rejected ({Status}); logging in again", (int)probe.StatusCode);
        }

        var result = string.IsNullOrWhiteSpace(totpSecret)
            ? await LoginInteractiveAsync(vrchat, ct)
            : await LoginWithTotpAsync(vrchat, ct);

        if (!result)
            return null;

        SaveCache(vrchat);
        return vrchat;
    }

    private static async Task<bool> LoginWithTotpAsync(IVRChat vrchat, CancellationToken ct)
    {
        // NOTE: we deliberately do NOT use TryLoginAsync. As of VRChat.API 2.20.9 it inverts its
        // own result:
        //
        //     return new VRChatLoginResult(user == null, null);
        //                                  ^^^^^^^^^^^^ this is the `success` parameter
        //
        // so a SUCCESSFUL login reports Success == false with a null Exception, which is
        // indistinguishable from a silent failure. LoginAsync throws on failure and returns the
        // user on success, which is unambiguous.
        try
        {
            var user = await vrchat.LoginAsync(ct);
            Log.Information("Logged in as {DisplayName} ({UserId})", user.DisplayName, user.Id);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Login failed");
            return false;
        }
    }

    /// <summary>
    /// No TOTP secret configured, so VRChat will demand a code interactively -- email OTP, or an
    /// authenticator app code typed by hand.
    /// </summary>
    private static async Task<bool> LoginInteractiveAsync(IVRChat vrchat, CancellationToken ct)
    {
        try
        {
            var user = await vrchat.LoginWithExternalCodeAsync(methods =>
            {
                Log.Information("VRChat requires two-factor auth. Offered methods: {Methods}",
                    string.Join(", ", methods));

                var isEmail = methods.Any(m => m.Contains("email", StringComparison.OrdinalIgnoreCase));
                Console.Write(isEmail ? "Email code: " : "Authenticator code: ");
                var code = Console.ReadLine()?.Trim() ?? string.Empty;

                return isEmail
                    ? new TwoFactorEmailCode(code)
                    : new TwoFactorAuthCode(code);
            }, ct);

            Log.Information("Logged in as {DisplayName}", user.DisplayName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Interactive login failed");
            return false;
        }
    }

    private static Cached? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return JsonSerializer.Deserialize<Cached>(File.ReadAllText(CachePath));
        }
        catch
        {
            return null; // a corrupt cache is a cache miss, not a crash
        }
    }

    private static void SaveCache(IVRChat vrchat)
    {
        try
        {
            var cookies = vrchat.GetCookies();
            var auth = Find(cookies, "auth");
            var twoFactor = Find(cookies, "twoFactorAuth");

            if (auth is null) return;

            // This file holds a live VRChat session cookie -- a credential. Default creation
            // permissions are world-readable on Linux (umask 022 -> 0644), so create it
            // owner-only. UnixCreateMode is not supported on Windows, where the user profile's
            // inherited ACL already restricts access.
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
            };

            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(CachePath, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(
                    new Cached(auth, twoFactor ?? string.Empty, DateTimeOffset.UtcNow)));
            }

            Log.Debug("Saved session to {Path}", CachePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not cache session; will log in again next run");
        }

        static string? Find(List<Cookie> cookies, string name) =>
            cookies.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}
