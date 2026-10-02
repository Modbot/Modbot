using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Step = Modbot.Core.Users.StaffRoles.BothWaysStep;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// The pure parts of staff roles from Discord: which side a both-ways mapping copies, the brake,
/// and who a mapping reaches (design 2026-10-02 §3.1, §4, §6). No database, no host.
/// </summary>
public class StaffRolesTests
{
    private static readonly DateTimeOffset Agreed = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static DiscordStaffRoleState State(bool held) => new() { Held = held, AgreedAt = Agreed };

    // ── Both ways: the table in §3.1 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, Step.Agree)]
    [InlineData(false, false, Step.Agree)]
    [InlineData(true, false, Step.FollowDiscord)]
    [InlineData(false, true, Step.FollowModbot)]
    public void TheFirstTimeWhicheverSideHoldsItGivesItToTheOther(bool discord, bool modbot, Step expected)
        => Assert.Equal(expected, StaffRoles.BothWays(null, discord, modbot, Agreed.AddMinutes(1)));

    [Fact]
    public void NothingChangedIsNothingToDo()
        => Assert.Equal(Step.Nothing, StaffRoles.BothWays(State(true), true, true, Agreed.AddMinutes(1)));

    [Fact]
    public void AChangeInDiscordIsFollowedInModbot()
        => Assert.Equal(Step.FollowDiscord, StaffRoles.BothWays(State(true), false, true, Agreed.AddMinutes(1)));

    [Fact]
    public void AChangeInModbotIsFollowedInDiscord()
        => Assert.Equal(Step.FollowModbot, StaffRoles.BothWays(State(false), false, true, Agreed.AddMinutes(1)));

    /// <summary>
    /// The no-bounce rule: the bot gave the Discord role and wrote the agreement, but the member row
    /// still shows the roles from before. A row not changed since the agreement is not Discord
    /// changing its mind.
    /// </summary>
    [Fact]
    public void AMemberRowNotChangedSinceTheAgreementIsNotADiscordChange()
        => Assert.Equal(Step.Nothing, StaffRoles.BothWays(State(true), false, true, Agreed.AddSeconds(-5)));

    [Fact]
    public void BothChangedAndNowAgreeIsWrittenDown()
        => Assert.Equal(Step.Agree, StaffRoles.BothWays(State(true), false, false, Agreed.AddMinutes(1)));

    [Fact]
    public void SomebodyNeverSeenInTheServerCountsAsAChange()
        => Assert.Equal(Step.FollowDiscord, StaffRoles.BothWays(State(true), false, true, null));

    // ── The brake ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, 4, true)]
    [InlineData(3, 10, false)]
    [InlineData(5, 20, false)]
    [InlineData(6, 100, true)]
    public void TheBrakeStopsTakingFromManyAtOnce(int losing, int covered, bool brakes)
        => Assert.Equal(brakes, StaffRoles.Brakes(losing, covered));

    // ── Who is reached ─────────────────────────────────────────────────────────────────────

    private static ModbotUser Account(bool proven = true, bool disabled = false, ModbotPermissions role = ModbotPermissions.None)
    {
        var user = new ModbotUser
        {
            Username = "someone",
            DiscordUserId = "5001",
            DiscordVerifiedAt = proven ? Agreed : null,
            IsDisabled = disabled,
        };

        if (role != ModbotPermissions.None)
        {
            var r = new ModbotRole { Name = "r", Permissions = role };
            user.Roles.Add(new ModbotUserRole { User = user, Role = r, RoleId = r.Id });
        }

        return user;
    }

    [Fact]
    public void AProvenEnabledAccountIsReached() => Assert.True(StaffRoles.IsCovered(Account()));

    [Fact]
    public void ATypedInDiscordAccountIsNeverReached() => Assert.False(StaffRoles.IsCovered(Account(proven: false)));

    [Fact]
    public void ADisabledAccountIsNotReached() => Assert.False(StaffRoles.IsCovered(Account(disabled: true)));

    [Fact]
    public void AnAdministratorIsNeverReached()
        => Assert.False(StaffRoles.IsCovered(Account(role: ModbotPermissions.Administrator)));

    [Fact]
    public void EveryoneManagedAndRemovedRolesCannotBeMapped()
    {
        Assert.False(StaffRoles.CanMap(new DiscordRole { Everyone = true }));
        Assert.False(StaffRoles.CanMap(new DiscordRole { Managed = true }));
        Assert.False(StaffRoles.CanMap(new DiscordRole { RemovedAt = Agreed }));
        Assert.True(StaffRoles.CanMap(new DiscordRole { Name = "Staff" }));
    }
}
