using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Core.Tests.Discord;

/// <summary>
/// Roles from lists design §4 and §5: which Discord roles a list may give, and when a pass stops and
/// waits for Apply.
/// </summary>
public class ListRoleChecksTests
{
    private const string Guild = "700";

    /// <summary>View Channels and Send Messages: what a community role usually carries.</summary>
    private const long Community = (1L << 10) | (1L << 11);

    private static DiscordRole Role(long? permissions = Community, Action<DiscordRole>? change = null)
    {
        var role = new DiscordRole
        {
            RoleId = "802",
            GuildId = Guild,
            Name = "Regular",
            BotCanAssign = true,
            Permissions = permissions,
        };

        change?.Invoke(role);
        return role;
    }

    [Fact]
    public void ACommunityRoleCanBeGiven()
        => Assert.Null(ListRoleChecks.WhyNot(Role(), decidedElsewhere: null));

    /// <summary>Every staff power the design lists refuses the role on its own.</summary>
    [Theory]
    [InlineData(3, "Administrator")]
    [InlineData(5, "Manage Server")]
    [InlineData(28, "Manage Roles")]
    [InlineData(4, "Manage Channels")]
    [InlineData(13, "Manage Messages")]
    [InlineData(2, "Ban Members")]
    [InlineData(1, "Kick Members")]
    [InlineData(40, "Timeout Members")]
    [InlineData(17, "Mention @everyone, @here and All Roles")]
    [InlineData(29, "Manage Webhooks")]
    [InlineData(27, "Manage Nicknames")]
    [InlineData(34, "Manage Threads")]
    [InlineData(33, "Manage Events")]
    [InlineData(30, "Manage Expressions")]
    [InlineData(7, "View Audit Log")]
    [InlineData(22, "Mute Members")]
    [InlineData(23, "Deafen Members")]
    [InlineData(24, "Move Members")]
    public void ARoleWithAStaffPowerIsRefused(int bit, string name)
    {
        var why = ListRoleChecks.WhyNot(Role(Community | (1L << bit)), decidedElsewhere: null);

        Assert.NotNull(why);
        Assert.Contains(name, why, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyStaffPowersAreNamedAFewAtATime()
    {
        var why = ListRoleChecks.WhyNot(Role((1L << 3) | (1L << 5) | (1L << 28) | (1L << 2)), decidedElsewhere: null);

        Assert.Equal("Regular has staff permissions: Administrator, Manage Server, Manage Roles and 1 more. A list only gives roles without them.", why);
    }

    [Fact]
    public void EveryoneIsRefused()
    {
        Assert.NotNull(ListRoleChecks.WhyNot(Role(change: r => r.Everyone = true), null));
        Assert.NotNull(ListRoleChecks.WhyNot(Role(change: r => r.RoleId = Guild), null));
    }

    [Fact]
    public void ABotsRoleIsRefused()
        => Assert.NotNull(ListRoleChecks.WhyNot(Role(change: r => r.Managed = true), null));

    [Fact]
    public void ARoleAboveTheBotIsRefused()
    {
        var why = ListRoleChecks.WhyNot(Role(change: r => r.BotCanAssign = false), null);
        Assert.Equal("The bot cannot give Regular. Give it Manage Roles and keep its own role above that one.", why);
    }

    /// <summary>Unread permissions are not taken to mean "none": the role might be a staff role.</summary>
    [Fact]
    public void ARoleWhosePermissionsAreNotReadYetIsRefused()
        => Assert.NotNull(ListRoleChecks.WhyNot(Role(permissions: null), null));

    [Fact]
    public void AGoneOrUnknownRoleIsRefused()
    {
        Assert.NotNull(ListRoleChecks.WhyNot(null, null));
        Assert.NotNull(ListRoleChecks.WhyNot(Role(change: r => r.RemovedAt = DateTimeOffset.UnixEpoch), null));
    }

    [Fact]
    public void ARoleSomethingElseDecidesIsRefusedWithItsReason()
        => Assert.Equal("Another list already gives that role.", ListRoleChecks.WhyNot(Role(), "Another list already gives that role."));

    // ── The brake ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)] // under the floor, even if it is everybody
    [InlineData(3, 6, false)] // exactly half is not more than half
    [InlineData(3, 5, true)] // more than half the holders
    [InlineData(25, 1000, false)] // 25 is allowed on a big role
    [InlineData(26, 1000, true)] // more than 25 always stops
    [InlineData(10, 100, false)]
    public void TheBrakeStopsMassRemovals(int taking, int holders, bool stops)
        => Assert.Equal(stops, ListRoleChecks.Brakes(taking, holders));
}
