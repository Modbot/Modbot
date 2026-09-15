using System.Security.Cryptography;
using System.Text;

namespace Modbot.Cloud.Auth;

/// <summary>
/// The one secret that unlocks Cloud admin, from <c>ROOT_API_KEY</c>.
/// </summary>
/// <remarks>
/// Admin reads backed-up presence events, which carry other players' ids, names and where they
/// were (cloud event backup spec 10). Nothing that reads them is ever public. Scripts send the
/// key as <c>Authorization: Bearer</c>; people sign in to <c>/admin</c> with it (see
/// <c>Features/Admin</c>).
/// </remarks>
public sealed class RootApiKey
{
    private readonly byte[]? _hash;

    public RootApiKey(string? key) =>
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

    /// <summary>
    /// A key for one purpose, made from <c>ROOT_API_KEY</c>, or null when none is set. A different
    /// <c>ROOT_API_KEY</c> makes a different key, so anything signed with the old one stops working.
    /// </summary>
    public byte[]? DeriveKey(string purpose) =>
        _hash is null ? null : HMACSHA256.HashData(_hash, Encoding.UTF8.GetBytes(purpose));
}
