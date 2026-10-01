using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// Which Discord id counts for a staff account (accounts and access design §4.6): a proven one
/// always, a typed one only until the day typed ids stop counting.
/// </summary>
public class StaffDiscordTests
{
    private static readonly DateTimeOffset BeforeTheEnd = StaffDiscord.TypedIdsEnd.AddDays(-1);
    private static readonly DateTimeOffset AfterTheEnd = StaffDiscord.TypedIdsEnd.AddDays(1);

    private static ModbotUser Typed(string id = "111") => new() { DiscordUserId = id };

    private static ModbotUser Proven(string id = "111") => new()
    {
        DiscordUserId = id,
        DiscordUsername = "someone",
        DiscordVerifiedAt = BeforeTheEnd.AddDays(-10),
    };

    [Fact]
    public void AProvenId_CountsBeforeAndAfterTheEnd()
    {
        Assert.Equal("111", StaffDiscord.IdOf(Proven(), BeforeTheEnd));
        Assert.Equal("111", StaffDiscord.IdOf(Proven(), AfterTheEnd));
    }

    [Fact]
    public void ATypedId_CountsUntilTheEnd_AndNotFromThen()
    {
        Assert.Equal("111", StaffDiscord.IdOf(Typed(), BeforeTheEnd));
        Assert.Null(StaffDiscord.IdOf(Typed(), StaffDiscord.TypedIdsEnd));
        Assert.Null(StaffDiscord.IdOf(Typed(), AfterTheEnd));
    }

    [Fact]
    public void NoId_IsNothing()
    {
        Assert.Null(StaffDiscord.IdOf(new ModbotUser(), BeforeTheEnd));
        Assert.Null(StaffDiscord.IdOf(new ModbotUser { DiscordUserId = string.Empty }, BeforeTheEnd));
        Assert.False(new ModbotUser { DiscordVerifiedAt = BeforeTheEnd }.IsDiscordProven);
    }
}
