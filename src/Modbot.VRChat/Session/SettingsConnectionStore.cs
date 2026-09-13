using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Security;

namespace Modbot.VRChat.Session;

/// <summary>
/// Reads the VRChat account out of the <c>settings</c> row, decrypting as it goes.
/// </summary>
/// <remarks>
/// <para>
/// Spec 2.6: configuration lives in the database, entered through the onboarding wizard, so
/// deploying Modbot is "click the template, open the URL, follow the wizard" rather than a page of
/// environment variables. Spec 4.1: the session cookie lives there too, never a file on disk.
/// </para>
/// <para>
/// A scope per read because the gate is a singleton and the context is scoped. Reads are rare —
/// once at startup and once per re-login — so there is nothing to cache and a cache would only
/// create a way for a settings change to be ignored.
/// </para>
/// </remarks>
public sealed class SettingsConnectionStore(IServiceScopeFactory scopes, ISecretProtector protector)
    : IVRChatConnectionStore
{
    public async Task<VRChatConnection> ReadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        var session = StoredSession.Parse(protector.Unprotect(settings.VRChatAuthCookieEncrypted));

        return new VRChatConnection(
            settings.VRChatUsername,
            protector.Unprotect(settings.VRChatPasswordEncrypted),
            protector.Unprotect(settings.VRChatTotpSecretEncrypted),
            session.Auth,
            session.TwoFactorAuth,
            settings.ProxyUrl,
            settings.ProxyUsername,
            protector.Unprotect(settings.ProxyPasswordEncrypted));
    }

    public async Task SaveSessionAsync(
        string? authCookie, string? twoFactorAuthCookie, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        settings.VRChatAuthCookieEncrypted = authCookie is null
            ? null
            : protector.Protect(StoredSession.Serialise(authCookie, twoFactorAuthCookie));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Both cookies in the one existing column.
    /// </summary>
    /// <remarks>
    /// A JSON object rather than a delimited pair: cookie values are opaque strings chosen by
    /// someone else, and a separator that turns up inside one would silently truncate a session.
    /// A value that is not JSON is read as a bare <c>auth</c> cookie, so a session written before
    /// this shape existed still works.
    /// </remarks>
    private sealed record StoredSession(string? Auth, string? TwoFactorAuth)
    {
        public static StoredSession Parse(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return new StoredSession(null, null);

            if (!stored.StartsWith('{'))
                return new StoredSession(stored, null);

            try
            {
                return JsonSerializer.Deserialize<StoredSession>(stored) ?? new StoredSession(null, null);
            }
            catch (JsonException)
            {
                // A corrupt session is a missing session, not a crash: the gate logs in again.
                return new StoredSession(null, null);
            }
        }

        public static string Serialise(string auth, string? twoFactorAuth) =>
            JsonSerializer.Serialize(new StoredSession(auth, twoFactorAuth));
    }
}
