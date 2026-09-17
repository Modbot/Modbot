using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Users;

public class ModbotPermissionsTests
{
    /// <summary>
    /// The bitfield is persisted, so a value changing meaning silently re-grants or revokes
    /// permissions on every existing account. This test is the reason nobody may renumber.
    /// </summary>
    [Fact]
    public void FlagValuesAreStable()
    {
        Assert.Equal(0L, (long)ModbotPermissions.None);
        Assert.Equal(1L << 0, (long)ModbotPermissions.ViewMembers);
        Assert.Equal(1L << 3, (long)ModbotPermissions.ViewAuditLog);
        Assert.Equal(1L << 4, (long)ModbotPermissions.ViewOperationalLog);
        Assert.Equal(1L << 6, (long)ModbotPermissions.ManageUsers);
        Assert.Equal(1L << 19, (long)ModbotPermissions.ManageRoles);
        Assert.Equal(1L << 20, (long)ModbotPermissions.ViewLiveInstances);
        Assert.Equal(1L << 21, (long)ModbotPermissions.UseAiChat);
        Assert.Equal(1L << 22, (long)ModbotPermissions.ManageDiscordLinks);
        Assert.Equal(1L << 23, (long)ModbotPermissions.UseAiPastLimits);
        Assert.Equal(1L << 24, (long)ModbotPermissions.ReadDiscordMessages);
        Assert.Equal(1L << 25, (long)ModbotPermissions.ViewCalendar);
        Assert.Equal(1L << 26, (long)ModbotPermissions.ManageCalendar);
        Assert.Equal(1L << 18, (long)ModbotPermissions.EditAgeVerification);
        Assert.Equal(1L << 62, (long)ModbotPermissions.Administrator);
    }

    [Fact]
    public void EveryFlagIsADistinctSingleBit()
    {
        var seen = new HashSet<long>();

        foreach (var value in Enum.GetValues<ModbotPermissions>())
        {
            var bits = (long)value;
            if (bits == 0)
                continue;

            Assert.True(long.PopCount(bits) == 1, $"{value} is not a single bit.");
            Assert.True(seen.Add(bits), $"{value} reuses a bit already assigned to another flag.");
        }
    }

    /// <summary>
    /// Chat sends group data to the operator's AI provider, so the permission is granted on
    /// purpose rather than arriving with a built-in role (AI chat design §4).
    /// </summary>
    [Fact]
    public void TheAiPermissions_AreNotInTheEditableBuiltInRoles()
    {
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.UseAiChat));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.UseAiChat));
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.UseAiPastLimits));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.UseAiPastLimits));
    }

    /// <summary>Reading somebody's messages, deleted ones included, is granted on purpose.</summary>
    [Fact]
    public void ReadDiscordMessages_IsNotInTheEditableBuiltInRoles()
    {
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.ReadDiscordMessages));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.ReadDiscordMessages));
    }

    /// <summary>An event can open an instance and post in the group's name, so managing it is granted on purpose.</summary>
    [Fact]
    public void TheCalendarPermissions_AreNotInTheEditableBuiltInRoles()
    {
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.ManageCalendar));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.ManageCalendar));
    }
}
