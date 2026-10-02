using System.Text.Json.Nodes;
using Modbot.Analytics.Lists;
using Modbot.Core.Giveaways;

namespace Modbot.Analytics.Tests.Lists;

/// <summary>
/// Roles from lists design §5: which rule trees let everybody in, read the way the checker reads
/// them, so a list like that never gives a role.
/// </summary>
public class ListRoleRulesTests
{
    private static GiveawayRule Read(string json)
    {
        var rule = GiveawayRules.Read(JsonNode.Parse(json), out var error);
        Assert.Null(error);
        return rule!;
    }

    [Theory]
    [InlineData("""{"kind":"allOf","rules":[]}""", true)]
    [InlineData("""{"kind":"noneOf","rules":[]}""", true)]
    [InlineData("""{"kind":"anyOf","rules":[{"kind":"allOf","rules":[]},{"kind":"inGroup"}]}""", true)]
    [InlineData("""{"kind":"allOf","rules":[{"kind":"allOf","rules":[]}]}""", true)]
    [InlineData("""{"kind":"noneOf","rules":[{"kind":"anyOf","rules":[]}]}""", true)]
    [InlineData("""{"kind":"allOf","rules":[{"kind":"inGroup"}]}""", false)]
    [InlineData("""{"kind":"anyOf","rules":[]}""", false)]
    [InlineData("""{"kind":"noneOf","rules":[{"kind":"inGroup"}]}""", false)]
    [InlineData("""{"kind":"allOf","rules":[{"kind":"inGroup"},{"kind":"allOf","rules":[]}]}""", false)]
    public void ARuleTreeLetsEverybodyInWhereTheCheckerWould(string json, bool everybody)
        => Assert.Equal(everybody, ListRolePlanner.LetsEverybodyIn(Read(json)));

    /// <summary>A list that only names a list nobody can find lets nobody in, not everybody.</summary>
    [Fact]
    public void AListThatIsGoneLetsNobodyIn()
    {
        var rule = Read("""{"kind":"noneOf","rules":[{"kind":"inList","id":"0192a8f0-0000-7000-8000-000000000009"}]}""");
        Assert.True(ListRolePlanner.LetsEverybodyIn(rule));
        Assert.False(ListRolePlanner.LetsEverybodyIn(Read("""{"kind":"allOf","rules":[{"kind":"inList","id":"0192a8f0-0000-7000-8000-000000000009"}]}""")));
    }

    [Theory]
    [InlineData("""{"kind":"allOf","rules":[{"kind":"inGroup"},{"kind":"allOf","rules":[]}]}""", true)]
    [InlineData("""{"kind":"anyOf","rules":[{"kind":"noneOf","rules":[]}]}""", true)]
    [InlineData("""{"kind":"allOf","rules":[{"kind":"inGroup"},{"kind":"anyOf","rules":[{"kind":"linkedAccounts"}]}]}""", false)]
    public void AnEmptyGroupAnywhereIsFound(string json, bool empty)
        => Assert.Equal(empty, ListRolePlanner.HasEmptyGroup(Read(json)));
}
