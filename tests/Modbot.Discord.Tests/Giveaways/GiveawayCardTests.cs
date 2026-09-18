using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Discord.Gateway;
using Modbot.Discord.Giveaways;

namespace Modbot.Discord.Tests.Giveaways;

/// <summary>
/// Giveaways design §7.1 and §7.3: the card carries the rules in plain words, and neither it nor
/// the announcement can ping anybody.
/// </summary>
public class GiveawayCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);

    private static Giveaway Giveaway(Action<Giveaway>? shape = null)
    {
        var giveaway = new Giveaway
        {
            Id = Guid.Parse("01923456-7890-7abc-8def-0123456789ab"),
            Name = "Autumn raffle",
            Prize = "A very silly hat",
            OpensAt = Now,
            ClosesAt = Now.AddDays(7),
            WinnerCount = 2,
            EntryWay = GiveawayEntryWays.React,
            Emoji = "🎉",
            Rules = GiveawayRules.Store(new GiveawayRule
            {
                Kind = GiveawayRuleKinds.AllOf,
                Rules =
                [
                    new GiveawayRule { Kind = GiveawayRuleKinds.DiscordMemberDays, Amount = 30 },
                    new GiveawayRule { Kind = GiveawayRuleKinds.InstanceHours, Amount = 10, WithinDays = 90 },
                ],
            }),
            Exclusions = new GiveawayExclusions { Staff = true }.Store(),
            Weighting = GiveawayWeights.InstanceHours,
            WeightCap = 20,
            State = GiveawayStates.Open,
        };

        shape?.Invoke(giveaway);
        return giveaway;
    }

    private static GiveawayEntrant Winner(int rank, string? name, bool purged = false) => new()
    {
        Position = rank - 1,
        Key = $"vrchat:usr_{rank}",
        Name = name,
        Weight = 5,
        WinnerRank = rank,
        Purged = purged,
    };

    private static string Field(DiscordEmbedContent card, string name)
        => card.Fields.Single(f => f.Name == name).Value;

    [Fact]
    public void TheCardSaysWhatItIsAndHowToEnter()
    {
        var card = GiveawayCard.For(Giveaway(), GiveawayCardState.Open, entryCount: 12, winners: []);

        Assert.Equal("Autumn raffle", card.Title);
        Assert.Equal("A very silly hat", Field(card, "Prize"));
        Assert.Equal("React with 🎉", Field(card, "How to enter"));
        Assert.Equal("2", Field(card, "Winners"));
        Assert.Equal("12", Field(card, "Entries"));
        Assert.Equal("Open", card.Footer);
    }

    /// <summary>
    /// §7.1: the rules are on the card, because a giveaway whose rules live on a page members
    /// cannot open is a giveaway they have to take on trust.
    /// </summary>
    [Fact]
    public void TheRulesAreOnTheCardInPlainWords()
    {
        var card = GiveawayCard.For(Giveaway(), GiveawayCardState.Open, 0, []);

        Assert.Equal(
            "• in Discord for 30 days or more\n• 10 hours or more in our instances in the last 90 days",
            Field(card, "Rules"));
    }

    [Fact]
    public void TheExclusionsAreOnTheCardToo()
    {
        var card = GiveawayCard.For(Giveaway(), GiveawayCardState.Open, 0, []);

        Assert.Equal("staff", Field(card, "Not eligible"));
    }

    [Fact]
    public void TheWeightingAndItsCapAreOnTheCard()
    {
        var card = GiveawayCard.For(Giveaway(), GiveawayCardState.Open, 0, []);

        Assert.Equal("Hours in our instances, capped at 20", Field(card, "Weighted by"));
    }

    [Fact]
    public void AGiveawayWhereEverybodyWeighsTheSameSaysNothingAboutWeighting()
    {
        var card = GiveawayCard.For(
            Giveaway(g => g.Weighting = GiveawayWeights.Uniform), GiveawayCardState.Open, 0, []);

        Assert.DoesNotContain(card.Fields, f => f.Name == "Weighted by");
    }

    [Fact]
    public void AnAutomaticGiveawayAsksNobodyToDoAnything()
    {
        var card = GiveawayCard.For(
            Giveaway(g => g.EntryWay = GiveawayEntryWays.Automatic), GiveawayCardState.Open, 0, []);

        Assert.Equal("Nothing — everyone who matches is in", Field(card, "How to enter"));
        Assert.DoesNotContain(card.Fields, f => f.Name == "Entries");
    }

    [Fact]
    public void ADrawnCardNamesTheWinners()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Drawn, 12, [Winner(1, "Ada"), Winner(2, "Grace")]);

        Assert.Equal("Ada, Grace", Field(card, "Winners"));
        Assert.Equal("Drawn", card.Footer);
    }

    [Fact]
    public void ACancelledCardSaysSo()
    {
        var card = GiveawayCard.For(Giveaway(), GiveawayCardState.Cancelled, 12, []);

        Assert.Equal("Cancelled", card.Footer);
        Assert.Equal("Cancelled", Field(card, "How to enter"));
    }

    /// <summary>
    /// A winner's name is text they chose, so it is escaped the way a display name on an instance
    /// card is. Mentions are off on every message the bot sends as well.
    /// </summary>
    [Fact]
    public void AWinnersNameCannotCarryFormattingOrAMention()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Drawn, 1, [Winner(1, "**@everyone** <@123>")]);

        var winners = Field(card, "Winner");

        Assert.DoesNotContain("**", winners, StringComparison.Ordinal);
        Assert.DoesNotContain("<@123>", winners, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErasedWinnerIsNamedAsErased()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Drawn, 1, [Winner(1, null, purged: true)]);

        Assert.Contains("erased", Field(card, "Winner"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongWinnerListSaysHowManyMoreThereAre()
    {
        var many = Enumerable.Range(1, 25).Select(i => Winner(i, $"Person {i}")).ToList();

        var card = GiveawayCard.For(Giveaway(), GiveawayCardState.Drawn, 25, many);

        Assert.Contains("and 5 more", Field(card, "Winners"), StringComparison.Ordinal);
    }

    // ── The announcement ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAnnouncementNamesTheWinner()
    {
        var draw = new GiveawayDraw { Number = 1 };

        Assert.Equal(
            "**Autumn raffle**: the winner is Ada.",
            GiveawayCard.Announcement(Giveaway(), draw, [Winner(1, "Ada")]));
    }

    [Fact]
    public void TheAnnouncementNamesSeveralWinners()
    {
        var draw = new GiveawayDraw { Number = 1 };

        Assert.Equal(
            "**Autumn raffle**: the winners are Ada, Grace.",
            GiveawayCard.Announcement(Giveaway(), draw, [Winner(1, "Ada"), Winner(2, "Grace")]));
    }

    /// <summary>§5.3: a re-draw is visibly a different draw, in the channel as well as on the page.</summary>
    [Fact]
    public void ASecondDrawSaysWhichDrawItIs()
    {
        var draw = new GiveawayDraw { Number = 2 };

        Assert.Contains(
            "(draw 2)",
            GiveawayCard.Announcement(Giveaway(), draw, [Winner(1, "Ada")]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADrawWithNobodyInItSaysThatRatherThanNamingNobody()
    {
        var draw = new GiveawayDraw { Number = 1 };

        Assert.Equal(
            "**Autumn raffle** was drawn and nobody was in it.",
            GiveawayCard.Announcement(Giveaway(), draw, []));
    }

    [Fact]
    public void TheAnnouncementCannotPingAnybody()
    {
        var draw = new GiveawayDraw { Number = 1 };

        var line = GiveawayCard.Announcement(Giveaway(), draw, [Winner(1, "@everyone")]);

        Assert.DoesNotContain("@everyone", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetailsButtonAppearsOnlyWhenThereIsAnAddressToPointAt()
    {
        Assert.Empty(GiveawayCard.Links(null));
        Assert.Single(GiveawayCard.Links("https://modbot.example.com/giveaways?giveaway=1"));
    }

    [Fact]
    public void TheLinkIsNullUntilThePublicAddressIsSet()
    {
        Assert.Null(GiveawayDiscordPublisher.Link(null, Giveaway()));

        Assert.Equal(
            "https://modbot.example.com/giveaways?giveaway=01923456-7890-7abc-8def-0123456789ab",
            GiveawayDiscordPublisher.Link("https://modbot.example.com/", Giveaway()));
    }
}
