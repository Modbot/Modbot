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

    // Every other kind of interaction runs the same two tests before it is acknowledged (Discord
    // commands design §3.10): slash commands, autocomplete and the right-click menus ask
    // IsForThisServer, and buttons and forms ask IsOurButton, which asks IsForThisServer first. So
    // each kind is checked here through the test it uses.

    /// <summary>Slash commands, menus and autocomplete: another server's, and a direct message, are never acknowledged.</summary>
    [Theory]
    [InlineData("424242", 424242UL, true)]
    [InlineData("424242", 999999UL, false)]
    [InlineData("424242", null, false)]
    [InlineData(null, 424242UL, false)]
    public void ACommandMenuOrSuggestion_IsOursOnlyFromOurServer(string? ours, ulong? from, bool expected)
        => Assert.Equal(expected, DiscordNetGateway.IsForThisServer(ours, from));

    /// <summary>A form's id carries the prefix like a button's, and is held to the same test.</summary>
    [Theory]
    [InlineData("424242", 424242UL, "modbot:form:note:v:usr_1", true)]
    [InlineData("424242", 999999UL, "modbot:form:note:v:usr_1", false)]
    [InlineData("424242", null, "modbot:form:note:v:usr_1", false)]
    [InlineData("424242", 424242UL, "someone-elses:form", false)]
    public void AForm_IsOursOnlyFromOurServer_WithOurPrefix(string ours, ulong? from, string id, bool expected)
        => Assert.Equal(expected, DiscordNetGateway.IsOurButton(ours, from, id));

    /// <summary>A reminder's Stop in a direct message will carry the mark; another server's mark is left alone.</summary>
    [Fact]
    public void ADirectMessageForm_WithAnotherServersMark_IsLeftAlone()
        => Assert.False(DiscordNetGateway.IsOurButton("424242", null, DiscordActionButton.Marked("modbot:remind:stop:9", "999999")));

    /// <summary>The join gate's buttons are still this Modbot's in a direct message, by the mark, and in the server by the id.</summary>
    [Fact]
    public void TheJoinGatesButtons_StillPassTheGuard_InTheServerAndInADirectMessage()
    {
        Assert.True(DiscordNetGateway.IsOurButton("424242", 424242UL, "modbot:gate:in"));
        Assert.True(DiscordNetGateway.IsOurButton("424242", null, DiscordActionButton.Marked("modbot:gate:in", "424242")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other-bot:vote")]
    [InlineData("me:keeps")]
    public void AButtonWithoutModbotsPrefix_IsLeftAlone_EvenInOurServer(string? buttonId)
        => Assert.False(DiscordNetGateway.IsOurButton("424242", 424242UL, buttonId));
}
