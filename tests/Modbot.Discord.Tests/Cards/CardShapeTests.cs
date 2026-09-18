using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Alerts;
using Modbot.Discord.Calendar;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Insights;
using Modbot.Discord.Instances;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Tests.Cards;

/// <summary>
/// One whole card of each kind, read end to end: what heads it, what it links to, where its
/// pictures go, and -- on every one of them -- that no VRChat id is printed where a name will do.
/// </summary>
/// <remarks>
/// Every builder here is pure, so none of this needs a database or a gateway. The pictures arrive
/// as the addresses a poster would have resolved, which is what keeps the builders pure.
/// </remarks>
public class CardShapeTests
{
    private const string Address = "https://modbot.example.com";
    private const string Person = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Actor = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";
    private const string World = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";

    private static readonly DateTimeOffset At = new(2026, 9, 13, 2, 25, 36, TimeSpan.Zero);

    private static readonly CardStyle Style = new(Address, "The Kingdom", Address + "/icon-192.png");

    /// <summary>Every field value and the description, which is everywhere an id could hide.</summary>
    private static IEnumerable<string> Body(DiscordEmbedContent card)
    {
        if (card.Description is { } description)
            yield return description;

        foreach (var field in card.Fields)
            yield return field.Value;
    }

    private static void AssertNoIdsInTheBody(DiscordEmbedContent card)
    {
        foreach (var text in Body(card))
        {
            Assert.DoesNotContain(Person, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Actor, text, StringComparison.Ordinal);
            Assert.DoesNotContain(World, text, StringComparison.Ordinal);
        }
    }

    // ── A moderation event ───────────────────────────────────────────────────────────────

    private static ModerationEventView Ban(string? subjectName = "jessie") => new(
        52, FactType.MemberBanned, At,
        Person, subjectName,
        Actor, "E-Ray",
        "User jessie was preemptively banned by E-Ray.");

    [Fact]
    public void AModerationCard_IsHeadedByThePersonAndTitledByWhatHappened()
    {
        var card = ModerationEventEmbed.For(Ban(), Style, new CardPicture(AuthorIcon: "attachment://pa.png"));

        Assert.Equal("jessie", card.AuthorName);
        Assert.Equal($"{Address}/audit?subject={Person}", card.AuthorUrl);
        Assert.Equal("attachment://pa.png", card.AuthorIconUrl);

        Assert.Equal("Banned", card.Title);
        Assert.Equal($"{Address}/audit?subject={Person}", card.Url);
        Assert.Equal(CardColour.Red, card.Color);
        Assert.Equal(At, card.Timestamp);
    }

    /// <summary>The Who field is gone; the person is the author line now.</summary>
    [Fact]
    public void AModerationCard_HasOnlyByAndWhen()
    {
        var card = ModerationEventEmbed.For(Ban(), Style, CardPicture.None);

        Assert.Equal(["By", "When"], card.Fields.Select(f => f.Name));
        Assert.Equal($"[E-Ray]({Address}/audit?subject={Actor})", card.Fields[0].Value);
        Assert.StartsWith("<t:", card.Fields[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void AModerationCard_PrintsNoIds()
        => AssertNoIdsInTheBody(ModerationEventEmbed.For(Ban(), Style, CardPicture.None));

    /// <summary>
    /// Ten banners in one message is a wall rather than a record, so the banner is left to the one
    /// card that is about a person rather than about something that happened to them.
    /// </summary>
    [Fact]
    public void AModerationCard_HasNoBanner()
        => Assert.Null(ModerationEventEmbed.For(Ban(), Style, new CardPicture(AuthorIcon: "attachment://pa.png")).ImageUrl);

    [Fact]
    public void AModerationCard_FootersTheGroupWithModbotsMark()
    {
        Assert.Equal("The Kingdom", ModerationEventEmbed.For(Ban(), Style, CardPicture.None).Footer);
        Assert.Equal(Address + "/icon-192.png", ModerationEventEmbed.For(Ban(), Style, CardPicture.None).FooterIconUrl);
        Assert.Equal("Modbot", ModerationEventEmbed.For(Ban(), CardStyle.None, CardPicture.None).Footer);
    }

    /// <summary>
    /// The author line cannot be left out and still leave the card about somebody, so this is the
    /// one place a card still prints an id.
    /// </summary>
    [Fact]
    public void AModerationCardForSomebodyWithNoName_IsHeadedByTheirId()
        => Assert.Equal(Person, ModerationEventEmbed.For(Ban(subjectName: null), Style, CardPicture.None).AuthorName);

    [Fact]
    public void WithNoPublicAddress_AModerationCardLinksNowhere()
    {
        var card = ModerationEventEmbed.For(Ban(), CardStyle.None, CardPicture.None);

        Assert.Null(card.Url);
        Assert.Null(card.AuthorUrl);
        Assert.Equal("E-Ray", Assert.Single(card.Fields, f => f.Name == "By").Value);
    }

    // ── An instance ──────────────────────────────────────────────────────────────────────

    private static VRChatInstance Instance() => new()
    {
        Id = Guid.CreateVersion7(),
        Location = $"{World}:26093~group(grp_a)",
        WorldId = World,
        VRChatInstanceId = "26093~group(grp_a)",
        GroupAccessType = "plus",
        Region = "us",
        OpenedAt = At,
        LastSeenAt = At.AddHours(2),
        HeadCount = 3,
        SeenInGroupList = true,
    };

    private static VRChatWorld TheBlackCat() => new()
    {
        WorldId = World,
        Name = "The Black Cat",
        ImageUrl = "https://api.vrchat.cloud/api/1/file/file_a/1/file",
        Capacity = 32,
    };

    [Fact]
    public void AnInstanceCard_IsTitledByItsWorldAndHeadedByTheGroup()
    {
        var card = InstanceCard.For(
            Instance(), TheBlackCat(), At.AddHours(2), null, Style, new CardPicture(Image: "attachment://pw.png"));

        Assert.Equal("The Black Cat", card.Title);
        Assert.Equal("The Kingdom", card.AuthorName);
        Assert.Equal("attachment://pw.png", card.ImageUrl);
        Assert.Equal(CardColour.Green, card.Color);
        Assert.StartsWith("https://vrchat.com/home/launch", card.Url!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The picture the poster sends. Reading it off the row was the bug: VRChat's hosts refuse
    /// Discord, so the address that used to be handed straight to the embed was never drawn.
    /// </summary>
    [Fact]
    public void AnInstanceCards_WorldPictureComesFromTheRowButGoesThroughTheMessage()
    {
        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_a/1/file", InstanceCard.PictureOf(TheBlackCat()));
        Assert.Null(InstanceCard.PictureOf(null));

        Assert.Null(InstanceCard.For(Instance(), TheBlackCat(), At.AddHours(2)).ImageUrl);
    }

    [Fact]
    public void AnInstanceCard_PrintsNoIds()
        => AssertNoIdsInTheBody(InstanceCard.For(Instance(), TheBlackCat(), At.AddHours(2), ["Rin", "Ada"], Style));

    // ── A calendar event ─────────────────────────────────────────────────────────────────

    private static CalendarEvent Party() => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Friday night",
        Description = "Come along.",
        StartsAt = At,
        EndsAt = At.AddHours(3),
        WorldId = World,
        AccessType = "plus",
        Region = "us",
        State = CalendarEventStates.Scheduled,
    };

    [Fact]
    public void ACalendarCard_LinksItsWorldRatherThanNamingAnId()
    {
        var card = CalendarCard.For(
            Party(), new CalendarOccurrence(At, At.AddHours(3)), TheBlackCat(),
            CalendarCardState.Scheduled, null, Style, new CardPicture(Image: "attachment://pw.png"));

        Assert.Equal("Friday night", card.Title);
        Assert.Equal("The Kingdom", card.AuthorName);
        Assert.Equal("attachment://pw.png", card.ImageUrl);
        Assert.Equal(CardColour.Violet, card.Color);

        Assert.Equal(
            $"[The Black Cat]({Address}/analytics/worlds?subject=world%3A{World})",
            Assert.Single(card.Fields, f => f.Name == "World").Value);
    }

    [Fact]
    public void ACalendarCard_PrintsNoIds()
        => AssertNoIdsInTheBody(CalendarCard.For(
            Party(), new CalendarOccurrence(At, At.AddHours(3)), TheBlackCat(),
            CalendarCardState.Open, "https://vrchat.com/home/launch?x", Style));

    /// <summary>
    /// The moderators' own picture is on a host that serves anybody, so it is linked rather than
    /// sent -- only VRChat's addresses have to go through the message.
    /// </summary>
    [Fact]
    public void ACalendarEventsOwnPictureIsLinkedAsItIs()
    {
        var party = Party();
        party.ImageUrl = "https://pictures.example.com/friday.png";

        Assert.Equal("https://pictures.example.com/friday.png", CalendarCard.OwnPicture(party));

        party.ImageUrl = "http://pictures.example.com/friday.png";
        Assert.Null(CalendarCard.OwnPicture(party));
    }

    // ── An alert and an insight ──────────────────────────────────────────────────────────

    [Fact]
    public void AnAlertCard_CarriesTheFigureAndModbotsMark()
    {
        var alert = new Alert
        {
            Watcher = AlertWatchers.VRChatJoins,
            At = At,
            WindowStart = At.AddHours(-1),
            WindowEnd = At,
            Now = 41,
            Normal = 6,
            Text = "Four times the usual joins in an hour.",
            Link = "/analytics/group",
        };

        var card = AlertPoster.Card(alert, Style);

        Assert.Equal(CardColour.Amber, card.Color);
        Assert.Equal("Unusual activity", card.Footer);
        Assert.Equal(Address + "/icon-192.png", card.FooterIconUrl);
        Assert.Equal($"{Address}/analytics/group", card.Url);
        Assert.Equal(["Now", "Normally", "Window"], card.Fields.Select(f => f.Name));

        // An alert is about a number, not a person or a place.
        Assert.Null(card.AuthorName);
        Assert.Null(card.ImageUrl);
        Assert.Null(card.ThumbnailUrl);
    }

    [Fact]
    public void AnInsightCard_CarriesModbotsMark()
    {
        var insight = new Insight
        {
            Kind = "group",
            FirstDay = new DateOnly(2026, 9, 7),
            LastDay = new DateOnly(2026, 9, 13),
            CreatedAt = At,
            Text = "A quiet week.",
            Model = "claude",
        };

        var card = InsightPoster.Card(insight, Style);

        Assert.Equal(CardColour.Violet, card.Color);
        Assert.Equal("A quiet week.", card.Description);
        Assert.Equal("AI insight · claude", card.Footer);
        Assert.Equal(Address + "/icon-192.png", card.FooterIconUrl);
    }

    // ── The lookup reply ─────────────────────────────────────────────────────────────────

    private static PersonSummary Profile() => new(
        Person,
        new VRChatUser
        {
            UserId = Person,
            DisplayName = "jessie",
            IconUrl = "https://api.vrchat.cloud/api/1/file/file_i/1/file",
            BannerUrl = "https://api.vrchat.cloud/api/1/file/file_b/1/file",
            RepresentedGroupId = "grp_a",
            RepresentedGroupName = "The Kingdom",
            RepresentedGroupIconUrl = "https://api.vrchat.cloud/api/1/file/file_g/1/file",
            LastRefreshedAt = At,
        },
        Bans: 1, Kicks: 0, Warns: 2,
        LastBannedAt: At, LastUnbannedAt: null,
        Recent: [Ban()]);

    /// <summary>
    /// The one card about a person rather than about something that happened to them, so it gets
    /// the profile as VRChat shows it: the name, the face, the banner, and the group they
    /// represent.
    /// </summary>
    [Fact]
    public void TheLookupCard_ShowsTheProfileAsVRChatDoes()
    {
        var card = DiscordCommandHandler.ProfileCard(
            Profile(),
            Style,
            new CardPicture("attachment://pi.png", "attachment://pb.png", "attachment://pg.png"));

        Assert.Equal("jessie", card.Title);
        Assert.Equal($"{Address}/audit?subject={Person}", card.Url);
        Assert.Equal("attachment://pi.png", card.ThumbnailUrl);
        Assert.Equal("attachment://pb.png", card.ImageUrl);

        Assert.Equal("The Kingdom", card.AuthorName);
        Assert.Equal("attachment://pg.png", card.AuthorIconUrl);

        Assert.Equal(CardColour.Red, card.Color);
    }

    /// <summary>
    /// The id used to be the first line under the title. It is in the link instead, and the profile
    /// shows it with a control that copies it.
    /// </summary>
    [Fact]
    public void TheLookupCard_PrintsNoIds()
    {
        var card = DiscordCommandHandler.ProfileCard(Profile(), Style, CardPicture.None);

        Assert.Null(card.Description);
        AssertNoIdsInTheBody(card);
    }

    [Fact]
    public void TheLookupCardForSomebodyNeverRead_SaysSoAndIsStillTitledByTheirId()
    {
        var card = DiscordCommandHandler.ProfileCard(
            new PersonSummary(Person, null, 0, 0, 0, null, null, []),
            Style,
            CardPicture.None);

        Assert.Equal(Person, card.Title);
        Assert.Contains("has not read the profile yet", card.Description!, StringComparison.Ordinal);
        Assert.Null(card.ThumbnailUrl);
        Assert.Null(card.ImageUrl);
        Assert.Null(card.AuthorName);
    }

    /// <summary>Every name in the recent list is a link, the same as on a card.</summary>
    [Fact]
    public void TheLookupCard_LinksTheNamesInItsRecentEvents()
    {
        var recent = Assert.Single(
            DiscordCommandHandler.ProfileCard(Profile(), Style, CardPicture.None).Fields,
            f => f.Name == "Recent moderation events");

        Assert.Contains($"[jessie]({Address}/audit?subject={Person})", recent.Value, StringComparison.Ordinal);
        Assert.Contains($"[E-Ray]({Address}/audit?subject={Actor})", recent.Value, StringComparison.Ordinal);
    }
}
