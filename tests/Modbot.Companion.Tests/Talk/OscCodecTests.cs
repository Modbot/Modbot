using System.Buffers.Binary;
using System.Text;
using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// The OSC packets Talk in Discord or VRChat reads and writes: what goes out has to be exactly what
/// VRChat expects, and nothing that comes in may ever throw.
/// </summary>
public class OscCodecTests
{
    private static byte[] Padded(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[(raw.Length + 4) & ~3];
        raw.CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public void TheVoiceButtonIsWrittenTheWayOscSaysItIs()
    {
        var bytes = OscCodec.Write("/input/Voice", 1);

        var expected = Padded("/input/Voice").Concat(Padded(",i")).Concat(new byte[] { 0, 0, 0, 1 }).ToArray();
        Assert.Equal(expected, bytes);
        Assert.Equal(0, bytes.Length % 4);
    }

    [Fact]
    public void ReleasingTheVoiceButtonIsAZero()
    {
        var message = Assert.Single(OscCodec.Read(OscCodec.Write("/input/Voice", 0)));

        Assert.Equal("/input/Voice", message.Address);
        Assert.Equal(0, Assert.Single(message.Arguments));
    }

    [Fact]
    public void WhatIsWrittenIsReadBack()
    {
        var bytes = OscCodec.Write("/avatar/change", "avtr_1234", 7, 0.5f, true, false);

        var message = Assert.Single(OscCodec.Read(bytes));

        Assert.Equal("/avatar/change", message.Address);
        Assert.Equal(new object[] { "avtr_1234", 7, 0.5f, true, false }, message.Arguments);
    }

    [Theory]
    [InlineData("/a")]
    [InlineData("/ab")]
    [InlineData("/abc")]
    [InlineData("/abcd")]
    public void EveryLengthOfAddressIsPaddedToAMultipleOfFour(string address)
    {
        var bytes = OscCodec.Write(address);

        Assert.Equal(0, bytes.Length % 4);
        Assert.Equal(address, Assert.Single(OscCodec.Read(bytes)).Address);
    }

    [Fact]
    public void MuteSelfIsReadWhetherItComesAsABoolOrAsAZeroOrOne()
    {
        const string address = "/avatar/parameters/MuteSelf";

        Assert.True(Assert.Single(OscCodec.Read(OscCodec.Write(address, true))).FirstAsBool);
        Assert.False(Assert.Single(OscCodec.Read(OscCodec.Write(address, false))).FirstAsBool);
        Assert.True(Assert.Single(OscCodec.Read(OscCodec.Write(address, 1))).FirstAsBool);
        Assert.False(Assert.Single(OscCodec.Read(OscCodec.Write(address, 0))).FirstAsBool);
    }

    [Fact]
    public void ANonYesNoOrNothingAtAllIsNotAnAnswer()
    {
        Assert.Null(Assert.Single(OscCodec.Read(OscCodec.Write("/x", "yes"))).FirstAsBool);
        Assert.Null(Assert.Single(OscCodec.Read(OscCodec.Write("/x"))).FirstAsBool);
    }

    [Fact]
    public void ABundleGivesEveryMessageInsideIt()
    {
        var first = OscCodec.Write("/avatar/parameters/MuteSelf", true);
        var second = OscCodec.Write("/avatar/parameters/Other", 3);

        var bundle = new List<byte>();
        bundle.AddRange(Padded("#bundle"));
        bundle.AddRange(new byte[8]);

        foreach (var message in new[] { first, second })
        {
            var size = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, message.Length);
            bundle.AddRange(size);
            bundle.AddRange(message);
        }

        var messages = OscCodec.Read([.. bundle]);

        Assert.Equal(new[] { "/avatar/parameters/MuteSelf", "/avatar/parameters/Other" }, messages.Select(m => m.Address));
    }

    [Fact]
    public void ABundleInsideABundleIsReadToo()
    {
        var inner = OscCodec.Write("/avatar/parameters/MuteSelf", true);

        var innerBundle = new List<byte>();
        innerBundle.AddRange(Padded("#bundle"));
        innerBundle.AddRange(new byte[8]);
        innerBundle.AddRange(BigEndian(inner.Length));
        innerBundle.AddRange(inner);

        var outer = new List<byte>();
        outer.AddRange(Padded("#bundle"));
        outer.AddRange(new byte[8]);
        outer.AddRange(BigEndian(innerBundle.Count));
        outer.AddRange(innerBundle);

        Assert.Equal("/avatar/parameters/MuteSelf", Assert.Single(OscCodec.Read([.. outer])).Address);
    }

    [Fact]
    public void NothingAtAllGivesNothing()
    {
        Assert.Empty(OscCodec.Read([]));
    }

    [Fact]
    public void AddressesWithoutASlashAreNotMessages()
    {
        Assert.Empty(OscCodec.Read(Padded("hello")));
    }

    [Fact]
    public void ATruncatedArgumentGivesNothingRatherThanAGuess()
    {
        var whole = OscCodec.Write("/input/Voice", 1);

        Assert.Empty(OscCodec.Read(whole.AsSpan(0, whole.Length - 2)));
    }

    [Fact]
    public void ABundleThatClaimsMoreThanItHoldsGivesNothing()
    {
        var bundle = new List<byte>();
        bundle.AddRange(Padded("#bundle"));
        bundle.AddRange(new byte[8]);
        bundle.AddRange(BigEndian(9999));
        bundle.AddRange(OscCodec.Write("/x"));

        Assert.Empty(OscCodec.Read([.. bundle]));
    }

    [Fact]
    public void ATypeNobodyKnowsStopsTheMessageInsteadOfThrowing()
    {
        var bytes = Padded("/x").Concat(Padded(",q")).Concat(new byte[4]).ToArray();

        Assert.Empty(OscCodec.Read(bytes));
    }

    [Fact]
    public void RandomBytesNeverThrow()
    {
        var random = new Random(17);

        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[random.Next(0, 80)];
            random.NextBytes(bytes);

            _ = OscCodec.Read(bytes);

            if (bytes.Length > 0)
            {
                // The same, with a believable start: a bundle tag or a slash.
                bytes[0] = (byte)(i % 2 == 0 ? '#' : '/');
                _ = OscCodec.Read(bytes);
            }
        }
    }

    [Fact]
    public void AnArgumentOscWritingDoesNotKnowIsRefused()
    {
        Assert.Throws<ArgumentException>(() => OscCodec.Write("/x", 12345678901L));
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
