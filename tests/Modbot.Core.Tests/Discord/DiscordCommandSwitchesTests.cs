using Modbot.Core.Discord;

namespace Modbot.Core.Tests.Discord;

/// <summary>
/// The <c>discord_commands</c> setting (Discord commands design §3.8): a name that is not stored takes
/// its default, so a command added later needs no migration.
/// </summary>
public class DiscordCommandSwitchesTests
{
    [Fact]
    public void NothingStored_EveryCommandTakesItsDefault_MeAndEventsBeingTheOnesThatAreOff()
    {
        foreach (var json in new string?[] { null, "", "   ", "{}" })
        {
            Assert.True(DiscordCommandSwitches.IsOn(json, "lookup"));
            Assert.True(DiscordCommandSwitches.IsOn(json, "recent"));
            Assert.True(DiscordCommandSwitches.IsOn(json, "Look up in Modbot"));
            Assert.False(DiscordCommandSwitches.IsOn(json, "me"));
        }

        Assert.Equal(["me", "events"], DiscordCommandSwitches.All.Where(c => !c.OnByDefault).Select(c => c.Name));
    }

    [Fact]
    public void ACommandThatIsNotOurs_IsNeverOn()
        => Assert.False(DiscordCommandSwitches.IsOn("{\"nonsense\":true}", "nonsense"));

    [Fact]
    public void AStoredChoice_BeatsTheDefault_ForThatNameOnly()
    {
        const string json = "{\"me\":true,\"recent\":false}";

        Assert.True(DiscordCommandSwitches.IsOn(json, "me"));
        Assert.False(DiscordCommandSwitches.IsOn(json, "recent"));
        Assert.True(DiscordCommandSwitches.IsOn(json, "lookup"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[true,false]")]
    [InlineData("\"me\"")]
    [InlineData("null")]
    [InlineData("{\"me\":\"yes\",\"recent\":1,\"lookup\":null}")]
    public void ASettingThatIsNotAnObjectOfTrueAndFalse_ReadsAsNothingStored(string json)
    {
        Assert.False(DiscordCommandSwitches.IsOn(json, "me"));
        Assert.True(DiscordCommandSwitches.IsOn(json, "recent"));
        Assert.True(DiscordCommandSwitches.IsOn(json, "lookup"));
    }

    [Fact]
    public void Writing_KeepsOnlyWhatDiffersFromTheDefault_AndDropsNamesThatAreNotCommands()
    {
        var json = DiscordCommandSwitches.Write(new Dictionary<string, bool>
        {
            ["me"] = true,
            ["lookup"] = true,
            ["recent"] = false,
            ["nonsense"] = true,
        });

        Assert.Equal("{\"me\":true,\"recent\":false}", json);
        Assert.Equal("{}", DiscordCommandSwitches.Write(DiscordCommandSwitches.Current(null)));
    }

    [Fact]
    public void WithOneCommandSet_TheOthersStayAsTheyWere()
    {
        var json = DiscordCommandSwitches.With("{\"recent\":false}", "me", true);

        Assert.True(DiscordCommandSwitches.IsOn(json, "me"));
        Assert.False(DiscordCommandSwitches.IsOn(json, "recent"));

        // Putting it back to its default leaves nothing stored for it.
        Assert.Equal("{\"recent\":false}", DiscordCommandSwitches.With(json, "me", false));
    }

    [Fact]
    public void EveryNameIsUnique_AndMenusAreMarked()
    {
        Assert.Equal(DiscordCommandSwitches.All.Count, DiscordCommandSwitches.All.Select(c => c.Name).Distinct().Count());
        Assert.Equal(["Look up in Modbot", "Add a note"], DiscordCommandSwitches.All.Where(c => c.Menu).Select(c => c.Name));
    }
}
