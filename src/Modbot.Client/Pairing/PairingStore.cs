using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Client.Ingest;

namespace Modbot.Client.Pairing;

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
    /// <summary>
    /// Mixed into the key so a blob lifted out of this file cannot be decrypted by a different
    /// program that happens to run as the same user and calls DPAPI with no entropy.
    /// </summary>
    public const string DeviceTokenPurpose = "moe.bin.modbot.client.device-token.v1";

    /// <summary>A different purpose for Modbot Cloud secrets, so neither kind decrypts as the other.</summary>
    public const string CloudSecretPurpose = "moe.bin.modbot.client.cloud-secret.v1";

    private readonly byte[] _entropy;

    public DpapiSecretProtector(string purpose = DeviceTokenPurpose) => _entropy = Encoding.UTF8.GetBytes(purpose);

    public byte[] Protect(byte[] plaintext)
        => ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext)
        => ProtectedData.Unprotect(ciphertext, _entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Why a stored pairing could not be used.</summary>
public enum PairingFault
{
    /// <summary>Nothing wrong.</summary>
    None,

    /// <summary>
    /// The token is there but will not decrypt. Almost always because the file was copied from
    /// another machine or another Windows account — DPAPI's key is the point, not a bug.
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
                    entry.ApiVersion));
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
        [property: JsonPropertyName("apiVersion")] int ApiVersion,
        [property: JsonPropertyName("deviceTokenProtected")] string DeviceToken);
}
