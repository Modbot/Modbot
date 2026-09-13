namespace Modbot.Evidence.Storage;

/// <summary>
/// The only place a key is spelled. One layout across all three backends (design section 5.1).
/// </summary>
/// <remarks>
/// Every key this produces is built from an <see cref="EvidenceHash"/> or an
/// <see cref="EvidenceUploadId"/>, both of which can only hold hex. There is no overload that takes
/// a caller's string, which is the point: a traversal bug would need a new method before it could
/// need a mitigation.
/// </remarks>
public static class EvidenceKeys
{
    /// <summary>Prefix for committed objects.</summary>
    public const string ObjectPrefix = "sha256/";

    /// <summary>Prefix for objects that have been transferred but not yet committed.</summary>
    public const string StagingPrefix = "staging/";

    /// <summary>
    /// The one key in the store that is not a hash (design section 8.2).
    /// </summary>
    /// <remarks>
    /// It lives outside <see cref="ObjectPrefix"/> so that section 5.1's "every key under sha256/
    /// is hex" property stays literally true and a sweep can tell Modbot's own bookkeeping from an
    /// object at a glance.
    /// </remarks>
    public const string SentinelKey = ".modbot-store";

    /// <summary>
    /// <c>sha256/ab/cd/abcdef…</c> — two levels of shard, then the full digest.
    /// </summary>
    /// <remarks>
    /// The shard keeps directory entry counts sane on filesystems that care and costs nothing on
    /// those that do not; object stores ignore it entirely. The full hex appears in the leaf as
    /// well as in the shards so that a key is self-describing when somebody is looking at a bucket
    /// listing at two in the morning.
    /// </remarks>
    public static string ForObject(EvidenceHash hash)
    {
        var hex = hash.Hex;
        return $"{ObjectPrefix}{hex[..2]}/{hex[2..4]}/{hex}";
    }

    /// <summary><c>staging/&lt;upload id&gt;</c>. Stable across retries of the same upload.</summary>
    public static string ForStaging(EvidenceUploadId uploadId) => StagingPrefix + uploadId.Value;

    /// <summary>
    /// Reads an object key back into a hash, for a sweep walking a store it did not write.
    /// </summary>
    public static bool TryReadObjectKey(string key, out EvidenceHash hash)
    {
        hash = default;
        if (key is null || !key.StartsWith(ObjectPrefix, StringComparison.Ordinal))
            return false;

        var rest = key.AsSpan(ObjectPrefix.Length);

        // "ab/cd/" then the digest.
        if (rest.Length != 6 + EvidenceHash.HexLength || rest[2] != '/' || rest[5] != '/')
            return false;

        var hex = rest[6..];
        if (!hex.StartsWith(rest[..2], StringComparison.Ordinal) ||
            !hex[2..].StartsWith(rest[3..5], StringComparison.Ordinal))
        {
            return false;
        }

        return EvidenceHash.TryParse(hex.ToString(), out hash);
    }
}
