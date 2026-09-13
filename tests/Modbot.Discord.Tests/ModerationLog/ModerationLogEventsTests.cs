using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>The closed list that keeps account facts out of Discord.</summary>
public class ModerationLogEventsTests
{
    [Fact]
    public void Nothing_Stored_MeansTheDefaults()
    {
        var set = ModerationLogEvents.Parse(null);

        Assert.Equal(ModerationLogEvents.Defaults.ToHashSet(), set);
        Assert.Contains(FactType.MemberBanned, set);
        Assert.Contains(FactType.GroupInstanceWarn, set);
        Assert.Contains(FactType.RoleGranted, set);
    }

    [Fact]
    public void AnEmptyColumn_PostsNothing()
    {
        Assert.Empty(ModerationLogEvents.Parse(string.Empty));
    }

    [Fact]
    public void AccountFacts_AreDropped_HoweverTheyGotIntoTheColumn()
    {
        var set = ModerationLogEvents.Parse(
            $"{FactType.ResetLinkCreated}, {FactType.Login},{FactType.MemberBanned} , nonsense");

        Assert.Equal([FactType.MemberBanned], set);
    }

    [Fact]
    public void Serialize_KeepsDisplayOrder_AndDropsStrangers()
    {
        var stored = ModerationLogEvents.Serialize([FactType.RoleRevoked, FactType.PasswordChanged, FactType.MemberBanned]);

        Assert.Equal($"{FactType.MemberBanned},{FactType.RoleRevoked}", stored);
    }

    [Fact]
    public void Serialize_OfNothing_IsEmpty_NotNull()
    {
        // Empty string reads back as "post nothing"; null would read back as the defaults.
        Assert.Equal(string.Empty, ModerationLogEvents.Serialize([]));
    }

    [Fact]
    public void EverythingAllowed_IsAGroupAuditLogType()
    {
        Assert.All(ModerationLogEvents.Allowed, t => Assert.StartsWith("vrchat.group.", t, StringComparison.Ordinal));
    }
}
