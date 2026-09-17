using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modbot.Client.Ingest;
using Modbot.Client.Pairing;

namespace Modbot.Client.Tests.Pairing;

/// <summary>
/// The token store holds the only credential the client has, and the interesting cases are the
/// ones where it cannot be read: a file copied between Windows accounts, or a machine restore.
/// Both must produce "re-pair this server", never a crash and never a retry loop against a
/// server with a credential the client cannot decrypt.
/// </summary>
public class PairingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-pairing-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "pairings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    /// <summary>A stand-in for DPAPI, so the file format can be tested away from Windows' key.</summary>
    private sealed class ReversingProtector : IPairingSecretProtector
    {
        public bool Fail { get; set; }

        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();

        public byte[] Unprotect(byte[] ciphertext)
            => Fail ? throw new CryptographicException("wrong user") : ciphertext.Reverse().ToArray();
    }

    private static ServerPairing Pairing(string serverId = "cats", string token = "dev_secret")
        => new(serverId, new Uri("https://modbot.example"), token, "grp_cats", 3);

    [Fact]
    public void RoundTripsAPairing()
    {
        var protector = new ReversingProtector();
        var store = new DpapiPairingStore(Path_, protector);

        store.Save(Pairing());

        var loaded = new DpapiPairingStore(Path_, protector).Load();

        var only = Assert.Single(loaded);
        Assert.True(only.IsUsable);
        Assert.Equal("dev_secret", only.Pairing!.DeviceToken);
        Assert.Equal("grp_cats", only.Pairing.ManagedGroupId);
        Assert.Equal(3, only.Pairing.ApiVersion);
    }

    [Fact]
    public void KeepsOnePairingPerServerRatherThanAccumulatingThem()
    {
        var protector = new ReversingProtector();
        var store = new DpapiPairingStore(Path_, protector);

        store.Save(Pairing(token: "first"));
        store.Save(Pairing(token: "second"));

        Assert.Equal("second", Assert.Single(store.Load()).Pairing!.DeviceToken);
    }

    [Fact]
    public void HoldsSeveralServersSideBySideWithUnrelatedTokens()
    {
        // A moderator staffing two groups pairs the same client twice. Neither operator learns of
        // the other, and neither token is derived from the other.
        var store = new DpapiPairingStore(Path_, new ReversingProtector());

        store.Save(Pairing("cats", "token-cats"));
        store.Save(new ServerPairing("dogs", new Uri("https://dogs.example"), "token-dogs", "grp_dogs"));

        var loaded = store.Load().OrderBy(p => p.ServerId).ToList();

        Assert.Equal(["cats", "dogs"], loaded.Select(p => p.ServerId));
        Assert.Equal(["token-cats", "token-dogs"], loaded.Select(p => p.Pairing!.DeviceToken));
    }

    [Fact]
    public void StoresTheTokenEncryptedAndEverythingElseInTheClear()
    {
        // Deliberate: a suspicious moderator can open this file and see exactly which servers
        // their client talks to. Encrypting the whole file would hide the one thing they most
        // want to check, and the token is the only part that is actually a secret.
        new DpapiPairingStore(Path_, new ReversingProtector()).Save(Pairing());

        var raw = File.ReadAllText(Path_);

        Assert.Contains("https://modbot.example", raw, StringComparison.Ordinal);
        Assert.Contains("grp_cats", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("dev_secret", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndecryptableTokenIsReportedAsNeedingRepairingRatherThanThrowing()
    {
        // This is what a file copied from another Windows account looks like: DPAPI doing exactly
        // its job. The client must show "re-pair this server", not crash on start-up and not
        // start posting a credential it cannot read.
        var protector = new ReversingProtector();
        new DpapiPairingStore(Path_, protector).Save(Pairing());

        protector.Fail = true;
        var loaded = Assert.Single(new DpapiPairingStore(Path_, protector).Load());

        Assert.False(loaded.IsUsable);
        Assert.Equal(PairingFault.TokenUndecryptable, loaded.Fault);

        // The label survives, because it was stored in the clear: the moderator is told *which*
        // server needs re-pairing rather than "something is wrong".
        Assert.Equal("cats", loaded.ServerId);
    }

    [Fact]
    public void ATokenThatIsNotEvenBase64IsTheSameKindOfFaultRatherThanACrash()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, """
            [{"serverId":"cats","baseUri":"https://modbot.example","managedGroupId":"grp_cats",
              "apiVersion":1,"deviceTokenProtected":"not base64 at all!!"}]
            """);

        var loaded = Assert.Single(new DpapiPairingStore(Path_, new ReversingProtector()).Load());

        Assert.Equal(PairingFault.TokenUndecryptable, loaded.Fault);
    }

    [Fact]
    public void ATruncatedFileLosesNothingThatCanStillBeRecovered()
    {
        // Reporting nothing is honest. Rewriting the file here would destroy tokens that a
        // repair might still get back.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, """[{"serverId":"cats","baseUri":"https:""");

        var store = new DpapiPairingStore(Path_, new ReversingProtector());

        Assert.Empty(store.Load());
        Assert.Equal("""[{"serverId":"cats","baseUri":"https:""", File.ReadAllText(Path_));
    }

    [Fact]
    public void RemovingAPairingRemovesItsToken()
    {
        // Uninstalling stops reporting without the server operator having to do anything.
        var store = new DpapiPairingStore(Path_, new ReversingProtector());
        store.Save(Pairing("cats"));
        store.Save(new ServerPairing("dogs", new Uri("https://dogs.example"), "token-dogs", "grp_dogs"));

        store.Remove("cats");

        Assert.Equal("dogs", Assert.Single(store.Load()).ServerId);
        Assert.DoesNotContain("modbot.example", File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFileIsNotAnError()
        => Assert.Empty(new DpapiPairingStore(Path_, new ReversingProtector()).Load());

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RealDpapiRoundTripsOnWindows()
    {
        // The shipping protector, exercised for real. Skipped elsewhere: the client is a
        // Windows-only product by design, so there is nothing to fall back to and nothing to fix.
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only.");

        var protector = new DpapiSecretProtector();
        var cipher = protector.Protect(Encoding.UTF8.GetBytes("dev_secret"));

        Assert.NotEqual("dev_secret", Encoding.UTF8.GetString(cipher));
        Assert.Equal("dev_secret", Encoding.UTF8.GetString(protector.Unprotect(cipher)));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RealDpapiRefusesACiphertextItDidNotProduce()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only.");

        var protector = new DpapiSecretProtector();
        var cipher = protector.Protect(Encoding.UTF8.GetBytes("dev_secret"));
        cipher[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => protector.Unprotect(cipher));
    }

    // ── The key file, for the platforms without DPAPI ─────────────────────────────────────

    private string KeyPath => Path.Combine(_directory, "secret.key");

    [Fact]
    public void TheKeyFileProtectorRoundTripsAndMakesItsKeyOnFirstUse()
    {
        var protector = new KeyFileSecretProtector(KeyPath);

        var cipher = protector.Protect(Encoding.UTF8.GetBytes("dev_secret"));

        Assert.True(File.Exists(KeyPath));
        Assert.Equal(32, new FileInfo(KeyPath).Length);
        Assert.NotEqual("dev_secret", Encoding.UTF8.GetString(cipher));
        Assert.Equal("dev_secret", Encoding.UTF8.GetString(new KeyFileSecretProtector(KeyPath).Unprotect(cipher)));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void TheKeyFileIsReadableByThisUserAlone()
    {
        Assert.SkipWhen(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "File modes are a Unix thing; Windows uses DPAPI.");

        new KeyFileSecretProtector(KeyPath).Protect([1, 2, 3]);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_directory));
    }

    [Fact]
    public void TheKeyFileProtectorRefusesACiphertextItDidNotProduce()
    {
        var protector = new KeyFileSecretProtector(KeyPath);
        var cipher = protector.Protect(Encoding.UTF8.GetBytes("dev_secret"));
        cipher[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(cipher));
    }

    /// <summary>A device token must not decrypt as a Cloud secret, and the other way round.</summary>
    [Fact]
    public void TheKeyFileProtectorKeepsThePurposesApart()
    {
        var cipher = new KeyFileSecretProtector(KeyPath).Protect(Encoding.UTF8.GetBytes("dev_secret"));

        Assert.ThrowsAny<CryptographicException>(
            () => new KeyFileSecretProtector(KeyPath, SecretPurposes.CloudSecret).Unprotect(cipher));
    }

    /// <summary>
    /// The pairings file copied to a machine without the key beside it: the same "re-pair this
    /// server" the store reports for a DPAPI blob from another account, never a crash.
    /// </summary>
    [Fact]
    public void AMissingKeyFileReadsAsAPairingThatNeedsRedoing()
    {
        new DpapiPairingStore(Path_, new KeyFileSecretProtector(KeyPath)).Save(Pairing());
        File.Delete(KeyPath);

        var loaded = new DpapiPairingStore(Path_, new KeyFileSecretProtector(KeyPath)).Load();

        var only = Assert.Single(loaded);
        Assert.Equal(PairingFault.TokenUndecryptable, only.Fault);
        Assert.Equal("cats", only.ServerId);
    }

    [Fact]
    public void ThisMachineGetsDpapiOnWindowsAndTheKeyFileElsewhere()
    {
        var protector = PairingSecretProtectors.ForThisMachine(_directory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.IsType<DpapiSecretProtector>(protector);
        else
            Assert.IsType<KeyFileSecretProtector>(protector);
    }

    [Fact]
    public void TheStoredFileIsTheShapeAHumanCanRead()
    {
        new DpapiPairingStore(Path_, new ReversingProtector()).Save(Pairing());

        using var document = JsonDocument.Parse(File.ReadAllText(Path_));
        var fields = document.RootElement[0].EnumerateObject().Select(p => p.Name).Order().ToList();

        Assert.Equal(
            ["apiVersion", "baseUri", "deviceTokenProtected", "managedGroupId", "serverId"],
            fields);
    }
}
