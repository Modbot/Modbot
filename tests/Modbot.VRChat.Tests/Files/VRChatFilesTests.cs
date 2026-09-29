using Modbot.VRChat.Files;

namespace Modbot.VRChat.Tests.Files;

/// <summary>
/// The two rules a file fetch is held to (VRChat files design): whose address it is, and what
/// kind of file came back. Both decide whether Modbot will hand bytes to a browser, so both are
/// pinned here rather than left to the endpoint that happens to call them.
/// </summary>
public class VRChatFilesTests
{
    /// <summary>
    /// The stored address lives on <c>api.vrchat.cloud</c>; the redirect lands on whichever
    /// delivery host VRChat is using that week, and those names change without notice. So the
    /// rule is the domain, not a list of hosts somebody has to keep up to date.
    /// </summary>
    [Theory]
    [InlineData("https://api.vrchat.cloud/api/1/file/file_abc/1/file", true)]
    [InlineData("https://assets.vrchat.cloud/avatars/picture.png", true)]
    [InlineData("https://files.vrchat.cloud/thumbnails/1.jpg?x=1", true)]
    [InlineData("https://d348imysud55la.vrchat.cloud/banner.png", true)]
    [InlineData("https://vrchat.cloud/x.png", true)]
    [InlineData("HTTPS://API.VRCHAT.CLOUD/x", true)]
    public void VRChatsOwnAddressesAreAccepted(string url, bool expected)
        => Assert.Equal(expected, VRChatFiles.IsVRChatAddress(url));

    /// <summary>
    /// The route must never become a fetcher for whatever address somebody puts in the query
    /// string. A name that merely contains VRChat's is not VRChat's.
    /// </summary>
    [Theory]
    [InlineData("https://notvrchat.cloud/x.png")]
    [InlineData("https://vrchat.cloud.example.com/x.png")]
    [InlineData("https://example.com/?a=https://api.vrchat.cloud/x")]
    [InlineData("http://api.vrchat.cloud/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://localhost:8080/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/api/1/file/file_abc/1/file")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsRefused(string? url)
        => Assert.False(VRChatFiles.IsVRChatAddress(url));

    /// <summary>
    /// On the API host, where the session cookie goes, only the file and image paths are
    /// pictures. The delivery hosts never get the cookie, so their paths are left alone.
    /// </summary>
    [Theory]
    [InlineData("https://api.vrchat.cloud/api/1/file/file_abc/1/file")]
    [InlineData("https://api.vrchat.cloud/api/1/image/file_abc/1/256")]
    [InlineData("HTTPS://API.VRCHAT.CLOUD/api/1/file/file_abc/1/file")]
    [InlineData("https://api.vrchat.cloud/api/1/file/file_abc/1/file?x=1")]
    [InlineData("https://api.vrchat.cloud/api/1/file/not-an-id-shape/1/file")]
    [InlineData("https://d348imysud55la.vrchat.cloud/file_abc.png")]
    [InlineData("https://assets.vrchat.cloud/avatars/picture.png")]
    [InlineData("https://files.vrchat.cloud/thumbnails/1.jpg?x=%2f")]
    [InlineData("https://vrchat.cloud/x.png")]
    public void PictureAddressesAreAccepted(string url)
        => Assert.True(VRChatFiles.IsPictureAddress(url));

    /// <summary>
    /// Any other path on the API host is an API call, and it would go out as the service account.
    /// A path that only reaches another one after <c>..</c> or an escape is decoded counts as
    /// that other path.
    /// </summary>
    [Theory]
    [InlineData("https://api.vrchat.cloud/api/1/auth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/users/usr_abc")]
    [InlineData("https://api.vrchat.cloud/api/1/files")]
    [InlineData("https://api.vrchat.cloud/api/1/file")]
    [InlineData("https://api.vrchat.cloud/api/1/image")]
    [InlineData("https://api.vrchat.cloud/x.png")]
    [InlineData("https://api.vrchat.cloud/")]
    [InlineData("https://api.vrchat.cloud//api/1/file/file_abc/1/file")]
    [InlineData("https://api.vrchat.cloud/api/1/file/../auth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/file/../../1/auth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/file/%2e%2e/%2e%2e/1/auth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/file/%2E%2E/auth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/file/..%2f..%2fauth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/file/..%2F..%2Fauth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/file/..%5c..%5cauth/user")]
    [InlineData("https://api.vrchat.cloud/api/1/file\\..\\..\\auth/user")]
    [InlineData("http://api.vrchat.cloud/api/1/file/file_abc/1/file")]
    [InlineData("https://example.com/api/1/file/file_abc/1/file")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseOnTheApiHostIsNotAPicture(string? url)
        => Assert.False(VRChatFiles.IsPictureAddress(url));

    /// <summary>
    /// Pictures and video, nothing else. A browser handed HTML or SVG from somewhere else runs
    /// it, and the point of the route is a face in a list.
    /// </summary>
    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("IMAGE/WEBP", true)]
    [InlineData("image/png; charset=binary", true)]
    [InlineData("video/mp4", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("image/svg", false)]
    [InlineData("text/html", false)]
    [InlineData("application/json", false)]
    [InlineData("application/pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyPicturesAndVideoAreShowable(string? contentType, bool expected)
        => Assert.Equal(expected, VRChatFiles.IsShowable(contentType));

    /// <summary>
    /// SVG is a document with scripts in it. Served from Modbot's own address it runs them
    /// against whoever opens it, which is why the evidence store refuses it too.
    /// </summary>
    [Fact]
    public void SvgIsNotAPicture()
    {
        Assert.False(VRChatFiles.IsShowable("image/svg+xml"));
        Assert.False(VRChatFiles.IsShowable("IMAGE/SVG+XML; charset=utf-8"));
    }

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("IMAGE/PNG; charset=binary", "image/png")]
    [InlineData("  video/mp4  ", "video/mp4")]
    public void TheStoredTypeLosesItsParameters(string contentType, string expected)
        => Assert.Equal(expected, VRChatFiles.BareType(contentType));
}
