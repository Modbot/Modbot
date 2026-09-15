using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Client.Pairing;

namespace Modbot.Client.CloudBackup;

/// <summary>This client's identity with one Modbot Cloud.</summary>
/// <param name="Endpoint">The Cloud it belongs to.</param>
/// <param name="InstallId">A random id Cloud gave this client. Not a name, an account or a machine.</param>
/// <param name="Secret">Proves the id is this client's. Held in memory; encrypted on disk.</param>
public sealed record CloudInstall(Uri Endpoint, Guid InstallId, string Secret);

/// <summary>Where installs live between runs.</summary>
public interface ICloudInstallStore
{
    CloudInstall? Find(Uri endpoint);

    void Save(CloudInstall install);

    void Forget(Uri endpoint);
}

/// <summary>
/// One file holding this client's install with each Modbot Cloud it has sent to, the secrets
/// encrypted.
/// </summary>
/// <remarks>
/// <para><strong>What is written to your disk.</strong> <c>%APPDATA%\Modbot\cloud-installs.json</c>:
/// per Cloud, its address, the install id it gave this client, and the secret, encrypted to your
/// Windows account the same way pairing tokens are (<see cref="DpapiSecretProtector"/>). The address
/// and id are readable, so you can see which Cloud this client has registered with.</para>
/// <para><strong>What is sent.</strong> The id and secret, together, as the bearer header on
/// batches to the one Cloud they belong to. Never to a Modbot server, never anywhere else.</para>
/// <para>A secret that will not decrypt — a file copied from another account — is treated as no
/// install at all, and the client registers again.</para>
/// </remarks>
public sealed class DpapiCloudInstallStore(string path, IPairingSecretProtector protector) : ICloudInstallStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>The default location: <c>%APPDATA%\Modbot\cloud-installs.json</c>.</summary>
    public static string DefaultPath(string applicationData) => Path.Combine(applicationData, "Modbot", "cloud-installs.json");

    public CloudInstall? Find(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        lock (_gate)
        {
            var entry = ReadAll().FirstOrDefault(e => SameCloud(e.Endpoint, endpoint));
            if (entry is null)
                return null;

            try
            {
                var secret = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(entry.SecretProtected)));
                return new CloudInstall(endpoint, entry.InstallId, secret);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }
    }

    public void Save(CloudInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);

        lock (_gate)
        {
            var entries = ReadAll();
            entries.RemoveAll(e => SameCloud(e.Endpoint, install.Endpoint));
            entries.Add(new Entry(
                install.Endpoint.GetLeftPart(UriPartial.Authority),
                install.InstallId,
                Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(install.Secret)))));
            Write(entries);
        }
    }

    public void Forget(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        lock (_gate)
        {
            var entries = ReadAll();
            if (entries.RemoveAll(e => SameCloud(e.Endpoint, endpoint)) > 0)
                Write(entries);
        }
    }

    private static bool SameCloud(string stored, Uri endpoint) =>
        string.Equals(stored, endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private List<Entry> ReadAll()
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void Write(List<Entry> entries)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(entries, Json), new UTF8Encoding(false));
    }

    private sealed record Entry(
        [property: JsonPropertyName("endpoint")] string Endpoint,
        [property: JsonPropertyName("installId")] Guid InstallId,
        [property: JsonPropertyName("secretProtected")] string SecretProtected);
}
