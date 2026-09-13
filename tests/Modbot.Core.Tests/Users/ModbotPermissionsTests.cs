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
}
