using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Pairing;

/// <summary>
/// Encrypts and decrypts one device token.
/// </summary>
/// <remarks>
/// An interface for exactly one reason: so the file format can be tested without DPAPI, and DPAPI
/// can be tested without the file. The shipping implementation is
/// <see cref="DpapiSecretProtector"/> and there is no configuration switch to replace it — a
/// plaintext option would be used by somebody, once, on a machine that mattered.
/// </remarks>
public interface IPairingSecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>What a stored secret is for. Every protector mixes it into the encryption.</summary>
/// <remarks>
/// A blob lifted out of the pairings file cannot be decrypted by a different program that happens
/// to run as the same user, and a device token never decrypts as a Cloud secret or the other way
/// round. Kept apart from any one protector because every platform's protector needs the same two.
/// </remarks>
public static class SecretPurposes
{
    public const string DeviceToken = "moe.bin.modbot.client.device-token.v1";

    public const string CloudSecret = "moe.bin.modbot.client.cloud-secret.v1";
}

/// <summary>
/// Windows DPAPI, <c>CurrentUser</c> scope.
/// </summary>
/// <remarks>
/// <para><strong>What this protects, and from whom.</strong> Device tokens are encrypted with a key
/// derived from the signed-in Windows account. Another account on the same PC — including an
/// administrator's — cannot decrypt them without first taking over this account. Copying the file
/// to a different machine yields nothing.</para>
/// <para><strong>It is not protection from the moderator.</strong> Anything running as this user
/// can decrypt these, which is the correct boundary: the tokens are the moderator's own, they are
/// ingest-scoped, and pretending otherwise would be theatre.</para>
/// <para><c>CurrentUser</c> rather than <c>LocalMachine</c> deliberately. <c>LocalMachine</c> would
/// let every account on a shared family PC read another's token.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : IPairingSecretProtector
{
    private readonly byte[] _entropy;

    public DpapiSecretProtector(string purpose = SecretPurposes.DeviceToken) => _entropy = Encoding.UTF8.GetBytes(purpose);

    public byte[] Protect(byte[] plaintext)
        => ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext)
        => ProtectedData.Unprotect(ciphertext, _entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// A random key in a file only this user can read, for the platforms that have no DPAPI.
/// </summary>
/// <remarks>
/// <para><strong>What this protects, and from whom.</strong> Device tokens are encrypted with a
/// 256-bit key that is made once and kept in Modbot's own folder under the user's profile, with
/// the file readable by that user alone (mode 0600, in a folder of mode 0700). Another account on
/// the same machine cannot read the key, so it cannot read the tokens. Copying the pairings file
/// to a different machine yields nothing without the key beside it.</para>
/// <para><strong>Weaker than DPAPI in one way, and honestly so.</strong> DPAPI ties the key to the
/// account's own credentials; this ties it to file permissions. Somebody who can read this
/// user's files can read the key. That is the same boundary the rest of the user's profile has,
/// and it is the boundary every desktop program on these platforms lives with unless it talks to
/// a keyring, which not every desktop has.</para>
/// <para>The purpose goes in as associated data, so a blob written for one purpose does not
/// decrypt under another, the same way DPAPI's entropy keeps the two kinds of secret apart.</para>
/// </remarks>
public sealed class KeyFileSecretProtector : IPairingSecretProtector
{
    public const string DefaultFileName = "secret.key";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly string _keyPath;
    private readonly byte[] _purpose;

    public KeyFileSecretProtector(string keyPath, string purpose = SecretPurposes.DeviceToken)
    {
        _keyPath = keyPath;
        _purpose = Encoding.UTF8.GetBytes(purpose);
    }

    /// <summary>The default location: <c>~/.config/Modbot/secret.key</c>, beside the pairings.</summary>
    public static string DefaultPath(string applicationData)
        => Path.Combine(applicationData, "Modbot", DefaultFileName);

    public byte[] Protect(byte[] plaintext)
    {
        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, _purpose);

        var blob = new byte[NonceBytes + TagBytes + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceBytes);
        ciphertext.CopyTo(blob, NonceBytes + TagBytes);
        return blob;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceBytes + TagBytes)
            throw new CryptographicException("The stored secret is too short to be one this program wrote.");

        var key = LoadKey() ?? throw new CryptographicException("The key file that protects the stored secrets is missing.");

        var nonce = ciphertext.AsSpan(0, NonceBytes);
        var tag = ciphertext.AsSpan(NonceBytes, TagBytes);
        var payload = ciphertext.AsSpan(NonceBytes + TagBytes);
        var plaintext = new byte[payload.Length];

        // A wrong key, a wrong purpose or a changed byte all fail the tag, and AesGcm reports that
        // as a CryptographicException -- the same exception DPAPI throws, so the store's one catch
        // covers both.
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, payload, tag, plaintext, _purpose);
        return plaintext;
    }

    private byte[]? LoadKey()
    {
        if (!File.Exists(_keyPath))
            return null;

        var key = File.ReadAllBytes(_keyPath);
        return key.Length == KeyBytes ? key : throw new CryptographicException("The key file is not the size this program writes.");
    }

    private byte[] LoadOrCreateKey()
    {
        if (LoadKey() is { } existing)
            return existing;

        var directory = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var key = RandomNumberGenerator.GetBytes(KeyBytes);

        // Created with the mode already set rather than written and then tightened, so there is
        // no moment where the key is on disk and readable by everyone.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(_keyPath, options))
            stream.Write(key);

        return key;
    }
}

/// <summary>Picks the protector this machine can use.</summary>
public static class PairingSecretProtectors
{
    /// <summary>
    /// DPAPI on Windows; a key file only this user can read everywhere else. The same call on
    /// every platform, so the program never has to ask which one it is on before it can store a
    /// token -- which is how a copy run from source on Linux stopped at the first pairing.
    /// </summary>
    public static IPairingSecretProtector ForThisMachine(
        string applicationData, string purpose = SecretPurposes.DeviceToken)
        => OperatingSystem.IsWindows()
            ? new DpapiSecretProtector(purpose)
            : new KeyFileSecretProtector(KeyFileSecretProtector.DefaultPath(applicationData), purpose);
}

/// <summary>Why a stored pairing could not be used.</summary>
public enum PairingFault
{
    /// <summary>Nothing wrong.</summary>
    None,

    /// <summary>
    /// The token is there but will not decrypt. Almost always because the file was copied from
    /// another machine or another account, or the key file beside it is gone — the key being
    /// tied to this user on this machine is the point, not a bug.
    /// </summary>
    TokenUndecryptable,

    /// <summary>The entry is not shaped like a pairing at all.</summary>
    Unreadable,
}

/// <param name="Pairing">Null when <see cref="Fault"/> is not <see cref="PairingFault.None"/>.</param>
/// <param name="ServerId">Always present, because it is stored in the clear on purpose.</param>
public sealed record LoadedPairing(string ServerId, ServerPairing? Pairing, PairingFault Fault = PairingFault.None)
{
    public bool IsUsable => Fault is PairingFault.None && Pairing is not null;
}

/// <summary>Where a moderator's pairings live between runs.</summary>
public interface IPairingStore
{
    IReadOnlyList<LoadedPairing> Load();

    void Save(ServerPairing pairing);

    void Remove(string serverId);
}

/// <summary>
/// One file holding every server this client is paired with, with the tokens encrypted.
/// </summary>
/// <remarks>
/// <para><strong>What is written to your disk, and where.</strong> One JSON file in Modbot's own
/// folder under your user profile. It holds, per paired server: the label you gave it, its
/// address, the group it manages, the negotiated API version, and the device token. Nothing else —
/// no VRChat account, no log contents, no instance history, no machine fingerprint.</para>
/// <para><strong>Only the token is encrypted, and that is deliberate.</strong> The rest is left
/// readable so that a suspicious moderator can open this file in Notepad and see exactly which
/// servers their client talks to, without having to take anyone's word for it. Encrypting the
/// whole file would hide the one thing they most want to check.</para>
/// <para><strong>Nothing here is transmitted.</strong> A token is sent as a bearer header to the
/// one server it belongs to and to nowhere else; the file itself never leaves the machine.</para>
/// <para><strong>Removing a pairing removes the token.</strong> Uninstalling stops reporting
/// without needing the server operator to do anything.</para>
/// </remarks>
public sealed class DpapiPairingStore : IPairingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly IPairingSecretProtector _protector;

    public DpapiPairingStore(string path, IPairingSecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    /// <summary>The default location: <c>%APPDATA%\Modbot\pairings.json</c>.</summary>
    /// <remarks>
    /// The pairings live under the user profile because they are encrypted to that user and mean
    /// nothing to another one. The program itself installs to <c>Program Files</c>: software that
    /// runs from <c>%APPDATA%</c> is scored as hostile, correctly.
    /// </remarks>
    public static string DefaultPath(string applicationData)
        => Path.Combine(applicationData, "Modbot", "pairings.json");

    public IReadOnlyList<LoadedPairing> Load()
    {
        if (!File.Exists(_path))
            return [];

        List<Entry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json);
        }
        catch (JsonException)
        {
            // A truncated file after a power cut. Reporting nothing is honest; rewriting it here
            // would destroy tokens that a repair might still recover.
            return [];
        }

        var loaded = new List<LoadedPairing>();
        foreach (var entry in entries ?? [])
        {
            if (entry is not { ServerId.Length: > 0, BaseUri.Length: > 0 })
                continue;

            loaded.Add(Read(entry));
        }

        return loaded;
    }

    public void Save(ServerPairing pairing)
    {
        var entries = ReadRaw();
        entries.RemoveAll(e => string.Equals(e.ServerId, pairing.ServerId, StringComparison.Ordinal));

        entries.Add(new Entry(
            pairing.ServerId,
            pairing.BaseUri.ToString(),
            pairing.ManagedGroupId,
            pairing.ManagedGroupName,
            pairing.ManagedGroupIconUrl,
            pairing.ApiVersion,
            Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(pairing.DeviceToken)))));

        Write(entries);
    }

    public void Remove(string serverId)
    {
        var entries = ReadRaw();
        if (entries.RemoveAll(e => string.Equals(e.ServerId, serverId, StringComparison.Ordinal)) > 0)
            Write(entries);
    }

    private LoadedPairing Read(Entry entry)
    {
        string token;
        try
        {
            token = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(entry.DeviceToken)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The file came from another Windows account or another machine. That is DPAPI doing
            // its job, so it is reported as a pairing that needs redoing rather than as a crash --
            // and emphatically not retried against the server, which would look like a client
            // hammering an endpoint with a credential it cannot read.
            return new LoadedPairing(entry.ServerId, null, PairingFault.TokenUndecryptable);
        }

        try
        {
            return new LoadedPairing(
                entry.ServerId,
                new ServerPairing(
                    entry.ServerId,
                    new Uri(entry.BaseUri, UriKind.Absolute),
                    token,
                    entry.ManagedGroupId,
                    entry.ApiVersion)
                {
                    ManagedGroupName = entry.ManagedGroupName,
                    ManagedGroupIconUrl = entry.ManagedGroupIconUrl,
                });
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return new LoadedPairing(entry.ServerId, null, PairingFault.Unreadable);
        }
    }

    private List<Entry> ReadRaw()
    {
        if (!File.Exists(_path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(List<Entry> entries)
    {
        if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        File.WriteAllText(_path, JsonSerializer.Serialize(entries, Json), new UTF8Encoding(false));
    }

    /// <summary>
    /// One line of the file. Everything except <see cref="DeviceToken"/> is stored in the clear so
    /// the file can be read and checked.
    /// </summary>
    private sealed record Entry(
        [property: JsonPropertyName("serverId")] string ServerId,
        [property: JsonPropertyName("baseUri")] string BaseUri,
        [property: JsonPropertyName("managedGroupId")] string ManagedGroupId,
        [property: JsonPropertyName("managedGroupName")] string? ManagedGroupName,
        [property: JsonPropertyName("managedGroupIconUrl")] string? ManagedGroupIconUrl,
        [property: JsonPropertyName("apiVersion")] int ApiVersion,
        [property: JsonPropertyName("deviceTokenProtected")] string DeviceToken);
}
