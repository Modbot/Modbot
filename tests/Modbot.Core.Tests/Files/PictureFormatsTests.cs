using System.Text;
using Modbot.Core.Files;

namespace Modbot.Core.Tests.Files;

/// <summary>
/// A picture's kind comes from its first bytes, never from what a host called it (calendar design
/// §15.2). SVG and HTML are never pictures.
/// </summary>
public class PictureFormatsTests
{
    public static TheoryData<byte[], string> Pictures() => new()
    {
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0], "image/png" },
        { [0xFF, 0xD8, 0xFF, 0xE0, 0, 0], "image/jpeg" },
        { Encoding.ASCII.GetBytes("GIF89a......"), "image/gif" },
        { Encoding.ASCII.GetBytes("GIF87a......"), "image/gif" },
        { Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 "), "image/webp" },
        { Encoding.ASCII.GetBytes("BM" + new string('\0', 30)), "image/bmp" },
        { IsoMedia("avif"), "image/avif" },
        { IsoMedia("heic"), "image/heic" },
        { IsoMedia("mif1"), "image/heic" },
    };

    /// <summary>The start of an ISO media file: a box size, "ftyp", and the brand.</summary>
    private static byte[] IsoMedia(string brand) =>
        [0, 0, 0, 0x18, .. Encoding.ASCII.GetBytes("ftyp" + brand), 0, 0, 0, 0];

    [Fact]
    public void AVideoInTheSameBoxesIsNotAPicture()
    {
        Assert.Null(PictureFormats.Sniff(IsoMedia("mp42")));
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void EachKindIsKnownByItsBytes(byte[] bytes, string expected)
    {
        Assert.Equal(expected, PictureFormats.Sniff(bytes));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg></svg>")]
    [InlineData("<!doctype html><html></html>")]
    [InlineData("{\"error\":\"nope\"}")]
    [InlineData("")]
    public void AnythingElseIsNotAPicture(string text)
    {
        Assert.Null(PictureFormats.Sniff(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/gif", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/avif", false)]
    [InlineData("image/heic", false)]
    [InlineData("image/bmp", false)]
    [InlineData(null, false)]
    public void DiscordTakesFourKinds(string? type, bool expected)
    {
        Assert.Equal(expected, PictureFormats.DiscordTakes(type));
    }
}
