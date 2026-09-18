using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Proxy;

namespace Modbot.Api.Features.Proxy;

/// <summary>What a request to the proxy turned out to be.</summary>
public enum VRChatProxyCallerKind
{
    /// <summary>No credential Modbot recognises: no key, no session, no VRChat cookie. A 401.</summary>
    NotSignedIn,

    /// <summary>A Modbot account, without the permission or the VRChat link. A 403.</summary>
    Forbidden,

    /// <summary>A Modbot account that may use the proxy. The request goes out as the service account.</summary>
    ServiceAccount,

    /// <summary>
    /// Somebody's own VRChat session, in the cookie a VRChat client library sends. The request
    /// goes out with their cookie and nothing of Modbot's.
    /// </summary>
    OwnCookie,
}

/// <param name="Reason">For a refusal, the sentence to answer with.</param>
public sealed record VRChatProxyCaller(VRChatProxyCallerKind Kind, string? Reason = null)
{
    public VRChatProxyAccount Account => Kind == VRChatProxyCallerKind.OwnCookie
        ? VRChatProxyAccount.Caller
        : VRChatProxyAccount.Service;
}

/// <summary>
/// Who is calling the proxy, decided in the order the VRChat proxy design sets out.
/// </summary>
/// <remarks>
/// <para>
/// A Modbot key in the <c>Authorization</c> header, or the web app's session cookie, has
/// already been turned into a principal by the default scheme before the endpoint runs: the
/// route allows anonymous callers so that a VRChat cookie can be looked at, but authentication
/// still runs. A key that failed there is a bad key, and is answered as one rather than falling
/// through to the cookie.
/// </para>
/// <para>
/// Then the <c>auth</c> cookie, which is where a VRChat client library puts whatever it was
/// given as its session. A value shaped like a Modbot key <em>is</em> a Modbot key, and the
/// caller is whoever that key stands for, with the same cap and the same last-used stamp as a
/// key in the header. Anything else in that cookie is somebody's own VRChat session, and the
/// request goes out as them -- the cookie is never read, stored or logged, only forwarded.
/// </para>
/// </remarks>
public static class VRChatProxyCallers
{
    /// <summary>The cookie VRChat's own API reads its session from, and client libraries write it to.</summary>
    public const string VRChatAuthCookie = "auth";

    public const string NeedsPermission = "You do not have permission to use the VRChat proxy.";

    public const string NeedsLink = "Link your VRChat account first.";

    public static async Task<VRChatProxyCaller> ResolveAsync(HttpContext http, ApiCallers callers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(callers);

        if (http.User.Identity?.IsAuthenticated == true)
            return ForAccount(ModbotAuth.PermissionsOf(http.User), ModbotAuth.IsVRChatLinked(http.User));

        // A key in the header that the key handler refused. Not a reason to try the cookie:
        // whoever sent it meant it to be the credential.
        if (ApiKeyAuthentication.Carries(http))
            return new VRChatProxyCaller(VRChatProxyCallerKind.NotSignedIn);

        var auth = http.Request.Cookies[VRChatAuthCookie];
        if (string.IsNullOrEmpty(auth))
            return new VRChatProxyCaller(VRChatProxyCallerKind.NotSignedIn);

        if (!ApiKeySecrets.LooksLikeKey(auth))
            return new VRChatProxyCaller(VRChatProxyCallerKind.OwnCookie);

        var caller = await callers.ForKeyAsync(auth, ct);

        // One sentence for every refusal, as for a key in the header: whether the key exists,
        // expired or was revoked is not something to tell whoever is holding it.
        return caller is null
            ? new VRChatProxyCaller(VRChatProxyCallerKind.NotSignedIn)
            : ForAccount(caller.Permissions, linked: true);
    }

    /// <summary>The same rule for a key and a session: the link, then the permission.</summary>
    public static VRChatProxyCaller ForAccount(ModbotPermissions held, bool linked)
    {
        if (!linked)
            return new VRChatProxyCaller(VRChatProxyCallerKind.Forbidden, NeedsLink);

        return ModbotAuth.Allows(held, ModbotPermissions.UseVRChatProxy)
            ? new VRChatProxyCaller(VRChatProxyCallerKind.ServiceAccount)
            : new VRChatProxyCaller(VRChatProxyCallerKind.Forbidden, NeedsPermission);
    }
}
