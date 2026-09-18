using System.Text.Json;
using Modbot.Moderation;

namespace Modbot.Moderation.Tests;

/// <summary>The AI tool switches (AutoMod design §6): where each starts, and what is stored.</summary>
public class AutoModAiToolsTests
{
    [Fact]
    public void TheReadingToolsStartOnAndTheProposalStartsOff()
    {
        var none = AutoModAiTools.Parse(null);

        Assert.True(AutoModAiTools.IsOn(none, AutoModAiTools.ClassifyTopics));
        Assert.True(AutoModAiTools.IsOn(none, AutoModAiTools.CheckPictures));
        Assert.True(AutoModAiTools.IsOn(none, AutoModAiTools.ReviewFlag));
        Assert.False(AutoModAiTools.IsOn(none, AutoModAiTools.ProposeAction));
    }

    [Fact]
    public void AStoredSwitchWinsOverTheDefault()
    {
        var switches = AutoModAiTools.Parse("""{"classify_topics": false, "propose_action": true}""");

        Assert.False(AutoModAiTools.IsOn(switches, AutoModAiTools.ClassifyTopics));
        Assert.True(AutoModAiTools.IsOn(switches, AutoModAiTools.ProposeAction));
        Assert.True(AutoModAiTools.IsOn(switches, AutoModAiTools.CheckPictures));
    }

    [Fact]
    public void OnlySwitchesThatDifferFromTheDefaultAreStored()
    {
        var all = new Dictionary<string, bool>
        {
            [AutoModAiTools.ClassifyTopics] = true,
            [AutoModAiTools.CheckPictures] = false,
            [AutoModAiTools.ReviewFlag] = true,
            [AutoModAiTools.ProposeAction] = true,
            ["not_a_tool"] = true,
        };

        var stored = JsonSerializer.Deserialize<Dictionary<string, bool>>(AutoModAiTools.Serialize(all))!;

        Assert.Equal(2, stored.Count);
        Assert.False(stored[AutoModAiTools.CheckPictures]);
        Assert.True(stored[AutoModAiTools.ProposeAction]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void UnreadableSwitchesCountAsNone(string json)
    {
        var switches = AutoModAiTools.Parse(json);

        Assert.Empty(switches);
        Assert.True(AutoModAiTools.IsOn(switches, AutoModAiTools.ClassifyTopics));
    }

    [Fact]
    public void AnUnknownToolIsOff_AndIsNotATool()
    {
        Assert.False(AutoModAiTools.IsOn(AutoModAiTools.Parse(null), "summarise_everything"));
        Assert.False(AutoModAiTools.IsTool("summarise_everything"));
        Assert.True(AutoModAiTools.IsTool(AutoModAiTools.ReviewFlag));
    }
}
