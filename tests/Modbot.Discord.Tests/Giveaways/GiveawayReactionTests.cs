using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Discord.Gateway;
using Modbot.Discord.Giveaways;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Giveaways;

/// <summary>
/// Giveaways design §4.2 and §4.4: a reaction on the giveaway's post with the giveaway's emoji is
/// an entry, taking it off withdraws it, and everything else in the server is somebody using
/// Discord.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GiveawayReactionTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string Message = "333333333333333333";
    private const string Person = "444444444444444444";
    private const string Emoji = "🎉";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordReactionSnapshot Reaction(
        string emoji = Emoji, string userId = Person, string messageId = Message, bool isBot = false)
        => new(Guild, Channel, messageId, userId, emoji, "Someone", isBot);

    private static async Task<Giveaway> AddGiveawayAsync(
        TestServices services,
        GiveawayRule? rules = null,
        string state = GiveawayStates.Open,
        string entryWay = GiveawayEntryWays.React,
        string emoji = Emoji,
        bool posted = true)
    {
        var now = services.Clock.UtcNow;

        var giveaway = new Giveaway
        {
            Id = Guid.CreateVersion7(),
            Name = "A prize",
            OpensAt = now.AddDays(-1),
            ClosesAt = now.AddDays(1),
            WinnerCount = 1,
            EntryWay = entryWay,
            Emoji = emoji,
            Rules = GiveawayRules.Store(rules ?? GiveawayRule.Everyone),
            State = state,
            PostToChannel = true,
            ChannelId = Channel,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var context = services.Database.NewContext();
        context.Giveaways.Add(giveaway);

        if (posted)
        {
            context.GiveawayPosts.Add(new GiveawayPost
            {
                GiveawayId = giveaway.Id,
                State = GiveawayPostStates.Published,
                MessageId = Message,
                ChannelId = Channel,
                UpdatedAt = now,
            });
        }

        await context.SaveChangesAsync(Ct);
        return giveaway;
    }

    private static async Task<bool> AddedAsync(TestServices services, DiscordReactionSnapshot reaction)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<GiveawayReactions>().AddedAsync(reaction, Ct);
    }

    private static async Task<bool> RemovedAsync(TestServices services, DiscordReactionSnapshot reaction)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<GiveawayReactions>().RemovedAsync(reaction, Ct);
    }

    private static async Task<GiveawayEntry?> EntryAsync(TestServices services, Guid giveawayId)
    {
        await using var context = services.Database.NewContext();
        return await context.GiveawayEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.GiveawayId == giveawayId && e.DiscordUserId == Person, Ct);
    }

    [Fact]
    public async Task ReactingEntersTheGiveaway()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        Assert.True(await AddedAsync(services, Reaction()));

        var entry = await EntryAsync(services, giveaway.Id);
        Assert.NotNull(entry);
        Assert.Null(entry.WithdrawnAt);
        Assert.True(entry.QualifiedOnEntry);

        var facts = await services.FactsOfTypeAsync(FactType.GiveawayEntered, Ct);
        var fact = Assert.Single(facts);
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Equal(Person, fact.SubjectId);
        Assert.Contains(giveaway.Id.ToString(), fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakingTheReactionOffWithdrawsTheEntry()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        await AddedAsync(services, Reaction());
        Assert.True(await RemovedAsync(services, Reaction()));

        var entry = await EntryAsync(services, giveaway.Id);
        Assert.NotNull(entry);
        Assert.NotNull(entry.WithdrawnAt);

        Assert.Single(await services.FactsOfTypeAsync(FactType.GiveawayWithdrawn, Ct));
    }

    /// <summary>
    /// Putting the reaction back is the same person returning, not a second entry — the row is
    /// kept and cleared rather than a new one added.
    /// </summary>
    [Fact]
    public async Task ReactingAgainAfterWithdrawingEntersThemOnceMore()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        await AddedAsync(services, Reaction());
        await RemovedAsync(services, Reaction());
        await AddedAsync(services, Reaction());

        var entry = await EntryAsync(services, giveaway.Id);
        Assert.Null(entry!.WithdrawnAt);

        await using var context = services.Database.NewContext();
        Assert.Equal(1, await context.GiveawayEntries.CountAsync(e => e.GiveawayId == giveaway.Id, Ct));
    }

    [Fact]
    public async Task WithdrawingWhenThereIsNoEntryDoesNothing()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await AddGiveawayAsync(services);

        Assert.False(await RemovedAsync(services, Reaction()));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.GiveawayWithdrawn, Ct));
    }

    /// <summary>
    /// §4.2: the answer on entry is recorded, and somebody who does not qualify still enters —
    /// with the reason beside them.
    /// </summary>
    [Fact]
    public async Task SomebodyWhoDoesNotQualifyStillEntersAndIsMarked()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);

        var giveaway = await AddGiveawayAsync(
            services,
            rules: new GiveawayRule { Kind = GiveawayRuleKinds.LinkedAccounts });

        Assert.True(await AddedAsync(services, Reaction()));

        var entry = await EntryAsync(services, giveaway.Id);
        Assert.NotNull(entry);
        Assert.False(entry.QualifiedOnEntry);
        Assert.Equal(GiveawayKeptOut.Rules, entry.KeptOut);
    }

    // ── What does not count ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnotherEmojiOnTheSamePostIsNotAnEntry()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        Assert.False(await AddedAsync(services, Reaction(emoji: "👍")));
        Assert.Null(await EntryAsync(services, giveaway.Id));
    }

    [Fact]
    public async Task AReactionOnAnyOtherMessageIsNotAnEntry()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        Assert.False(await AddedAsync(services, Reaction(messageId: "999999999999999999")));
        Assert.Null(await EntryAsync(services, giveaway.Id));
    }

    [Fact]
    public async Task AReactionBeforeEntriesOpenIsNotAnEntry()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        await using (var context = services.Database.NewContext())
        {
            var row = await context.Giveaways.SingleAsync(g => g.Id == giveaway.Id, Ct);
            row.OpensAt = services.Clock.UtcNow.AddDays(2);
            await context.SaveChangesAsync(Ct);
        }

        Assert.False(await AddedAsync(services, Reaction()));
        Assert.Null(await EntryAsync(services, giveaway.Id));
    }

    [Fact]
    public async Task AReactionAfterItClosesIsNotAnEntry()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services, state: GiveawayStates.Closed);

        Assert.False(await AddedAsync(services, Reaction()));
        Assert.Null(await EntryAsync(services, giveaway.Id));
    }

    /// <summary>The bot's own reaction is there to give people something to click, not to enter.</summary>
    [Fact]
    public async Task TheBotsOwnReactionIsNotAnEntry()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        Assert.False(await AddedAsync(services, Reaction(isBot: true)));
        Assert.Null(await EntryAsync(services, giveaway.Id));
    }

    [Fact]
    public async Task AReactionOnAnAutomaticGiveawaysPostIsNotAnEntry()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services, entryWay: GiveawayEntryWays.Automatic);

        Assert.False(await AddedAsync(services, Reaction()));
        Assert.Null(await EntryAsync(services, giveaway.Id));
    }

    /// <summary>
    /// §4.4: Modbot does not have to know who somebody is. An unknown account enters; whether they
    /// qualify is up to the rules.
    /// </summary>
    [Fact]
    public async Task SomebodyModbotHasNoMemberRowForCanStillEnter()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var giveaway = await AddGiveawayAsync(services);

        Assert.True(await AddedAsync(services, Reaction(userId: "555555555555555555")));

        await using var context = services.Database.NewContext();
        Assert.True(await context.GiveawayEntries
            .AnyAsync(e => e.GiveawayId == giveaway.Id && e.DiscordUserId == "555555555555555555", Ct));
    }
}
