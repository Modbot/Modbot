using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Security;

/// <summary>AES-256-GCM. Layout: [12-byte nonce][16-byte tag][ciphertext], base64-encoded.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;

    private AesGcmSecretProtector(byte[] key) => _key = key;

    /// <summary>
    /// Loads the stored key, generating and persisting one on first call.
    /// </summary>
    public static async Task<ISecretProtector> CreateAsync(ModbotContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stored = await context.ProtectorKeys.FirstOrDefaultAsync(k => k.Id == 1, ct);
        if (stored is null)
        {
            stored = new ProtectorKey { Id = 1, Key = RandomNumberGenerator.GetBytes(KeySize) };
            context.ProtectorKeys.Add(stored);
            await context.SaveChangesAsync(ct);
        }

        return new AesGcmSecretProtector(stored.Key);
    }

    /// <summary>An ephemeral protector for tests that do not need persistence.</summary>
    public static ISecretProtector ForTesting()
        => new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(KeySize));

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(
            nonce,
            plain,
            output.AsSpan(NonceSize + TagSize),
            output.AsSpan(NonceSize, TagSize));

        return Convert.ToBase64String(output);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (ciphertext is null)
            return null;

        var input = Convert.FromBase64String(ciphertext);
        if (input.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short to be valid.");

        var plain = new byte[input.Length - NonceSize - TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            input.AsSpan(0, NonceSize),
            input.AsSpan(NonceSize + TagSize),
            input.AsSpan(NonceSize, TagSize),
            plain);

        return Encoding.UTF8.GetString(plain);
    }
}
