using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Modbot.Evidence.Storage;

/// <summary>
/// The SHA-256 of an object's bytes, which is also its name in every store.
/// </summary>
/// <remarks>
/// <para>
/// A type rather than a <c>byte[]</c> or a <c>string</c> because design section 5.1's strongest
/// claim rests on it: <em>"there is no user-supplied filename anywhere in a path, so path traversal
/// is not mitigated, it is impossible by construction"</em>. A value that cannot exist unless it is
/// exactly thirty-two bytes, and that renders as exactly sixty-four lowercase hex characters, is
/// what makes that a property of the type system rather than a rule somebody has to remember at
/// every call site. No <c>..</c>, no null byte, no Unicode normalisation surprise, and no
/// case-folding collision on a filesystem that folds case.
/// </para>
/// <para>
/// The original filename travels as metadata and is displayed. It is never part of a key.
/// </para>
/// </remarks>
public readonly struct EvidenceHash : IEquatable<EvidenceHash>
{
    /// <summary>SHA-256 is 32 bytes. Nothing else is accepted.</summary>
    public const int ByteLength = 32;

    /// <summary>…and 64 characters of lowercase hex.</summary>
    public const int HexLength = ByteLength * 2;

    private readonly byte[]? _bytes;

    private EvidenceHash(byte[] bytes) => _bytes = bytes;

    /// <summary>Whether this is a real hash rather than a defaulted struct.</summary>
    public bool IsEmpty => _bytes is null;

    /// <summary>The raw digest. A copy, so a caller cannot reach in and change a key.</summary>
    public byte[] ToArray()
        => _bytes is null ? throw new InvalidOperationException("Uninitialised hash.") : (byte[])_bytes.Clone();

    /// <summary>Lowercase hex, which is the only form that ever appears in a key.</summary>
    public string Hex => _bytes is null
        ? throw new InvalidOperationException("Uninitialised hash.")
        : Convert.ToHexStringLower(_bytes);

    public static EvidenceHash FromBytes(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != ByteLength)
            throw new ArgumentException($"A SHA-256 digest is {ByteLength} bytes, not {digest.Length}.", nameof(digest));

        return new EvidenceHash(digest.ToArray());
    }

    /// <summary>Hashes a span outright. For sentinels, canaries and tests — never for evidence.</summary>
    /// <remarks>
    /// Evidence is hashed while it streams (<see cref="CountingHashStream"/>), because
    /// buffering a hundred megabytes to hash it is the thing design section 9.3 forbids.
    /// </remarks>
    public static EvidenceHash Compute(ReadOnlySpan<byte> content) => new(SHA256.HashData(content).ToArray());

    public static EvidenceHash Parse(string hex)
        => TryParse(hex, out var hash)
            ? hash
            : throw new FormatException("An evidence hash is 64 lowercase hex characters.");

    /// <summary>
    /// Strict on purpose: lowercase only. Two spellings of one hash would be two keys on a
    /// case-sensitive store and one key on a case-folding one, which is a deduplication bug that
    /// only appears on somebody else's filesystem.
    /// </summary>
    public static bool TryParse(string? hex, out EvidenceHash hash)
    {
        hash = default;
        if (hex is null || hex.Length != HexLength)
            return false;

        foreach (var c in hex)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        hash = new EvidenceHash(Convert.FromHexString(hex));
        return true;
    }

    public bool Equals(EvidenceHash other)
    {
        if (_bytes is null || other._bytes is null)
            return _bytes is null && other._bytes is null;

        // Not a secret, but constant-time comparison costs nothing here and keeps the habit.
        return CryptographicOperations.FixedTimeEquals(_bytes, other._bytes);
    }

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is EvidenceHash other && Equals(other);

    public override int GetHashCode()
        => _bytes is null ? 0 : BitConverter.ToInt32(_bytes, 0);

    public override string ToString() => _bytes is null ? "(none)" : Hex;

    public static bool operator ==(EvidenceHash left, EvidenceHash right) => left.Equals(right);

    public static bool operator !=(EvidenceHash left, EvidenceHash right) => !left.Equals(right);
}
