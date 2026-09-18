using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// Which of a person's three stored pictures Modbot shows. One rule, because the six screens
/// that show a face had already drifted apart once (VRChat files design §7).
/// </summary>
public class ProfilePicturesTests
{
    /// <summary>
    /// The override the person chose, then the icon the public profile carries, then the oldest
    /// avatar thumbnail. Newest-first by which field VRChat actually fills: profilePicOverride
    /// left every call in API specification v1.21.0, so for anyone seen since then the icon is
    /// all there is.
    /// </summary>
    [Theory]
    [InlineData("override", "icon", "thumb", "override")]
    [InlineData(null, "icon", "thumb", "icon")]
    [InlineData(null, null, "thumb", "thumb")]
    [InlineData(null, null, null, null)]
    [InlineData("override", null, null, "override")]
    [InlineData(null, "icon", null, "icon")]
    public void TheBestPictureIsTheOverrideThenTheIconThenTheThumbnail(
        string? profilePicture, string? icon, string? thumbnail, string? expected)
        => Assert.Equal(expected, ProfilePictures.Best(profilePicture, icon, thumbnail));

    /// <summary>
    /// An empty string is not a picture. VRChat's models give a missing string the value
    /// <c>""</c>, and a row written before a field moved can hold one, so a blank has to fall
    /// through to the next field rather than be shown as a broken image.
    /// </summary>
    [Theory]
    [InlineData("", "icon", "thumb", "icon")]
    [InlineData("   ", "", "thumb", "thumb")]
    [InlineData("", "", "", null)]
    public void ABlankIsNotAPicture(string? profilePicture, string? icon, string? thumbnail, string? expected)
        => Assert.Equal(expected, ProfilePictures.Best(profilePicture, icon, thumbnail));

    [Fact]
    public void AStoredRowIsReadTheSameWay()
    {
        var row = new VRChatUser
        {
            UserId = "usr_a",
            ProfilePictureUrl = null,
            IconUrl = "https://api.vrchat.cloud/api/1/file/file_icon/1/file",
            CurrentAvatarThumbnailImageUrl = "https://api.vrchat.cloud/api/1/file/file_thumb/1/file",
        };

        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_icon/1/file", ProfilePictures.Best(row));
    }

    /// <summary>No row is no picture, which is what a person Modbot has never read looks like.</summary>
    [Fact]
    public void NoRowIsNoPicture() => Assert.Null(ProfilePictures.Best(null));
}
