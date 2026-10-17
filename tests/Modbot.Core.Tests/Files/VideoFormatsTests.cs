using System.Buffers.Binary;
using System.Text;
using Modbot.Core.Files;

namespace Modbot.Core.Tests.Files;

/// <summary>
/// A video's kind comes from its first bytes, never from what a host or a file name called it, and its
/// shape comes from the track header of an MP4 or QuickTime file.
/// </summary>
public class VideoFormatsTests
{
    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Ftyp(string brand) => Box("ftyp", Encoding.ASCII.GetBytes(brand), new byte[4]);

    /// <summary>A version 0 track header of the size given, with a matrix that is either upright or a quarter turn.</summary>
    private static byte[] Track(int width, int height, bool turned)
    {
        var body = new byte[84];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(40), turned ? 0 : 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(44), turned ? 0x10000 : 0);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(52), turned ? -0x10000 : 0);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(56), turned ? 0 : 0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(76), (uint)width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(80), (uint)height << 16);
        return Box("trak", Box("tkhd", body));
    }

    public static TheoryData<byte[], string> Videos() => new()
    {
        { Ftyp("isom"), "video/mp4" },
        { Ftyp("mp42"), "video/mp4" },
        { Ftyp("qt  "), "video/quicktime" },
        { [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0], "video/webm" },
        { [0x00, 0x00, 0x01, 0xBA, 0, 0, 0, 0], "video/mpeg" },
        { [0x00, 0x00, 0x01, 0xB3, 0, 0, 0, 0], "video/mpeg" },
    };

    [Theory]
    [MemberData(nameof(Videos))]
    public void EachKindIsKnownByItsBytes(byte[] bytes, string expected)
    {
        Assert.Equal(expected, VideoFormats.Sniff(bytes));
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("heic")]
    [InlineData("mif1")]
    [InlineData("M4A ")]
    public void APictureOrSoundInTheSameBoxesIsNotAVideo(string brand)
    {
        Assert.Null(VideoFormats.Sniff(Ftyp(brand)));
    }

    [Theory]
    [InlineData("<!doctype html><html></html>")]
    [InlineData("{\"error\":\"nope\"}")]
    [InlineData("GIF89a......")]
    [InlineData("")]
    public void AnythingElseIsNotAVideo(string text)
    {
        Assert.Null(VideoFormats.Sniff(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void TheSizeIsReadFromTheTrackHeader()
    {
        var video = Ftyp("isom").Concat(Box("moov", Track(1280, 720, turned: false))).ToArray();

        Assert.Equal((1280, 720), VideoFormats.Size(video));
    }

    [Fact]
    public void ASidewaysVideoIsTurnedTheRightWayUp()
    {
        var video = Ftyp("qt  ").Concat(Box("moov", Track(1920, 1080, turned: true))).ToArray();

        Assert.Equal((1080, 1920), VideoFormats.Size(video));
    }

    [Fact]
    public void ABoxThatRunsPastTheEndOrAFileOfAnotherKindHasNoSize()
    {
        var cut = Ftyp("isom").Concat(Box("moov", Track(1280, 720, turned: false))).ToArray()[..60];

        Assert.Null(VideoFormats.Size(cut));
        Assert.Null(VideoFormats.Size([0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0]));
        Assert.Null(VideoFormats.Size(Ftyp("isom")));
    }
}
