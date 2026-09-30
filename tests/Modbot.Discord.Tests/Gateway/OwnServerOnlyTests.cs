using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tests.Gateway;

/// <summary>
/// A Modbot answers commands only from the server it is set up for, so several Modbots can share
/// one Discord bot without answering each other's commands.
/// </summary>
/// <remarks>
/// The check runs before the command is acknowledged: a command it turns away is never deferred,
/// answered or recorded, which leaves it to the Modbot whose server it came from.
/// </remarks>
public class OwnServerOnlyTests
{
    [Fact]
    public void ACommandFromOurServer_IsOurs()
        => Assert.True(DiscordNetGateway.IsForThisServer("424242", 424242UL));

    [Fact]
    public void ACommandFromAnotherServer_IsLeftAlone()
        => Assert.False(DiscordNetGateway.IsForThisServer("424242", 999999UL));

    [Fact]
    public void ACommandInADirectMessage_IsLeftAlone()
        => Assert.False(DiscordNetGateway.IsForThisServer("424242", null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithNoServerSet_NothingIsAnswered(string? ours)
        => Assert.False(DiscordNetGateway.IsForThisServer(ours, 424242UL));

    [Fact]
    public void ServerIdsAreComparedWhole_NotByTheirStart()
        => Assert.False(DiscordNetGateway.IsForThisServer("4242", 424242UL));
}
