using System.Security.Cryptography;
using System.Text;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Tests.Fakes;

namespace Modbot.Evidence.Tests.Storage;

/// <summary>
/// Design section 5.1: the key is the SHA-256 of the bytes, every key is hex, and therefore path
/// traversal is impossible by construction rather than mitigated.
/// </summary>
public class ContentAddressingTests
{
    [Fact]
    public void TheKeyIsTheHashOfTheBytes()
    {
        var content = SampleMedia.Png();
        var hash = EvidenceHash.Compute(content);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), hash.Hex);
        Assert.Equal($"sha256/{hash.Hex[..2]}/{hash.Hex[2..4]}/{hash.Hex}", EvidenceKeys.ForObject(hash));
    }

    [Fact]
    public void IdenticalBytesProduceOneKey()
    {
        var first = EvidenceHash.Compute(SampleMedia.Jpeg());
        var second = EvidenceHash.Compute(SampleMedia.Jpeg());

        Assert.Equal(first, second);
        Assert.Equal(EvidenceKeys.ForObject(first), EvidenceKeys.ForObject(second));
    }

    /// <summary>
    /// There is no overload that takes a caller's string, so a traversal attempt cannot be
    /// expressed. These are the shapes somebody would try.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void NothingButLowercaseHexParsesAsAHash(string attempt)
        => Assert.False(EvidenceHash.TryParse(attempt, out _));

    /// <summary>
    /// Uppercase is refused rather than normalised. Two spellings of one hash would be two keys on
    /// a case-sensitive store and one key on a case-folding one — a deduplication bug that only
    /// shows up on somebody else's filesystem.
    /// </summary>
    [Fact]
    public void UppercaseHexIsNotAHash()
    {
        var hex = EvidenceHash.Compute(SampleMedia.Png()).Hex;

        Assert.True(EvidenceHash.TryParse(hex, out _));
        Assert.False(EvidenceHash.TryParse(hex.ToUpperInvariant(), out _));
    }

    [Fact]
    public void AnUploadIdIsAlsoHexOnly()
    {
        Assert.True(EvidenceUploadId.TryParse(EvidenceUploadId.New().Value, out _));
        Assert.False(EvidenceUploadId.TryParse("../../staging/other", out _));
        Assert.False(EvidenceUploadId.TryParse(Guid.NewGuid().ToString("D"), out _));
        Assert.False(EvidenceUploadId.TryParse(null, out _));
    }

    [Fact]
    public void TheStagingKeyIsStableAcrossRetriesOfTheSameUpload()
    {
        var id = EvidenceUploadId.New();

        Assert.Equal(EvidenceKeys.ForStaging(id), EvidenceKeys.ForStaging(id));
        Assert.NotEqual(EvidenceKeys.ForStaging(id), EvidenceKeys.ForStaging(EvidenceUploadId.New()));
    }

    [Fact]
    public void AnObjectKeyReadsBackIntoItsHash()
    {
        var hash = EvidenceHash.Compute(SampleMedia.Webm());

        Assert.True(EvidenceKeys.TryReadObjectKey(EvidenceKeys.ForObject(hash), out var parsed));
        Assert.Equal(hash, parsed);
    }

    [Theory]
    [InlineData("staging/abc")]
    [InlineData(".modbot-store")]
    [InlineData("sha256/ab/cd/nothex")]
    public void AKeyThatIsNotAnObjectKeyDoesNotReadBack(string key)
        => Assert.False(EvidenceKeys.TryReadObjectKey(key, out _));

    /// <summary>
    /// The sentinel is the one key that is not a hash, and it lives outside the object prefix so
    /// that "everything under sha256/ is hex" stays literally true.
    /// </summary>
    [Fact]
    public void TheSentinelLivesOutsideTheObjectPrefix()
        => Assert.DoesNotContain(EvidenceKeys.ObjectPrefix, EvidenceKeys.SentinelKey, StringComparison.Ordinal);

    [Fact]
    public void ASentinelRoundTripsThroughItsSerialisedForm()
    {
        var sentinel = new StoreSentinel(Guid.NewGuid(), new DateTimeOffset(2026, 3, 4, 21, 14, 0, TimeSpan.Zero), "home");
        var parsed = StoreSentinel.TryParse(sentinel.Serialise());

        Assert.NotNull(parsed);
        Assert.Equal(sentinel.StoreId, parsed.StoreId);
        Assert.Equal(sentinel.Deployment, parsed.Deployment);
    }

    /// <summary>
    /// Something at the sentinel key that is not a sentinel is a finding of the same kind as a
    /// foreign one, not the same thing as an empty store.
    /// </summary>
    [Fact]
    public void RubbishAtTheSentinelKeyDoesNotParseAsASentinel()
    {
        Assert.Null(StoreSentinel.TryParse(Encoding.UTF8.GetBytes("not json at all")));
        Assert.Null(StoreSentinel.TryParse(Encoding.UTF8.GetBytes("""{"storeId":"00000000-0000-0000-0000-000000000000"}""")));
    }
}
