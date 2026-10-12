using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Core.Tests.Discord;

/// <summary>
/// What the invite link asks Discord for. Pictures are sent as files, so Attach Files is asked for
/// whenever pictures can go out.
/// </summary>
public class DiscordInviteTests
{
    [Fact]
    public void PicturesGoOut_AttachFilesIsAskedFor()
    {
        var settings = new Settings { VRChatImagesProxied = true, DiscordPostsOn = false };

        Assert.Equal(DiscordInvite.AttachFiles, DiscordInvite.PermissionsFor(settings) & DiscordInvite.AttachFiles);
    }

    [Fact]
    public void PostsToDiscordAreOn_AttachFilesIsAskedFor()
    {
        var settings = new Settings { VRChatImagesProxied = false, DiscordPostsOn = true };

        Assert.Equal(DiscordInvite.AttachFiles, DiscordInvite.PermissionsFor(settings) & DiscordInvite.AttachFiles);
    }

    [Fact]
    public void NoPicturesAndNoPosts_AttachFilesIsNotAskedFor()
    {
        var settings = new Settings { VRChatImagesProxied = false, DiscordPostsOn = false };

        Assert.Equal(0, DiscordInvite.PermissionsFor(settings) & DiscordInvite.AttachFiles);
    }

    [Fact]
    public void AttachFiles_IsDiscordsBitFifteen()
        => Assert.Equal(0x8000, DiscordInvite.AttachFiles);
}
