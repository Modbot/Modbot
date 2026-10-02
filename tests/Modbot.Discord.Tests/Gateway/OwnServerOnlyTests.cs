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

    // Buttons (the two under /me) follow the same rule before they are acknowledged.

    [Fact]
    public void OurButtonInOurServer_IsOurs()
        => Assert.True(DiscordNetGateway.IsOurButton("424242", 424242UL, "modbot:me:keeps"));

    [Fact]
    public void OurButtonInAnotherServer_IsLeftAlone()
        => Assert.False(DiscordNetGateway.IsOurButton("424242", 999999UL, "modbot:me:delete"));

    [Fact]
    public void AButtonInADirectMessage_IsLeftAlone()
        => Assert.False(DiscordNetGateway.IsOurButton("424242", null, "modbot:me:keeps"));

    /// <summary>The join gate's Get in, in a direct message, carries this server's mark (join gate design §4).</summary>
    [Fact]
    public void AButtonInADirectMessage_MarkedWithThisServer_IsOurs()
        => Assert.True(DiscordNetGateway.IsOurButton("424242", null, DiscordActionButton.Marked("modbot:gate:in", "424242")));

    [Fact]
    public void AButtonInADirectMessage_MarkedWithAnotherServer_IsLeftAlone()
        => Assert.False(DiscordNetGateway.IsOurButton("424242", null, DiscordActionButton.Marked("modbot:gate:in", "999999")));

    [Fact]
    public void TheServerMark_ComesOffForComparing()
        => Assert.Equal("modbot:gate:in", DiscordActionButton.Plain(DiscordActionButton.Marked("modbot:gate:in", "424242")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithNoServerSet_NoButtonIsAnswered(string? ours)
        => Assert.False(DiscordNetGateway.IsOurButton(ours, 424242UL, "modbot:me:keeps"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other-bot:vote")]
    [InlineData("me:keeps")]
    public void AButtonWithoutModbotsPrefix_IsLeftAlone_EvenInOurServer(string? buttonId)
        => Assert.False(DiscordNetGateway.IsOurButton("424242", 424242UL, buttonId));
}
