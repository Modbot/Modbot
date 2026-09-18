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
        Assert.Equal(1L << 27, (long)ModbotPermissions.UseVRChatProxy);
        Assert.Equal(1L << 28, (long)ModbotPermissions.ViewGiveaways);
        Assert.Equal(1L << 29, (long)ModbotPermissions.RunGiveaways);
        Assert.Equal(1L << 30, (long)ModbotPermissions.ImportOldData);
        Assert.Equal(1L << 32, (long)ModbotPermissions.ViewJoinRequests);
        Assert.Equal(1L << 33, (long)ModbotPermissions.AnswerJoinRequests);
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

    /// <summary>
    /// An entrant list names people and says how long each spends here, and a draw decides who
    /// gets something, so both are granted on purpose (giveaways design §8.2).
    /// </summary>
    [Fact]
    public void TheGiveawayPermissions_AreNotInTheEditableBuiltInRoles()
    {
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.ViewGiveaways));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.ViewGiveaways));
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.RunGiveaways));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.RunGiveaways));
    }

    /// <summary>
    /// An import writes history that did not happen inside Modbot, which is a larger power than
    /// changing a setting (import design §4). It rides on nothing else and is in no built-in role.
    /// </summary>
    [Fact]
    public void ImportOldData_StandsAlone_AndIsNotInTheEditableBuiltInRoles()
    {
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.ImportOldData));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.ImportOldData));
        Assert.False(ModbotPermissions.ManageSettings.HasFlag(ModbotPermissions.ImportOldData));
    }

    /// <summary>
    /// Approving a join request puts a stranger inside the group, which is a different decision
    /// from removing somebody already in it (join requests design §6). Neither flag rides on any
    /// other, and neither is in a built-in role.
    /// </summary>
    [Fact]
    public void TheJoinRequestPermissions_StandAlone_AndAreNotInTheEditableBuiltInRoles()
    {
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.ViewJoinRequests));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.ViewJoinRequests));
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.AnswerJoinRequests));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.AnswerJoinRequests));

        Assert.False(ModbotPermissions.ViewMembers.HasFlag(ModbotPermissions.ViewJoinRequests));
        Assert.False(ModbotPermissions.Ban.HasFlag(ModbotPermissions.AnswerJoinRequests));
        Assert.False(ModbotPermissions.ViewJoinRequests.HasFlag(ModbotPermissions.AnswerJoinRequests));
    }
}
