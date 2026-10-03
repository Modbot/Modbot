using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Tests.Cards;

/// <summary>
/// Cards that tell the whole story: a Discord member is headed by their Discord name and picture
/// and linked to their Discord view, a Discord reason and a timeout's end are on the card, and an
/// action taken from Modbot says who decided it and why.
/// </summary>
/// <remarks>
/// The cards for the server's own channels and roles, and for messages removed, are the event
/// cards' own (Modbot's own facts and Discord's, event cards design §9) and are tested there.
/// </remarks>
public class WholeStoryCardTests
{
    private const string Address = "https://modbot.example.com";
    private const string Member = "900000000000000777";
    private const string DiscordModerator = "900000000000000111";
    private const string Person = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string ModbotVRChat = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";
    private const string Avatar = "https://cdn.discordapp.com/avatars/900000000000000777/abc.png";

    private static readonly Guid Moderator = Guid.Parse("0199a3b4-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset At = new(2026, 9, 13, 2, 25, 36, TimeSpan.Zero);
    private static readonly CardStyle Style = new(Address, "The Kingdom", Address + "/icon-192.png");

    private static ModerationEventView OnDiscord(string type, EventDetails details, string? name = "jessie") => new(
        52, type, At,
        Member, name,
        DiscordModerator, "E-Ray",
        null,
        Details: details,
        SubjectPlatform: FactPlatform.Discord,
        ActorPlatform: FactPlatform.Discord);

    private static string? Field(DiscordEmbedContent card, string name)
        => card.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    /// <summary>What the Discord event recorder writes, as a card reads it.</summary>
    private static EventDetails Recorded(string? reason = null, string? until = null)
        => new(Modbot: new ModbotDetails(Until: until, Reason: reason));

    // ── Discord members ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADiscordBan_IsHeadedByTheMembersDiscordName_AndLinksToTheirDiscordView()
    {
        var card = EventCard.For(
            OnDiscord(FactType.DiscordMemberBanned, Recorded(reason: "Spam in #general")),
            Style,
            new CardPicture(AuthorIcon: Avatar));

        var link = $"{Address}/discord/members?subject=discord-person%3A{Member}";

        Assert.Equal("Banned from Discord", card.Title);
        Assert.Equal("jessie", card.AuthorName);
        Assert.Equal(link, card.AuthorUrl);
        Assert.Equal(link, card.Url);
        Assert.Equal(Avatar, card.AuthorIconUrl);
        Assert.Equal("Spam in #general", Field(card, "Reason"));

        // The Discord moderator is a Discord account too, never a VRChat profile.
        Assert.Equal(
            $"[E-Ray]({Address}/discord/members?subject=discord-person%3A{DiscordModerator})",
            Field(card, "By"));
    }

    [Fact]
    public void ADiscordBanWithNoReason_HasNoReasonField()
    {
        var card = EventCard.For(OnDiscord(FactType.DiscordMemberBanned, EventDetails.None), Style, CardPicture.None);

        Assert.Null(Field(card, "Reason"));
    }

    /// <summary>
    /// A member Modbot has no name for is Discord's own mention of them, which Discord draws as their
    /// name: never a bare id on the author line.
    /// </summary>
    [Fact]
    public void ADiscordMemberModbotHasNoNameFor_IsDiscordsMention_StillLinkedToTheDiscordView()
    {
        var card = EventCard.For(
            OnDiscord(FactType.DiscordMemberKicked, EventDetails.None, name: null), Style, new CardPicture(AuthorIcon: Avatar));

        Assert.Null(card.AuthorName);
        Assert.Null(card.AuthorIconUrl);
        Assert.Equal($"<@{Member}>", Field(card, "Who"));
        Assert.Contains("discord-person", card.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimeout_SaysWhenItEnds_InEachReadersOwnTime_AndWhy()
    {
        var until = At.AddHours(6);

        var card = EventCard.For(
            OnDiscord(FactType.DiscordMemberTimedOut, Recorded(reason: "Cool off", until: until.ToString("O"))),
            Style,
            CardPicture.None);

        Assert.Equal("Timed out on Discord", card.Title);
        Assert.Equal($"<t:{until.ToUnixTimeSeconds()}:f>", Field(card, "Until"));
        Assert.Equal("Cool off", Field(card, "Reason"));
    }

    [Fact]
    public void ADiscordRoleGiven_NamesTheRole()
    {
        var card = EventCard.For(
            OnDiscord(FactType.DiscordRoleGranted, new EventDetails(RoleName: "Verified")), Style, CardPicture.None);

        Assert.Equal("Given the Verified role on Discord", card.Title);
        Assert.Equal("jessie", card.AuthorName);
    }

    // ── Actions taken from Modbot ───────────────────────────────────────────────────────────

    private static ModerationEventView VRChatBan(EventDecision? decision) => new(
        60, FactType.MemberBanned, At,
        Person, "jessie",
        ModbotVRChat, "Modbot",
        "Modbot banned jessie",
        ActorPlatform: FactPlatform.VRChat,
        Decision: decision);

    [Fact]
    public void VRChatsBan_OfOneModbotMade_SaysWhoDecidedItAndWhy()
    {
        var card = EventCard.For(
            VRChatBan(new EventDecision(Moderator.ToString(), "sam", ["Harassment", "Ban evasion"])),
            Style,
            CardPicture.None);

        Assert.Equal("Banned from the group", card.Title);
        Assert.Equal($"[sam]({Address}/audit?subject=account%3A{Moderator})", Field(card, "Decided by"));
        Assert.Equal("Harassment, Ban evasion", Field(card, "Reasons"));

        // VRChat's own actor is still the one By names.
        Assert.Equal($"[Modbot]({Address}/audit?subject={ModbotVRChat})", Field(card, "By"));
    }

    /// <summary>Regression: a ban nobody made from Modbot is the card it always was.</summary>
    [Fact]
    public void VRChatsBan_WithNoDecision_IsUnchanged()
    {
        var card = EventCard.For(VRChatBan(null), Style, CardPicture.None);

        Assert.Equal(["By", "When"], card.Fields.Select(f => f.Name));
        Assert.Equal("> Modbot banned jessie", card.Description);
    }

    [Fact]
    public void AModbotAction_ShowsItsReasons_NotTheSentenceThatRepeatsThem()
    {
        var e = new ModerationEventView(
            61, FactType.ActionBan, At,
            Person, "jessie",
            Moderator.ToString(), "sam",
            "banned from the group by sam from Modbot: Harassment",
            Details: new EventDetails(Modbot: new ModbotDetails(ReasonLabels: ["Harassment"])),
            ActorPlatform: FactPlatform.Modbot);

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal("Banned from Modbot", card.Title);
        Assert.Null(card.Description);
        Assert.Equal("Harassment", Field(card, "Reason"));
        Assert.Null(Field(card, "Note"));
        Assert.Equal($"[sam]({Address}/audit?subject=account%3A{Moderator})", Field(card, "By"));
    }

    /// <summary>An actor carried in by an import is not a Modbot account to link to.</summary>
    [Fact]
    public void AModbotActorThatIsNotAnAccount_IsNamedWithoutALink()
    {
        var e = new ModerationEventView(
            62, FactType.NoteAdded, At,
            Person, "jessie",
            "oldbot-42", "Old Bot",
            "A note",
            ActorPlatform: FactPlatform.Modbot);

        Assert.Equal("Old Bot", Field(EventCard.For(e, Style, CardPicture.None), "By"));
    }

    /// <summary>Regression: a VRChat person's card links the way it always did.</summary>
    [Fact]
    public void AVRChatCard_StillLinksToTheVRChatPerson()
    {
        var card = EventCard.For(
            new ModerationEventView(63, FactType.GroupInstanceWarn, At, Person, "jessie", ModbotVRChat, "E-Ray", null),
            Style,
            CardPicture.None);

        Assert.Equal($"{Address}/audit?subject={Person}", card.AuthorUrl);
        Assert.Equal($"[E-Ray]({Address}/audit?subject={ModbotVRChat})", Field(card, "By"));
    }
}

/// <summary>What a card may read out of a Discord fact and a Modbot action's payload.</summary>
public class WholeStoryPayloadTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 2, 25, 36, TimeSpan.Zero);

    private static ModbotEvent DiscordFact(string type, string data) => new()
    {
        Id = 9,
        Type = type,
        OccurredAt = At,
        ObservedAt = At,
        SubjectPlatform = FactPlatform.Discord,
        SubjectId = "900000000000000777",
        ActorPlatform = FactPlatform.Discord,
        ActorId = "900000000000000111",
        Source = FactSource.Discord,
        Data = data,
    };

    private static readonly Dictionary<string, string?> NoNames = new(StringComparer.Ordinal);

    [Fact]
    public void ADiscordTimeout_ReadsItsReasonAndItsEnd()
    {
        var view = ModerationEventView.From(
            DiscordFact(
                FactType.DiscordMemberTimedOut,
                """{"reason":"Cool off","until":"2026-09-13T08:25:36+00:00","displayName":"jessie","actorDisplayName":"E-Ray"}"""),
            NoNames);

        Assert.Equal("Cool off", view.What.Own.Reason);
        Assert.Equal("2026-09-13T08:25:36+00:00", view.What.Own.Until);
        Assert.Equal("jessie", view.SubjectName);
        Assert.Equal("E-Ray", view.ActorName);
        Assert.True(view.OnDiscord);
    }

    /// <summary>
    /// The name the server shows now wins over the one written on the fact; a Discord id is never
    /// looked for among VRChat names.
    /// </summary>
    [Fact]
    public void ADiscordMember_IsNamedFromTheServer_ThenFromTheFact()
    {
        var fact = DiscordFact(FactType.DiscordMemberBanned, """{"displayName":"old name"}""");

        var vrchatNames = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["900000000000000777"] = "not a VRChat person",
        };

        var discordNames = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["900000000000000777"] = "new name",
            ["900000000000000111"] = "Mod",
        };

        Assert.Equal("old name", ModerationEventView.From(fact, vrchatNames).SubjectName);

        var named = ModerationEventView.From(fact, vrchatNames, discordNames: discordNames);
        Assert.Equal("new name", named.SubjectName);
        Assert.Equal("Mod", named.ActorName);
    }

    /// <summary>
    /// The reasons are read; the moderator's own note beside them is not, because a card goes to a
    /// channel with no audit-log gate on it.
    /// </summary>
    [Fact]
    public void AModbotAction_ReadsTheReasonsPicked_AndNeverTheNote()
    {
        var fact = new ModbotEvent
        {
            Id = 10,
            Type = FactType.ActionBan,
            OccurredAt = At,
            ObservedAt = At,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_target",
            ActorPlatform = FactPlatform.Modbot,
            ActorId = Guid.NewGuid().ToString(),
            Source = FactSource.Manual,
            Data = """{"reasonLabels":["Spam","",3,"Harassment"],"note":"Again"}""",
        };

        var view = ModerationEventView.From(fact, NoNames);

        Assert.Equal(["Spam", "Harassment"], view.What.Reasons);

        var card = EventCard.For(view, new CardStyle("https://modbot.example.com"), CardPicture.None);
        Assert.DoesNotContain(card.Fields, f => f.Value.Contains("Again", StringComparison.Ordinal));
        Assert.DoesNotContain("Again", card.Description ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntilThatIsNotATime_IsLeftOff()
    {
        var view = ModerationEventView.From(
            DiscordFact(FactType.DiscordMemberTimedOut, """{"until":"soon"}"""), NoNames);

        var card = EventCard.For(view, new CardStyle("https://modbot.example.com"), CardPicture.None);

        Assert.DoesNotContain(card.Fields, f => f.Name == "Until");
    }
}
