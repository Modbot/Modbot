using System.Security.Cryptography;
using Modbot.Core.Security;
using Modbot.Core.Tests.Data;

namespace Modbot.Core.Tests.Security;

[Collection(nameof(PostgresCollection))]
public class SecretProtectorTests
{
    private readonly PostgresFixture _db;

    public SecretProtectorTests(PostgresFixture db) => _db = db;

    private async Task<ISecretProtector> CreateAsync()
    {
        var context = _db.NewContext();
        return await AesGcmSecretProtector.CreateAsync(context, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RoundTrips()
    {
        var protector = await CreateAsync();

        var ciphertext = protector.Protect("hunter2");

        Assert.NotEqual("hunter2", ciphertext);
        Assert.Equal("hunter2", protector.Unprotect(ciphertext));
    }

    [Fact]
    public async Task ProducesDifferentCiphertextForTheSamePlaintext()
    {
        var protector = await CreateAsync();

        // A fresh nonce per call. Identical ciphertexts would let anyone reading the table see
        // that two accounts share a password.
        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public async Task RejectsTamperedCiphertext()
    {
        var protector = await CreateAsync();
        var bytes = Convert.FromBase64String(protector.Protect("hunter2"));
        bytes[^1] ^= 0xFF;

        // ThrowsAny, not Throws: AES-GCM reports tampering as AuthenticationTagMismatchException,
        // a CryptographicException subclass, and xUnit's Throws<T> demands an exact type match.
        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public async Task ReusesTheStoredKeyAcrossInstances()
    {
        var first = await CreateAsync();
        var ciphertext = first.Protect("persisted");

        var second = await CreateAsync();

        Assert.Equal("persisted", second.Unprotect(ciphertext));
    }

    [Fact]
    public void UnprotectNull_ReturnsNull()
    {
        // Settings columns are nullable before onboarding fills them in; callers should not have
        // to null-check at every site.
        Assert.Null(AesGcmSecretProtector.ForTesting().Unprotect(null));
    }
}
