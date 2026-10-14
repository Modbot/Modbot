using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/rule</c>: a member asks for one of the community's rules by its number or its title, whatever
/// the case; the number is the rule's place, so a rule moved up answers to a new number; a rule that
/// is not there is told so; the suggestions are capped at 25; five uses a minute; refused while off.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RuleCommandTests
{
    private const string Guild = "424242";
    private const string Ada = "7001";

    private readonly PostgresFixture _db;

    public RuleCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestServices> ServicesAsync(bool on = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.SwitchCommand("rule", on);
            s.DiscordGuildId = Guild;
        }, Ct);
        return services;
    }

    private static DiscordCommandCall Call(string who, string which)
        => new(
            who,
            "kiri",
            DiscordCommands.Rule,
            new Dictionary<string, string> { [DiscordCommands.RuleWhichOption] = which },
            (_, _) => Task.CompletedTask);

    private static async Task<DiscordReply> RunAsync(TestServices services, DiscordCommandCall call)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(call, Ct);
    }

    private static async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(TestServices services, string typed)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().SuggestAsync(
            new DiscordSuggestionAsk(Ada, DiscordCommands.Rule, DiscordCommands.RuleWhichOption, typed, (_, _) => Task.CompletedTask), Ct);
    }

    private static async Task AddAsync(TestServices services, int position, string title, string text)
    {
        await using var db = services.Database.NewContext();
        db.ServerRules.Add(new ServerRule
        {
            Id = Guid.NewGuid(),
            Position = position,
            Title = title,
            Text = text,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task ThreeRulesAsync(TestServices services)
    {
        await AddAsync(services, 0, "Be kind", "Treat everyone with respect.");
        await AddAsync(services, 1, "No spam", "Do not flood the channels.");
        await AddAsync(services, 2, "Stay on topic", "Keep chats to their subject.");
    }

    [Fact]
    public void TheCommand_IsForEveryone_AndOffUntilTheOperatorTurnsItOn()
    {
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Rule));
        Assert.False(DiscordCommandSwitches.Find("rule")!.OnByDefault);
    }

    [Fact]
    public async Task ANumber_IsAnsweredWithItsNumberTitleAndText()
    {
        await using var services = await ServicesAsync();
        await ThreeRulesAsync(services);

        var reply = await RunAsync(services, Call(Ada, "2"));

        Assert.Equal("**Rule 2: No spam**\nDo not flood the channels.", reply.Text);
    }

    [Theory]
    [InlineData("stay on topic")]
    [InlineData("STAY ON TOPIC")]
    [InlineData("  Stay on topic ")]
    public async Task ATitle_AnyCase_FindsTheSameRule(string typed)
    {
        await using var services = await ServicesAsync();
        await ThreeRulesAsync(services);

        var reply = await RunAsync(services, Call(Ada, typed));

        Assert.Equal("**Rule 3: Stay on topic**\nKeep chats to their subject.", reply.Text);
    }

    [Fact]
    public async Task TheNumber_IsThePlace_SoAGapInThePositionsDoesNotShowAndAMovedRuleGetsANewNumber()
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, 10, "Be kind", "Respect.");
        await AddAsync(services, 4, "No spam", "No flooding.");

        var first = await RunAsync(services, Call(Ada, "1"));
        var second = await RunAsync(services, Call(Ada, "2"));

        Assert.Equal("**Rule 1: No spam**\nNo flooding.", first.Text);
        Assert.Equal("**Rule 2: Be kind**\nRespect.", second.Text);
    }

    [Theory]
    [InlineData("4", "There is no rule 4.")]
    [InlineData("0", "There is no rule 0.")]
    [InlineData("Be rude", "No rule called Be rude.")]
    public async Task ARuleThatIsNotThere_IsToldSo(string typed, string expected)
    {
        await using var services = await ServicesAsync();
        await ThreeRulesAsync(services);

        var reply = await RunAsync(services, Call(Ada, typed));

        Assert.Equal(expected, reply.Text);
    }

    [Fact]
    public async Task Suggestions_ListEachRuleAsItsNumberAndTitle_InOrder_ValueTheNumber()
    {
        await using var services = await ServicesAsync();
        await ThreeRulesAsync(services);

        var all = await SuggestAsync(services, string.Empty);
        var matching = await SuggestAsync(services, "spam");
        var byNumber = await SuggestAsync(services, "3");

        Assert.Equal([("1. Be kind", "1"), ("2. No spam", "2"), ("3. Stay on topic", "3")], all.Select(s => (s.Name, s.Value)));
        Assert.Equal([("2. No spam", "2")], matching.Select(s => (s.Name, s.Value)));
        Assert.Equal([("3. Stay on topic", "3")], byNumber.Select(s => (s.Name, s.Value)));
    }

    [Fact]
    public async Task Suggestions_AreAtMost25()
    {
        await using var services = await ServicesAsync();
        for (var i = 0; i < 40; i++)
            await AddAsync(services, i, $"Rule {i:00}", "Text.");

        var offered = await SuggestAsync(services, "rule");

        Assert.Equal(DiscordSuggestion.Most, offered.Count);
    }

    [Fact]
    public async Task Suggestions_AreNotOfferedWhileTheCommandIsOff()
    {
        await using var services = await ServicesAsync(on: false);
        await ThreeRulesAsync(services);

        Assert.Empty(await SuggestAsync(services, string.Empty));
    }

    [Fact]
    public async Task TooManyTriesInAMinute_AreRefused()
    {
        await using var services = await ServicesAsync();
        await ThreeRulesAsync(services);

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            await RunAsync(services, Call(Ada, "1"));

        var reply = await RunAsync(services, Call(Ada, "1"));

        Assert.Equal(RuleCommand.TooFastMessage, reply.Text);
    }

    [Fact]
    public async Task WhenSwitchedOff_TheCommandIsRefused()
    {
        await using var services = await ServicesAsync(on: false);
        await ThreeRulesAsync(services);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(Call(Ada, "1"), Ct);

        Assert.Equal("/rule is turned off on this server.", reply?.Text);
    }
}
