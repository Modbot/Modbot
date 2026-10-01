using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Notifications;
using Modbot.Core.Users;
using Modbot.TestSupport;

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

    [Fact]
    public async Task DirectMessages_StopReachingATypedId_AtTheEnd()
    {
        var messenger = new Messenger();
        var clock = new FakeClock(BeforeTheEnd);
        var channel = new DiscordNotificationChannel(messenger, clock);

        Assert.True(await channel.CanReachAsync(Typed(), TestContext.Current.CancellationToken));

        clock.UtcNow = AfterTheEnd;

        Assert.False(await channel.CanReachAsync(Typed(), TestContext.Current.CancellationToken));
        var outcome = await channel.SendAsync(Typed(), "Title", "Body", TestContext.Current.CancellationToken);
        Assert.False(outcome.Sent);
        Assert.Empty(messenger.Sent);

        Assert.True(await channel.CanReachAsync(Proven(), TestContext.Current.CancellationToken));
        await channel.SendAsync(Proven("222"), "Title", "Body", TestContext.Current.CancellationToken);
        Assert.Equal("222", Assert.Single(messenger.Sent));
    }

    private sealed class Messenger : IDiscordMessenger
    {
        public List<string> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<SendOutcome> SendDirectMessageAsync(string discordUserId, string text, CancellationToken ct = default)
        {
            Sent.Add(discordUserId);
            return Task.FromResult(SendOutcome.Ok);
        }
    }
}
