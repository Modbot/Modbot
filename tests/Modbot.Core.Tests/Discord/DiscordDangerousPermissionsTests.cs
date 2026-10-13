using Modbot.Core.Discord;

namespace Modbot.Core.Tests.Discord;

/// <summary>Which Discord permissions the permission watch tells staff about, and what counts as gaining one.</summary>
public class DiscordDangerousPermissionsTests
{
    private const long Administrator = 1L << 3;
    private const long ManageMessages = 1L << 13;
    private const long SendMessages = 1L << 11;
    private const long ModerateMembers = 1L << 40;

    [Fact]
    public void TheNineWatchedPermissions_AreAdministratorAndTheOnesThatLetSomebodyTakeOverOrSilenceTheServer()
    {
        Assert.Equal(
            ["Administrator", "Manage Server", "Manage Roles", "Manage Channels", "Manage Webhooks", "Ban Members", "Kick Members", "Mention Everyone", "Manage Messages"],
            DiscordDangerousPermissions.All.Select(p => p.Name));
    }

    [Fact]
    public void AnUnwatchedPermission_IsNotInTheSet()
    {
        Assert.Equal(0, DiscordDangerousPermissions.In(SendMessages | ModerateMembers));
    }

    [Fact]
    public void Gained_IsWhatTheNewSetHasAndTheOldOneDidNot()
    {
        Assert.Equal(Administrator, DiscordDangerousPermissions.Gained(ManageMessages, ManageMessages | Administrator));
    }

    [Fact]
    public void Gained_IgnoresPermissionsThatWereTakenAwayOrNotWatched()
    {
        Assert.Equal(0, DiscordDangerousPermissions.Gained(Administrator | ManageMessages, ManageMessages | SendMessages));
        Assert.Equal(0, DiscordDangerousPermissions.Gained(0, SendMessages | ModerateMembers));
    }

    [Fact]
    public void ARoleNeverSeenBefore_HadNothing()
    {
        Assert.Equal(Administrator | ManageMessages, DiscordDangerousPermissions.Gained(null, Administrator | ManageMessages | SendMessages));
    }

    [Fact]
    public void Names_AreListedStrongestFirst()
    {
        Assert.Equal(["Administrator", "Manage Messages"], DiscordDangerousPermissions.Names(ManageMessages | Administrator));
    }
}
