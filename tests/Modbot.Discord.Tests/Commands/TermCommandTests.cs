using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/term</c>: a member asks what a word the community uses means; the answer comes from the terms
/// staff typed on the Terms and rules page, by name or by another name, whatever the case; a word no
/// term has is told so; the suggestions are capped at 25; five uses a minute; refused while off.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class TermCommandTests
{
    private const string Guild = "424242";
    private const string Ada = "7001";

    private readonly PostgresFixture _db;

    public TermCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestServices> ServicesAsync(bool on = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.SwitchCommand("term", on);
            s.DiscordGuildId = Guild;
        }, Ct);
        return services;
    }

    private static DiscordCommandCall Call(string who, string word)
        => new(
            who,
            "kiri",
            DiscordCommands.Term,
            new Dictionary<string, string> { [DiscordCommands.TermNameOption] = word },
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
            new DiscordSuggestionAsk(Ada, DiscordCommands.Term, DiscordCommands.TermNameOption, typed, (_, _) => Task.CompletedTask), Ct);
    }

    private static async Task AddAsync(TestServices services, string name, string meaning, params string[] otherNames)
    {
        await using var db = services.Database.NewContext();
        db.GlossaryTerms.Add(new GlossaryTerm
        {
            Id = Guid.NewGuid(),
            Name = name,
            OtherNames = otherNames,
            Meaning = meaning,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public void TheCommand_IsForEveryone_AndOffUntilTheOperatorTurnsItOn()
    {
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Term));
        Assert.False(DiscordCommandSwitches.Find("term")!.OnByDefault);
    }

    [Fact]
    public async Task ATypedName_IsAnsweredWithTheNameTheOtherNamesAndTheMeaning()
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, "Regent", "A trusted helper with a gold name.", "Reggie", "Reg");

        var reply = await RunAsync(services, Call(Ada, "Regent"));

        Assert.Equal("**Regent** (also: Reggie, Reg)\nA trusted helper with a gold name.", reply.Text);
    }

    [Fact]
    public async Task ATermWithNoOtherNames_IsAnsweredWithoutThePhraseAboutThem()
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, "Regent", "A trusted helper.");

        var reply = await RunAsync(services, Call(Ada, "Regent"));

        Assert.Equal("**Regent**\nA trusted helper.", reply.Text);
    }

    [Theory]
    [InlineData("reggie")]
    [InlineData("REGGIE")]
    [InlineData("  Reggie  ")]
    [InlineData("regent")]
    public async Task AnotherName_AndAnyCase_FindTheSameTerm(string typed)
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, "Regent", "A trusted helper.", "Reggie");

        var reply = await RunAsync(services, Call(Ada, typed));

        Assert.Equal("**Regent** (also: Reggie)\nA trusted helper.", reply.Text);
    }

    [Fact]
    public async Task AWordNoTermHas_IsToldSo()
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, "Regent", "A trusted helper.");

        var reply = await RunAsync(services, Call(Ada, "Duke"));

        Assert.Equal("No term called Duke.", reply.Text);
    }

    [Fact]
    public async Task APartOfAName_IsNotAnAnswer_OnlyASuggestion()
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, "Regent", "A trusted helper.");

        var reply = await RunAsync(services, Call(Ada, "Reg"));

        Assert.Equal("No term called Reg.", reply.Text);
    }

    [Fact]
    public async Task Suggestions_OfferNamesAndOtherNames_InOrder_AndLeaveOutWhatDoesNotMatch()
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, "Regent", "A trusted helper.", "Reggie");
        await AddAsync(services, "Prefect", "A helper.", "Region lead");
        await AddAsync(services, "Yarn", "Not matching.");

        var offered = await SuggestAsync(services, "reg");

        Assert.Equal(
            [("Regent", "Regent"), ("Reggie (see Regent)", "Reggie"), ("Region lead (see Prefect)", "Region lead")],
            offered.Select(s => (s.Name, s.Value)));
        Assert.DoesNotContain(offered, s => s.Value == "Yarn");
    }

    [Fact]
    public async Task Suggestions_AreAtMost25()
    {
        await using var services = await ServicesAsync();
        for (var i = 0; i < 40; i++)
            await AddAsync(services, $"Word {i:00}", "Meaning.");

        var offered = await SuggestAsync(services, "word");

        Assert.Equal(DiscordSuggestion.Most, offered.Count);
    }

    [Fact]
    public async Task Suggestions_AreNotOfferedWhileTheCommandIsOff()
    {
        await using var services = await ServicesAsync(on: false);
        await AddAsync(services, "Regent", "A trusted helper.");

        Assert.Empty(await SuggestAsync(services, "reg"));
    }

    [Fact]
    public async Task TooManyTriesInAMinute_AreRefused()
    {
        await using var services = await ServicesAsync();
        await AddAsync(services, "Regent", "A trusted helper.");

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            await RunAsync(services, Call(Ada, "Regent"));

        var reply = await RunAsync(services, Call(Ada, "Regent"));

        Assert.Equal(TermCommand.TooFastMessage, reply.Text);
    }

    [Fact]
    public async Task WhenSwitchedOff_TheCommandIsRefused()
    {
        await using var services = await ServicesAsync(on: false);
        await AddAsync(services, "Regent", "A trusted helper.");

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().RunAsync(Call(Ada, "Regent"), Ct);

        Assert.Equal("/term is turned off on this server.", reply?.Text);
    }
}
