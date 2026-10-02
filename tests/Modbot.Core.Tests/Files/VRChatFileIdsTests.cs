using Modbot.Core.Files;

namespace Modbot.Core.Tests.Files;

/// <summary>
/// The VRChat file id inside whatever was pasted for an event's VRChat picture (calendar design
/// §15.1): the id itself, or any VRChat link with one in it. Found, never judged by its shape.
/// </summary>
public class VRChatFileIdsTests
{
    private const string Id = "file_6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";

    [Theory]
    [InlineData(Id)]
    [InlineData("  " + Id + "  ")]
    [InlineData("https://api.vrchat.cloud/api/1/file/" + Id + "/1/file")]
    [InlineData("https://api.vrchat.cloud/api/1/file/" + Id + "/1")]
    [InlineData("https://api.vrchat.cloud/api/1/file/" + Id)]
    [InlineData("https://api.vrchat.cloud/api/1/image/" + Id + "/1/1024")]
    [InlineData("https://api.vrchat.cloud/api/1/file/" + Id + "/1/file?width=512&height=288")]
    [InlineData("https://files.vrchat.cloud/thumbnails/" + Id + ".png")]
    [InlineData("https://vrchat.com/home/gallery/" + Id)]
    [InlineData(Id + "_blob")]
    [InlineData(Id + "/1/file")]
    [InlineData(Id + "?ex=1&hm=2")]
    [InlineData("api/1/file/" + Id + "/1/file_blob")]
    public void TheIdIsTakenOutOfAnyVRChatLinkOrPaste(string pasted)
    {
        Assert.Equal(Id, VRChatFileIds.Find(pasted));
    }

    /// <summary>The usual form is taken whatever case VRChat's link wrote it in.</summary>
    [Fact]
    public void TheUsualFormIsFoundInCapitals()
    {
        Assert.Equal("file_6F1C2A3B-4D5E-4F60-8A71-92B3C4D5E6F7", VRChatFileIds.Find("FILE_x file_6F1C2A3B-4D5E-4F60-8A71-92B3C4D5E6F7_blob"));
    }

    /// <summary>
    /// An id not in the usual form is still an id VRChat may have issued (foundation §3.1.1): it is
    /// taken up to the first character that cannot be part of one in a link, and never refused.
    /// </summary>
    [Theory]
    [InlineData("file_legacy123", "file_legacy123")]
    [InlineData("https://api.vrchat.cloud/api/1/file/file_legacy123/4/file", "file_legacy123")]
    [InlineData("file_odd-but_real.png", "file_odd-but_real")]
    public void AnOddIdIsKept(string pasted, string expected)
    {
        Assert.Equal(expected, VRChatFileIds.Find(pasted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("file_")]
    [InlineData("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7")]
    [InlineData("https://api.vrchat.cloud/api/1/worlds/wrld_1")]
    // "file_" on somebody else's site is somebody else's file.
    [InlineData("https://example.com/uploads/" + Id + ".png")]
    [InlineData("https://notvrchat.cloud/" + Id)]
    public void WithNoVRChatFileIdThereIsNone(string? pasted)
    {
        Assert.Null(VRChatFileIds.Find(pasted));
    }

    /// <summary>Only a link on VRChat's own hosts fills the VRChat picture from the picture link.</summary>
    [Theory]
    [InlineData("https://api.vrchat.cloud/api/1/file/" + Id + "/1/file", Id)]
    [InlineData("https://vrchat.com/home/gallery/" + Id, Id)]
    [InlineData(Id, null)]
    [InlineData("https://pictures.example/" + Id + ".png", null)]
    [InlineData("https://api.vrchat.cloud/api/1/worlds/wrld_1", null)]
    [InlineData(null, null)]
    public void OnlyAVRChatLinkHasAnIdAsAPictureLink(string? link, string? expected)
    {
        Assert.Equal(expected, VRChatFileIds.InLink(link));
    }

    [Theory]
    [InlineData("api.vrchat.cloud", true)]
    [InlineData("files.vrchat.cloud", true)]
    [InlineData("vrchat.cloud.", true)]
    [InlineData("vrchat.com", false)]
    [InlineData("evilvrchat.cloud", false)]
    [InlineData("vrchat.cloud.example.com", false)]
    public void OnlyVRChatsFileHostsNeedItsSession(string host, bool expected)
    {
        Assert.Equal(expected, VRChatFileIds.IsVRChatFileHost(host));
    }

    [Fact]
    public void TheRefusalShowsAnExample()
    {
        Assert.Contains(VRChatFileIds.Example, VRChatFileIds.NotFound, StringComparison.Ordinal);
        Assert.Equal(VRChatFileIds.Example, VRChatFileIds.Find(VRChatFileIds.Example));
    }
}
