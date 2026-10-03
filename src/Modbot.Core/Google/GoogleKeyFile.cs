using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modbot.Core.Google;

/// <summary>
/// The parts of a Google service account's JSON key file that Modbot keeps (Google Calendar design
/// §3.1): who the account is, which key this is, and the private key itself.
/// </summary>
/// <remarks>
/// <para>
/// The rest of the file is dropped. In particular <c>token_uri</c> is never read: the token request
/// always goes to <see cref="GoogleSignIn.TokenAddress"/>, so a crafted file cannot make Modbot post
/// a signed token to an address of its choosing.
/// </para>
/// <para>
/// A class rather than a record, so that nothing prints the private key by accident: a record's
/// <c>ToString</c> lists every property.
/// </para>
/// </remarks>
public sealed class GoogleKeyFile
{
    /// <summary>The largest key file accepted. A real one is about 2.3 KB.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>What a file that is not a service account key gets.</summary>
    public const string NotAKeyFile = "This is not a Google key file.";

    private GoogleKeyFile(string clientEmail, string keyId, string? projectId, string privateKeyPem)
    {
        ClientEmail = clientEmail;
        KeyId = keyId;
        ProjectId = projectId;
        PrivateKeyPem = privateKeyPem;
    }

    /// <summary>The account's address (<c>client_email</c>): what the owner shares the calendar with.</summary>
    public string ClientEmail { get; }

    /// <summary>Which of the account's keys this is (<c>private_key_id</c>). Not secret.</summary>
    public string KeyId { get; }

    /// <summary>The Google Cloud project the account belongs to (<c>project_id</c>), when the file names one.</summary>
    public string? ProjectId { get; }

    /// <summary>The private key, PKCS#8 PEM (<c>-----BEGIN PRIVATE KEY-----</c>). Stored encrypted.</summary>
    public string PrivateKeyPem { get; }

    /// <inheritdoc />
    public override string ToString() => $"GoogleKeyFile({ClientEmail}, {KeyId})";

    /// <summary>
    /// Reads a key file's text. Null, with <paramref name="error"/> set, unless it is a service
    /// account key with an address, a key id and an RSA private key that loads.
    /// </summary>
    public static GoogleKeyFile? Parse(string? text, out string? error)
    {
        error = NotAKeyFile;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            error = "The key file is too big.";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (Text(root, "type") != "service_account")
                return null;

            var email = Text(root, "client_email")?.Trim();
            var keyId = Text(root, "private_key_id")?.Trim();
            var pem = Text(root, "private_key");
            var project = Text(root, "project_id")?.Trim();

            if (string.IsNullOrEmpty(email) || email.Length > 320 || !email.Contains('@', StringComparison.Ordinal))
                return null;

            if (string.IsNullOrEmpty(keyId) || keyId.Length > 128)
                return null;

            if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal))
                return null;

            // Loaded once here, so a key that cannot sign is refused on upload rather than at the
            // first Check.
            using (var rsa = RSA.Create())
                rsa.ImportFromPem(pem);

            error = null;
            return new GoogleKeyFile(email, keyId, string.IsNullOrEmpty(project) ? null : project, pem.Trim() + "\n");
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// The key Modbot signs in with, decrypted for the length of one call. Never stored as it is, and
/// never printed: <see cref="ToString"/> names the account only.
/// </summary>
public sealed class GoogleCredentials(string clientEmail, string keyId, string privateKeyPem)
{
    public string ClientEmail { get; } = clientEmail;

    public string KeyId { get; } = keyId;

    public string PrivateKeyPem { get; } = privateKeyPem;

    /// <inheritdoc />
    public override string ToString() => $"GoogleCredentials({ClientEmail}, {KeyId})";
}
