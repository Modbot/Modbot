using System.Security.Cryptography;
using System.Text;

namespace Modbot.Api.Features.Client.Devices;

/// <summary>
/// How a device token and a pairing code are made, and how they are compared.
/// </summary>
/// <remarks>
/// <para><strong>The token is long and never displayed; the code is short and shown once.</strong>
/// That asymmetry is the design. A credential a human has to read out or retype ends up pasted
/// into a Discord message, so the thing the human handles is single-use and expires in minutes,
/// and the thing that lasts is never seen by anybody.</para>
/// <para><strong>Only hashes are stored.</strong> A Modbot database that leaks must not hand the
/// attacker working ingest credentials for every moderator. The tokens are high-entropy random
/// strings rather than passwords, so a single SHA-256 is the right primitive here and a password
/// hash would only be slower — there is no dictionary to run against 256 bits of randomness.</para>
/// <para><strong>Comparison is constant-time</strong>, so the time a rejection takes says nothing
/// about how much of the token was right.</para>
/// </remarks>
public static class DeviceTokens
{
    /// <summary>256 bits, base64url. Long enough that nobody will be tempted to retype it.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The pairing code a moderator types: short, unambiguous, and single-use.
    /// </summary>
    /// <remarks>
    /// The alphabet omits <c>0/O</c>, <c>1/I/L</c> and <c>U</c> — the first two because they are
    /// misread, and the last so no combination spells something a moderator has to read aloud in
    /// a voice call. Eight characters of it is about 38 bits, which is far too little to survive
    /// guessing on its own; what makes it safe is that it is single-use, expires in minutes, and
    /// is rate-limited, not its length.
    /// </remarks>
    public const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewPairingCode()
    {
        Span<char> code = stackalloc char[9];
        for (var i = 0; i < code.Length; i++)
        {
            // A dash in the middle, because people read and type grouped characters more reliably.
            code[i] = i == 4 ? '-' : CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(code);
    }

    /// <summary>What is stored. The secret itself never reaches the database.</summary>
    public static string Hash(string secret)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Whether a presented secret matches a stored hash, in time that does not depend on how much
    /// of it was right.
    /// </summary>
    public static bool Matches(string presented, string storedHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(presented)),
            Encoding.UTF8.GetBytes(storedHash));

    /// <summary>
    /// Normalises what a moderator typed: case, spacing, and the dash they may or may not have
    /// included. The code is for a person to transcribe, so transcription slips are not failures.
    /// </summary>
    public static string NormaliseCode(string typed)
    {
        var cleaned = new StringBuilder(typed.Length);
        foreach (var character in typed)
        {
            if (char.IsLetterOrDigit(character))
                cleaned.Append(char.ToUpperInvariant(character));
        }

        return cleaned.Length == 8
            ? $"{cleaned.ToString(0, 4)}-{cleaned.ToString(4, 4)}"
            : cleaned.ToString();
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
