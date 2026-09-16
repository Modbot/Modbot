using System.Security.Cryptography;
using System.Text;

namespace Modbot.Cloud.Auth;

/// <summary>
/// The key my.modbot.co and the landing page send, from <c>PROXY_API_KEY</c>.
/// </summary>
/// <remarks>
/// <para>
/// A second secret rather than <c>ROOT_API_KEY</c> reused, and its own type rather than a flag on
/// one, because it is <em>handed to another service</em>. A key that lives in another deployment's
/// environment must not also open <c>/admin</c>, and the compiler is what keeps the two apart: an
/// endpoint asks for the key it means, and there is no way to satisfy one with the other.
/// </para>
/// <para>
/// It opens the narrow read and save endpoints under <c>/api/v1/site</c>, which serve exactly what
/// the selector page and the landing page need and cannot list or search the registry.
/// </para>
/// </remarks>
public sealed class ProxyApiKey
{
    private readonly byte[]? _hash;

    public ProxyApiKey(string? key) =>
        _hash = string.IsNullOrEmpty(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public bool IsConfigured => _hash is not null;

    /// <summary>
    /// Compares hashes, so the comparison takes the same time whatever the length or content of the
    /// guess. With no key configured, nothing matches.
    /// </summary>
    public bool Matches(string? candidate)
    {
        if (_hash is null || string.IsNullOrEmpty(candidate))
            return false;

        return CryptographicOperations.FixedTimeEquals(_hash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
    }
}
