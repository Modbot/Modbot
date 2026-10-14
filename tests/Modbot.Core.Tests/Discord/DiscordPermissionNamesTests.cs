using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Core.Tests.Discord;

/// <summary>Discord's permission bits said in words, and what the invite link asks for while restore is on.</summary>
public class DiscordPermissionNamesTests
{
    [Fact]
    public void Of_NamesEachBitInDiscordsOrder()
    {
        var names = DiscordPermissionNames.Of(DiscordInvite.SendMessages | DiscordInvite.ViewChannel);

        Assert.Equal(["View Channel", "Send Messages"], names);
    }

    [Fact]
    public void Of_NothingSet_IsEmpty()
    {
        Assert.Empty(DiscordPermissionNames.Of(0));
    }

    [Fact]
    public void Of_ABitWithNoName_SaysItsNumber()
    {
        Assert.Equal(["Permission 60"], DiscordPermissionNames.Of(1L << 60));
    }

    [Fact]
    public void Line_JoinsTheNames_AndSaysNoneForNothing()
    {
        Assert.Equal("View Channel, Send Messages", DiscordPermissionNames.Line(DiscordInvite.ViewChannel | DiscordInvite.SendMessages));
        Assert.Equal("none", DiscordPermissionNames.Line(0));
    }

    [Fact]
    public void RestoreOn_ManageRolesIsAskedFor()
    {
        var settings = new Settings { DiscordSnapshotRestoreOn = true };

        Assert.Equal(DiscordInvite.ManageRoles, DiscordInvite.PermissionsFor(settings) & DiscordInvite.ManageRoles);
    }

    [Fact]
    public void RestoreOff_ManageRolesIsNotAskedFor()
    {
        var settings = new Settings { DiscordSnapshotRestoreOn = false };

        Assert.Equal(0, DiscordInvite.PermissionsFor(settings) & DiscordInvite.ManageRoles);
    }
}
