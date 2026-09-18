using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.Giveaways;

namespace Modbot.Discord.Tests.Giveaways;

/// <summary>
/// Giveaways design §7.1 and §7.3: the card carries the rules in plain words, names a winner the
/// way every other card names a person (Discord embeds design §2), and neither it nor the
/// announcement can ping anybody.
/// </summary>
public class GiveawayCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);

    private const string Address = "https://modbot.example.com";

    private static readonly CardStyle Style = new(Address, "The Kingdom", Address + "/icon-192.png");

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

    /// <summary>A winner as the drawer writes one: the key, the id it was built from, and a name.</summary>
    private static GiveawayEntrant Winner(int rank, string? name, bool purged = false) => new()
    {
        Position = rank - 1,
        Key = $"vrchat:usr_{rank}",
        VRChatUserId = purged ? null : $"usr_{rank}",
        Name = name,
        Weight = 5,
        WinnerRank = rank,
        Purged = purged,
    };

    /// <summary>Somebody who entered through Discord and has never linked a VRChat account.</summary>
    private static GiveawayEntrant DiscordWinner(int rank, string? name) => new()
    {
        Position = rank - 1,
        Key = $"discord:{rank}00",
        DiscordUserId = $"{rank}00",
        Name = name,
        Weight = 5,
        WinnerRank = rank,
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

    /// <summary>
    /// §3.1: a giveaway posted on Monday for entries that open on Friday says when it opens,
    /// rather than inviting a reaction that would count for nothing.
    /// </summary>
    [Fact]
    public void ACardPostedBeforeEntriesOpenSaysWhenTheyDo()
    {
        var giveaway = Giveaway(g => g.OpensAt = Now.AddDays(2));

        var card = GiveawayCard.For(giveaway, GiveawayCardState.Open, 0, [], now: Now);

        Assert.StartsWith("Opens ", Field(card, "How to enter"), StringComparison.Ordinal);
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

        // The "<" that would start the mention is escaped, so Discord draws the whole thing as
        // text. Backslash escaping is how that is done, so the characters are all still there.
        Assert.Contains(@"\<@123>", winners, StringComparison.Ordinal);
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

    // ── The winners are linked names ─────────────────────────────────────────────────────

    /// <summary>
    /// Discord embeds design §2: a person on a card is a linked name. A card announcing that
    /// somebody won is the last place a moderator should have nothing to click.
    /// </summary>
    [Fact]
    public void AWinnersNameIsALinkWhenThereIsAnAddressToLinkTo()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Drawn, 12, [Winner(1, "Ada")], style: Style);

        Assert.Equal($"[Ada]({Address}/audit?subject=usr_1)", Field(card, "Winner"));
    }

    /// <summary>§2: no public address means no link, because the address would not work.</summary>
    [Fact]
    public void AWinnersNameIsPlainWhenThereIsNoAddress()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Drawn, 12, [Winner(1, "Ada")], style: new CardStyle(null, "The Kingdom"));

        Assert.Equal("Ada", Field(card, "Winner"));
        Assert.DoesNotContain("](", Field(card, "Winner"), StringComparison.Ordinal);
    }

    /// <summary>Somebody who only ever entered through Discord opens their Discord profile.</summary>
    [Fact]
    public void AWinnerWhoOnlyHasADiscordAccountLinksToThatProfile()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Drawn, 12, [DiscordWinner(1, "Bea")], style: Style);

        Assert.Equal($"[Bea]({Address}/discord/members?subject=discord-person%3A100)", Field(card, "Winner"));
    }

    /// <summary>
    /// §6.3: an erased winner has no name and no ids left, so there is nothing to link to and the
    /// words that replaced them are all the card can show.
    /// </summary>
    [Fact]
    public void AnErasedWinnerNeverBecomesALink()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Drawn, 1, [Winner(1, null, purged: true)], style: Style);

        Assert.Contains("erased", Field(card, "Winner"), StringComparison.Ordinal);
        Assert.DoesNotContain("](", Field(card, "Winner"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A linked name is several times the length of a plain one, so the list is built against
    /// Discord's field limit rather than cut to it: a cut inside a link would put a raw address on
    /// the card.
    /// </summary>
    [Fact]
    public void ALinkedWinnerListStaysInsideDiscordsFieldLimit()
    {
        var many = Enumerable.Range(1, 25).Select(i => Winner(i, $"Person {i}")).ToList();

        var winners = Field(
            GiveawayCard.For(Giveaway(), GiveawayCardState.Drawn, 25, many, style: Style), "Winners");

        Assert.True(winners.Length <= GiveawayCard.FieldValueLimit);
        Assert.Contains(" more", winners, StringComparison.Ordinal);
        Assert.DoesNotContain("…", winners, StringComparison.Ordinal);
    }

    // ── The group, the colours and the mark ──────────────────────────────────────────────

    /// <summary>
    /// The group's name sits above the giveaway's, the way it does on an instance and a calendar
    /// post, and the footer carries Modbot's mark.
    /// </summary>
    [Fact]
    public void TheGroupAndTheMarkComeFromTheStyle()
    {
        var card = GiveawayCard.For(
            Giveaway(), GiveawayCardState.Open, 12, [], style: Style,
            picture: new CardPicture(AuthorIcon: "attachment://p0123456789abcdef.png"));

        Assert.Equal("The Kingdom", card.AuthorName);
        Assert.Equal(Address + "/icon-192.png", card.FooterIconUrl);
        Assert.Equal("attachment://p0123456789abcdef.png", card.AuthorIconUrl);
    }

    [Fact]
    public void ADeploymentWithNoGroupAndNoAddressStillMakesACard()
    {
        var card = GiveawayCard.For(Giveaway(), GiveawayCardState.Open, 12, []);

        Assert.Null(card.AuthorName);
        Assert.Null(card.FooterIconUrl);
        Assert.Null(card.AuthorIconUrl);
    }

    /// <summary>The colours are the shared palette's, one per meaning.</summary>
    [Fact]
    public void TheColoursComeFromTheSharedPalette()
    {
        var giveaway = Giveaway();

        Assert.Equal(CardColour.Violet, GiveawayCard.For(giveaway, GiveawayCardState.Open, 0, []).Color);
        Assert.Equal(CardColour.Dark, GiveawayCard.For(giveaway, GiveawayCardState.Closed, 0, []).Color);
        Assert.Equal(CardColour.Green, GiveawayCard.For(giveaway, GiveawayCardState.Drawn, 0, []).Color);
        Assert.Equal(CardColour.Red, GiveawayCard.For(giveaway, GiveawayCardState.Cancelled, 0, []).Color);
    }

    /// <summary>
    /// A title is a slot Discord prints literally, so it is stripped rather than escaped: a
    /// giveaway called <c>*hats*</c> must not read as <c>\*hats\*</c> on its own card.
    /// </summary>
    [Fact]
    public void TheTitleIsPrintedAsWrittenRatherThanEscaped()
    {
        var card = GiveawayCard.For(
            Giveaway(g => g.Name = "*hats*"), GiveawayCardState.Open, 0, []);

        Assert.Equal("*hats*", card.Title);
    }

    /// <summary>A Discord role is named by whoever made it, so its name cannot carry formatting.</summary>
    [Fact]
    public void ARoleNameInARuleCannotCarryFormatting()
    {
        var giveaway = Giveaway(g => g.Rules = GiveawayRules.Store(new GiveawayRule
        {
            Kind = GiveawayRuleKinds.AllOf,
            Rules = [new GiveawayRule { Kind = GiveawayRuleKinds.DiscordRole, Id = "77" }],
        }));

        var card = GiveawayCard.For(
            giveaway, GiveawayCardState.Open, 0, [],
            roleNames: new Dictionary<string, string>(StringComparer.Ordinal) { ["77"] = "**VIP**" });

        Assert.DoesNotContain("**VIP**", Field(card, "Rules"), StringComparison.Ordinal);
        Assert.Contains("VIP", Field(card, "Rules"), StringComparison.Ordinal);
    }

    // ── The announcement ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The names here stay plain even where the card above them links every winner: this is a line
    /// of message text rather than an embed.
    /// </summary>
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

    /// <summary>
    /// §7.3: nobody is pinged. Mention markup in a name is escaped, so Discord draws it as text
    /// rather than as somebody's name. A bare <c>@everyone</c> is left as the winner wrote it,
    /// because a backslash in front of it would be a backslash on screen and would stop nothing --
    /// what stops the ping is the send, where every message the bot posts goes out with mentions
    /// off.
    /// </summary>
    [Fact]
    public void TheAnnouncementCannotPingAnybody()
    {
        var draw = new GiveawayDraw { Number = 1 };

        Assert.Equal(
            @"**Autumn raffle**: the winner is \<@123>.",
            GiveawayCard.Announcement(Giveaway(), draw, [Winner(1, "<@123>")]));

        Assert.Equal(
            "**Autumn raffle**: the winner is @everyone.",
            GiveawayCard.Announcement(Giveaway(), draw, [Winner(1, "@everyone")]));
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
