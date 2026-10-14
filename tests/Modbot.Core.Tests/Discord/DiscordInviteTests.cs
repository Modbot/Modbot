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
    public void EventRecapsAreOn_CreatePublicThreadsIsAskedFor()
    {
        var settings = new Settings { EventRecapsOn = true };

        Assert.Equal(DiscordInvite.CreatePublicThreads, DiscordInvite.PermissionsFor(settings) & DiscordInvite.CreatePublicThreads);
    }

    [Fact]
    public void EventRecapsAreOff_CreatePublicThreadsIsNotAskedFor()
    {
        var settings = new Settings { EventRecapsOn = false };

        Assert.Equal(0, DiscordInvite.PermissionsFor(settings) & DiscordInvite.CreatePublicThreads);
    }

    [Fact]
    public void ARegularRoleIsSet_ManageRolesIsAskedFor()
    {
        var settings = new Settings { MilestoneRegularRoleId = "100" };

        Assert.Equal(DiscordInvite.ManageRoles, DiscordInvite.PermissionsFor(settings) & DiscordInvite.ManageRoles);
    }

    [Fact]
    public void NoRegularRole_ManageRolesIsNotAskedFor()
    {
        var settings = new Settings { MilestoneRegularRoleId = null };

        Assert.Equal(0, DiscordInvite.PermissionsFor(settings) & DiscordInvite.ManageRoles);
    }

    [Fact]
    public void InviteTrackingIsOn_ManageServerAndCreateInviteAreAskedFor()
    {
        var permissions = DiscordInvite.PermissionsFor(new Settings { DiscordInviteTrackingOn = true });

        Assert.Equal(DiscordInvite.ManageGuild, permissions & DiscordInvite.ManageGuild);
        Assert.Equal(DiscordInvite.CreateInvite, permissions & DiscordInvite.CreateInvite);
    }

    [Fact]
    public void InviteTrackingIsOff_ManageServerAndCreateInviteAreNotAskedFor()
    {
        var permissions = DiscordInvite.PermissionsFor(new Settings { DiscordInviteTrackingOn = false });

        Assert.Equal(0, permissions & DiscordInvite.ManageGuild);
        Assert.Equal(0, permissions & DiscordInvite.CreateInvite);
    }

    [Fact]
    public void CreateInvite_IsDiscordsBitZero()
        => Assert.Equal(1L, DiscordInvite.CreateInvite);

    [Fact]
    public void ManageGuild_IsDiscordsBitFive()
        => Assert.Equal(0x20, DiscordInvite.ManageGuild);

    [Fact]
    public void CreatePublicThreads_IsDiscordsBitThirtyFive()
        => Assert.Equal(1L << 35, DiscordInvite.CreatePublicThreads);

    [Fact]
    public void AttachFiles_IsDiscordsBitFifteen()
        => Assert.Equal(0x8000, DiscordInvite.AttachFiles);
}
