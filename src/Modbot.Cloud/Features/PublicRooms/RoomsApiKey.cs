using System.Security.Cryptography;
using System.Text;
using Modbot.Cloud.Auth;

namespace Modbot.Cloud.Features.PublicRooms;

/// <summary>
/// The key that reads the public rooms feed, from <c>ROOMS_API_KEY</c>.
/// </summary>
/// <remarks>
/// <para>
/// A key of its own rather than <c>ROOT_API_KEY</c>, because the only thing that reads this feed is
/// the landing page — a public web server, on the open internet, which would otherwise be holding
/// the key to Cloud admin. This one reads group names, pictures and join links, and can do nothing
/// else.
/// </para>
/// <para>
/// <c>ROOT_API_KEY</c> is accepted too, so a maintainer with the admin key can read the feed
/// without a second secret. With neither key set, the feed refuses everyone: there is nothing to
/// compare against, and an open feed is not the default anybody should get by forgetting a
/// variable.
/// </para>
/// </remarks>
public sealed class RoomsApiKey
{
    private const string BearerPrefix = "Bearer ";

    private readonly byte[]? _hash;
    private readonly RootApiKey _root;

    public RoomsApiKey(string? key, RootApiKey root)
    {
        ArgumentNullException.ThrowIfNull(root);

        _hash = string.IsNullOrEmpty(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));
        _root = root;
    }

    public bool IsConfigured => _hash is not null || _root.IsConfigured;

    /// <summary>
    /// Reads <c>Authorization: Bearer &lt;key&gt;</c> and says whether it opens the feed. Compares
    /// hashes, so the comparison takes the same time whatever the guess.
    /// </summary>
    public bool Allows(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = authorization[BearerPrefix.Length..].Trim();

        if (candidate.Length == 0)
            return false;

        var mine = _hash is not null
            && CryptographicOperations.FixedTimeEquals(_hash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));

        return mine || _root.Matches(candidate);
    }
}
