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
    private const string Discord = "5001";

    private static readonly DateTimeOffset Agreed = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static DiscordStaffRoleState State(bool held, string discordUserId = Discord)
        => new() { Held = held, AgreedAt = Agreed, DiscordUserId = discordUserId };

    // ── Both ways: the table in §3.1 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, Step.Agree)]
    [InlineData(false, false, Step.Agree)]
    [InlineData(true, false, Step.FollowDiscord)]
    [InlineData(false, true, Step.FollowModbot)]
    public void TheFirstTimeWhicheverSideHoldsItGivesItToTheOther(bool discord, bool modbot, Step expected)
        => Assert.Equal(expected, StaffRoles.BothWays(null, Discord, false, discord, modbot, Agreed.AddMinutes(1)));

    [Fact]
    public void NothingChangedIsNothingToDo()
        => Assert.Equal(Step.Nothing, StaffRoles.BothWays(State(true), Discord, false, true, true, Agreed.AddMinutes(1)));

    [Fact]
    public void AChangeInDiscordIsFollowedInModbot()
        => Assert.Equal(Step.FollowDiscord, StaffRoles.BothWays(State(true), Discord, false, false, true, Agreed.AddMinutes(1)));

    [Fact]
    public void AChangeInModbotIsFollowedInDiscord()
        => Assert.Equal(Step.FollowModbot, StaffRoles.BothWays(State(false), Discord, false, false, true, Agreed.AddMinutes(1)));

    /// <summary>
    /// The no-bounce rule: the bot gave the Discord role and wrote the agreement, but the member row
    /// still shows the roles from before. A row not changed since the agreement is not Discord
    /// changing its mind.
    /// </summary>
    [Fact]
    public void AMemberRowNotChangedSinceTheAgreementIsNotADiscordChange()
        => Assert.Equal(Step.Nothing, StaffRoles.BothWays(State(true), Discord, false, false, true, Agreed.AddSeconds(-5)));

    [Fact]
    public void BothChangedAndNowAgreeIsWrittenDown()
        => Assert.Equal(Step.Agree, StaffRoles.BothWays(State(true), Discord, false, false, false, Agreed.AddMinutes(1)));

    /// <summary>
    /// What was agreed about another Discord account says nothing. A newly proven account that lacks
    /// the Discord role loses the Modbot role rather than being handed the Discord role.
    /// </summary>
    [Fact]
    public void AnAgreementAboutAnotherDiscordAccountDoesNotCount()
    {
        Assert.Equal(Step.FollowDiscord, StaffRoles.BothWays(State(true, "9999"), Discord, true, false, true, Agreed.AddMinutes(1)));
        Assert.Equal(Step.FollowModbot, StaffRoles.BothWays(State(true, "9999"), Discord, false, false, true, Agreed.AddMinutes(1)));
    }

    [Theory]
    [InlineData(true, false, Step.FollowDiscord)]
    [InlineData(false, true, Step.FollowDiscord)]
    [InlineData(true, true, Step.Agree)]
    public void WhenDiscordDecidesForWantOfAnAgreementDiscordIsFollowed(bool discord, bool modbot, Step expected)
        => Assert.Equal(expected, StaffRoles.BothWays(null, Discord, true, discord, modbot, Agreed));

    [Fact]
    public void SomebodyNeverSeenInTheServerCountsAsAChange()
        => Assert.Equal(Step.FollowDiscord, StaffRoles.BothWays(State(true), Discord, false, false, true, null));

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

    // ── Not set up ─────────────────────────────────────────────────────────────────────────

    private static readonly DiscordRole Assignable = new() { RoleId = "801", BotCanAssign = true, UpdatedAt = Agreed.AddDays(-10) };

    private static StaffRoleRule BothWays(DateTimeOffset? refusedAt = null)
        => new(Guid.CreateVersion7(), "801", Guid.CreateVersion7(), StaffRoleDirections.Both, Agreed, refusedAt);

    [Fact]
    public void ABothWaysRoleTheBotCanGiveWorks()
        => Assert.True(StaffRoles.Works(BothWays(), Assignable, Assignable.UpdatedAt, Agreed));

    [Fact]
    public void ARoleTheBotCannotAssignIsNotSetUp()
        => Assert.False(StaffRoles.Works(BothWays(), new DiscordRole { BotCanAssign = false }, null, Agreed));

    [Fact]
    public void ARefusalIsNotSetUpForADayUnlessARoleChanges()
    {
        var rule = BothWays(refusedAt: Agreed);

        Assert.False(StaffRoles.Works(rule, Assignable, Assignable.UpdatedAt, Agreed.AddHours(23)));
        Assert.True(StaffRoles.Works(rule, Assignable, Assignable.UpdatedAt, Agreed.AddDays(1)));
        Assert.True(StaffRoles.Works(rule, Assignable, Agreed.AddMinutes(5), Agreed.AddMinutes(10)));
    }

    [Fact]
    public void ADiscordDecidesMappingNeverWorksBothWays()
        => Assert.False(StaffRoles.Works(BothWays() with { Direction = StaffRoleDirections.Discord }, Assignable, null, Agreed));

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L << 3, true)]
    [InlineData(1L << 1, true)]
    [InlineData(1L << 2, true)]
    [InlineData(1L << 5, true)]
    [InlineData(1L << 28, true)]
    [InlineData(1L << 10, false)]
    public void ARoleWithPowerOverTheServerIsPowerful(long permissions, bool powerful)
        => Assert.Equal(powerful, StaffRoles.IsPowerful(new DiscordRole { Permissions = permissions }));

    [Fact]
    public void ARoleWhosePermissionsAreNotReadYetCountsAsPowerful()
        => Assert.True(StaffRoles.IsPowerful(new DiscordRole { Permissions = null }));

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
