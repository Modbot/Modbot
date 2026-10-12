using Modbot.VRChat.Users;

namespace Modbot.VRChat.Tests.Users;

/// <summary>Whether an account has VRChat+, read off its tags.</summary>
public class VRChatPlusTests
{
    [Fact]
    public void TheSupporterTagMeansVRChatPlus()
    {
        Assert.True(VRChatPlus.FromTags(["system_trust_known", "system_supporter"]));
    }

    [Fact]
    public void TagsWithoutItMeanNoVRChatPlus()
    {
        Assert.False(VRChatPlus.FromTags(["system_trust_known", "language_eng"]));
    }

    [Fact]
    public void NoTagsAtAllIsNotKnown_NotNo()
    {
        Assert.Null(VRChatPlus.FromTags(null));
        Assert.Null(VRChatPlus.FromTags([]));
    }

    [Fact]
    public void ALookAlikeTagIsNotTheSupporterTag()
    {
        Assert.False(VRChatPlus.FromTags(["system_supporter_old", "SYSTEM_SUPPORTER"]));
    }
}
