using System.Text;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Tests.Cards;

/// <summary>
/// One card per kind of event: what titles it, what it says beyond By and When, what is escaped,
/// what is cut -- and, on every one of them, that no id is printed where a name will do.
/// </summary>
/// <remarks>
/// Every builder is pure, so none of this needs a database or a gateway. The pictures arrive as the
/// addresses a poster would have resolved, which is what keeps the builders pure.
/// </remarks>
public class EventCardTests
{
    private const string Address = "https://modbot.example.com";
    private const string Person = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Actor = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";
    private const string World = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";
    private const string Location = World + ":26093~group(grp_a)";

    private static readonly DateTimeOffset At = new(2026, 9, 13, 2, 25, 36, TimeSpan.Zero);

    private static readonly CardStyle Style = new(Address, "The Kingdom", Address + "/icon-192.png");

    /// <summary>An event about a person, with nothing lifted out of its payload.</summary>
    private static ModerationEventView Event(string type, string? description = null) => new(
        52, type, At,
        Person, "jessie",
        Actor, "E-Ray",
        description);

    /// <summary>An event whose subject is a location, a role or a notification rather than a person.</summary>
    private static ModerationEventView Thing(string type, string subjectId, EventDetails details) => new(
        52, type, At,
        subjectId, null,
        Actor, "E-Ray",
        null,
        Details: details);

    private static string? Field(DiscordEmbedContent card, string name)
        => card.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    /// <summary>What a moderator sees: the markdown with every link's address taken out.</summary>
    private static string Visible(string markdown)
    {
        var visible = new StringBuilder(markdown.Length);

        for (var i = 0; i < markdown.Length; i++)
        {
            if (markdown[i] == ']' && i + 1 < markdown.Length && markdown[i + 1] == '('
                && markdown.IndexOf(')', i + 2) is var close && close > 0)
            {
                visible.Append(']');
                i = close;
                continue;
            }

            visible.Append(markdown[i]);
        }

        return visible.ToString();
    }

    private static void AssertNoIdsInTheBody(DiscordEmbedContent card)
    {
        var body = new List<string>();

        if (card.Description is { } description)
            body.Add(description);

        body.AddRange(card.Fields.Select(f => f.Value));

        foreach (var text in body.Select(Visible))
        {
            Assert.DoesNotContain(Person, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Actor, text, StringComparison.Ordinal);
            Assert.DoesNotContain(World, text, StringComparison.Ordinal);
        }
    }

    // ── The fallback, which is the safety of the whole converter ─────────────────────────────

    /// <summary>
    /// More than fifty types can be routed to a channel and they come from VRChat, not from Modbot.
    /// One nobody has written a builder for still posts, and posts the shape every card used to
    /// have.
    /// </summary>
    [Fact]
    public void AnUnrecognisedType_StillGetsTodaysCard()
    {
        var card = EventCard.For(Event("vrchat.group.something.nobody.mapped"), Style, CardPicture.None);

        Assert.Equal("vrchat.group.something.nobody.mapped", card.Title);
        Assert.Equal("jessie", card.AuthorName);
        Assert.Equal($"{Address}/audit?subject={Person}", card.AuthorUrl);
        Assert.Equal(["By", "When"], card.Fields.Select(f => f.Name));
    }

    [Fact]
    public void AKindWithNoBuilder_IsTheSameCardTheOldOneShapeGave()
    {
        var e = Event(FactType.MemberJoined, "User jessie joined the group.");

        var built = EventCard.For(e, Style, CardPicture.None);
        var fallback = ModerationEventEmbed.For(e, Style, CardPicture.None);

        Assert.Equal(fallback.Title, built.Title);
        Assert.Equal(fallback.Description, built.Description);
        Assert.Equal(fallback.Color, built.Color);
        Assert.Equal(fallback.Url, built.Url);
        Assert.Equal(fallback.AuthorName, built.AuthorName);
        Assert.Equal(fallback.AuthorUrl, built.AuthorUrl);
        Assert.Equal(fallback.Footer, built.Footer);
        Assert.Equal(fallback.Fields, built.Fields);
    }

    // ── Bans, unbans and kicks ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FactType.MemberBanned, "Banned from the group")]
    [InlineData(FactType.MemberUnbanned, "Unbanned from the group")]
    [InlineData(FactType.MemberKicked, "Kicked from the group")]
    public void ABanAnUnbanAndAKick_SayWhichGroupTheyWereFrom(string type, string title)
        => Assert.Equal(title, EventCard.For(Event(type), Style, CardPicture.None).Title);

    /// <summary>
    /// VRChat's ban, unban and kick entries carry an empty payload -- their <c>description</c> is
    /// VRChat's own template and states no reason -- so there is no reason field to fill. Modbot's
    /// own actions carry theirs inside that same sentence, which the card already quotes.
    /// </summary>
    [Fact]
    public void ABan_HasOnlyByAndWhen_BecauseVRChatSendsNoReason()
    {
        var card = EventCard.For(
            Event(FactType.MemberBanned, "User jessie was preemptively banned by E-Ray."),
            Style,
            new CardPicture(AuthorIcon: "attachment://pa.png"));

        Assert.Equal(["By", "When"], card.Fields.Select(f => f.Name));
        Assert.Equal("> User jessie was preemptively banned by E-Ray.", card.Description);
        Assert.Equal("attachment://pa.png", card.AuthorIconUrl);
        Assert.Equal(CardColour.Red, card.Color);
        AssertNoIdsInTheBody(card);
    }

    // ── A warn and an instance kick ──────────────────────────────────────────────────────────

    private static ModerationEventView InTheBlackCat(string type, string? worldName) => Event(type) with
    {
        WorldId = World,
        InstanceId = "26093",
        WorldName = worldName,
    };

    [Fact]
    public void AWarn_SaysWhichInstanceItHappenedIn()
    {
        var card = EventCard.For(InTheBlackCat(FactType.GroupInstanceWarn, "The Black Cat"), Style, CardPicture.None);

        Assert.Equal("Warned in an instance", card.Title);
        Assert.Equal(
            $"[The Black Cat]({Address}/analytics/worlds?subject=world%3A{World}) #26093",
            Field(card, "Where"));

        AssertNoIdsInTheBody(card);
    }

    [Fact]
    public void AnInstanceKick_SaysWhereToo()
    {
        var card = EventCard.For(InTheBlackCat(FactType.GroupInstanceKick, "The Black Cat"), Style, CardPicture.None);

        Assert.Equal("Kicked from an instance", card.Title);
        Assert.NotNull(Field(card, "Where"));
    }

    /// <summary>
    /// A world Modbot has never read has no name, and a world id in a field is what the links
    /// took out of these cards, so the field is left off instead.
    /// </summary>
    [Fact]
    public void AWarnInAWorldModbotHasNeverRead_LeavesWhereOff()
    {
        var card = EventCard.For(InTheBlackCat(FactType.GroupInstanceWarn, worldName: null), Style, CardPicture.None);

        Assert.Null(Field(card, "Where"));
        AssertNoIdsInTheBody(card);
    }

    // ── Roles ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARoleGiven_NamesTheRoleInTheTitle()
    {
        var e = Event(FactType.RoleGranted) with { Details = new EventDetails(RoleId: "grol_a", RoleName: "Moderator") };

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal("Given the Moderator role", card.Title);

        // Named once. The same thing twice on one card is noise, not detail.
        Assert.Equal(["By", "When"], card.Fields.Select(f => f.Name));
    }

    [Fact]
    public void ARoleTakenAway_SaysSo()
    {
        var e = Event(FactType.RoleRevoked) with { Details = new EventDetails(RoleName: "Moderator") };

        Assert.Equal("Lost the Moderator role", EventCard.For(e, Style, CardPicture.None).Title);
    }

    /// <summary>The title is a slot Discord prints literally, so escaping it would show backslashes.</summary>
    [Fact]
    public void ARoleNameIsPrintedAsItIs_AndStrippedOfControlCharacters()
    {
        var e = Event(FactType.RoleGranted) with { Details = new EventDetails(RoleName: "**Mod**\nsquad") };

        var title = EventCard.For(e, Style, CardPicture.None).Title;

        Assert.Equal("Given the **Mod** squad role", title);
        Assert.DoesNotContain(@"\", title, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoleEventWithNoRoleName_FallsBackToTheLabel()
    {
        var card = EventCard.For(Event(FactType.RoleGranted), Style, CardPicture.None);

        Assert.Equal(ModerationEventEmbed.LabelFor(FactType.RoleGranted), card.Title);
    }

    /// <summary>
    /// A role change is about the role, not about a person, so the group heads it rather than a
    /// role id dressed up as somebody's name.
    /// </summary>
    [Fact]
    public void ARoleChange_ListsWhatChangedAndIsHeadedByTheGroup()
    {
        var e = Thing(
            FactType.RoleUpdated,
            "grol_4a1f",
            new EventDetails(
                RoleName: "Moderator",
                Changed:
                [
                    new EventChange("name", "Mods", "Moderator"),
                    new EventChange("lastUpdatedByUserId", null, Actor),
                ]));

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal("The Moderator role changed", card.Title);
        Assert.Equal("The Kingdom", card.AuthorName);
        Assert.Null(card.AuthorIconUrl);

        var changed = Field(card, "Changed")!;
        Assert.Contains("**Name**: Mods → Moderator", changed, StringComparison.Ordinal);

        // An id says nothing a moderator wanted, so the field is named as changed and not printed.
        Assert.Contains("**Last updated by user id**: changed", changed, StringComparison.Ordinal);
        AssertNoIdsInTheBody(card);
    }

    [Fact]
    public void AChangedValueIsEscaped_BecauseAFieldIsMarkdown()
    {
        var e = Thing(
            FactType.RoleUpdated, "grol_a",
            new EventDetails(RoleName: "Mods", Changed: [new EventChange("name", "**loud**", "quiet")]));

        Assert.Contains(@"\*\*loud\*\*", Field(EventCard.For(e, Style, CardPicture.None), "Changed")!, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatWasNothing_ReadsAsNothing()
    {
        var e = Thing(
            FactType.RoleUpdated, "grol_a",
            new EventDetails(RoleName: "Mods", Changed: [new EventChange("name", null, "Mods")]));

        Assert.Contains("nothing → Mods", Field(EventCard.For(e, Style, CardPicture.None), "Changed")!, StringComparison.Ordinal);
    }

    [Fact]
    public void AVeryLongListOfChanges_IsCutAndSaysHowManyAreLeft()
    {
        var changes = Enumerable.Range(0, 12)
            .Select(i => new EventChange($"field{i}", "before", "after"))
            .ToArray();

        var e = Thing(FactType.RoleUpdated, "grol_a", new EventDetails(RoleName: "Mods", Changed: changes));

        var changed = Field(EventCard.For(e, Style, CardPicture.None), "Changed")!;

        Assert.Contains("and 4 more", changed, StringComparison.Ordinal);
        Assert.True(changed.Length <= 1024);
    }

    // ── An announcement and a group post ─────────────────────────────────────────────────────

    [Fact]
    public void AnAnnouncement_IsTitledByItsOwnTitleAndBodiedByItsMessage()
    {
        var e = Thing(
            FactType.GroupInstanceAnnouncement,
            Location,
            new EventDetails(Title: "Doors at eight", Message: "Come along, everyone.")) with { WorldId = World };

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal("Doors at eight", card.Title);
        Assert.Equal("Come along, everyone.", card.Description);
        Assert.Equal("The Kingdom", card.AuthorName);
        Assert.Equal($"{Address}/analytics/worlds?subject=world%3A{World}", card.Url);
        AssertNoIdsInTheBody(card);
    }

    [Fact]
    public void AnAnnouncementsMessage_IsCutWithAnEllipsis()
    {
        var e = Thing(
            FactType.GroupInstanceAnnouncement, Location,
            new EventDetails(Title: "Long", Message: new string('a', 500)));

        var description = EventCard.For(e, Style, CardPicture.None).Description!;

        Assert.Equal(300, description.Length);
        Assert.EndsWith("…", description, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupPost_IsTitledByItsOwnTitleAndSaysWhoMaySeeIt()
    {
        var e = Thing(
            FactType.GroupPostCreated,
            "not_9f2c",
            new EventDetails(Title: "Rules update", Text: "Read them.", AuthorId: Actor, Visibility: "group"));

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal("Rules update", card.Title);
        Assert.Equal("Read them.", card.Description);
        Assert.Equal("group", Field(card, "Seen by"));
        Assert.Equal("The Kingdom", card.AuthorName);

        // A post names no world, so there is nothing for the card to link.
        Assert.Null(card.Url);
        AssertNoIdsInTheBody(card);
    }

    [Fact]
    public void AnEventWithNoTitleOfItsOwn_KeepsItsLabel()
    {
        var e = Thing(FactType.GroupPostCreated, "not_9f2c", EventDetails.None);

        Assert.Equal(
            ModerationEventEmbed.LabelFor(FactType.GroupPostCreated),
            EventCard.For(e, Style, CardPicture.None).Title);
    }

    // ── An instance opened and closed ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FactType.GroupInstanceCreated, "Instance opened")]
    [InlineData(FactType.GroupInstanceClosed, "Instance closed")]
    public void AnInstanceOpenedOrClosed_SaysWhichAndWhoItWasOpenTo(string type, string title)
    {
        var e = Thing(type, Location, new EventDetails(GroupAccessType: "plus")) with { WorldId = World };

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal(title, card.Title);
        Assert.Equal("Group plus", Field(card, "Open to"));
        AssertNoIdsInTheBody(card);
    }

    [Theory]
    [InlineData("public", "Anyone")]
    [InlineData("members", "Group members")]
    [InlineData("something-new", "something-new")]
    public void HowOpenAnInstanceWas_ReadsInPlainWords(string accessType, string shown)
    {
        var e = Thing(FactType.GroupInstanceCreated, Location, new EventDetails(GroupAccessType: accessType));

        Assert.Equal(shown, Field(EventCard.For(e, Style, CardPicture.None), "Open to"));
    }

    // ── A calendar entry ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACalendarEntry_IsTitledByItsOwnTitleAndSaysWhatKindAndWhoMayCome()
    {
        var e = Thing(
            FactType.CalendarEventCreated,
            "cal_7d1a",
            new EventDetails(Title: "Friday night", Kind: "event", AccessType: "members"));

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal("Friday night", card.Title);
        Assert.Equal("event", Field(card, "Kind"));
        Assert.Equal("Group members", Field(card, "Open to"));
        AssertNoIdsInTheBody(card);
    }

    // ── Join requests ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Being blocked is not being turned down: somebody blocked cannot ask again. The two keep
    /// their own words here because Modbot keeps them apart everywhere else.
    /// </summary>
    [Theory]
    [InlineData(FactType.JoinRequestRejected, "Join request rejected")]
    [InlineData(FactType.JoinRequestBlocked, "Join request blocked")]
    public void AJoinRequestTurnedDown_KeepsItsOwnWordsAndAddsNothing(string type, string title)
    {
        var card = EventCard.For(Event(type), Style, CardPicture.None);

        Assert.Equal(title, card.Title);
        Assert.Equal(["By", "When"], card.Fields.Select(f => f.Name));
        Assert.Equal(CardColour.Grey, card.Color);
    }

    // ── A profile that changed ───────────────────────────────────────────────────────────────

    [Fact]
    public void AChangedName_TitlesTheCardAndShowsBeforeAndAfter()
    {
        var e = Event(FactType.UserProfileChanged) with
        {
            ActorId = null,
            ActorName = null,
            Details = new EventDetails(Changed: [new EventChange("displayName", "jessie", "Jessie")]),
        };

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal("Name changed", card.Title);
        Assert.Equal("jessie", card.AuthorName);
        Assert.Equal(["When", "Changed"], card.Fields.Select(f => f.Name));
        Assert.Equal("**Name**: jessie → Jessie", Field(card, "Changed"));
    }

    [Fact]
    public void AProfileChangeThatIsNotTheName_KeepsItsLabel()
    {
        var e = Event(FactType.UserProfileChanged) with
        {
            Details = new EventDetails(Changed: [new EventChange("bio", "hi", "hello")]),
        };

        var card = EventCard.For(e, Style, CardPicture.None);

        Assert.Equal(ModerationEventEmbed.LabelFor(FactType.UserProfileChanged), card.Title);
        Assert.Equal("**Bio**: hi → hello", Field(card, "Changed"));
    }

    [Fact]
    public void AProfileChangeWithNothingReadable_JustHasByAndWhen()
    {
        var card = EventCard.For(Event(FactType.UserProfileChanged), Style, CardPicture.None);

        Assert.Equal(["By", "When"], card.Fields.Select(f => f.Name));
    }

    // ── What every card still does ───────────────────────────────────────────────────────────

    [Fact]
    public void WithNoPublicAddress_NoCardLinksAnywhere()
    {
        var e = Thing(FactType.GroupInstanceCreated, Location, new EventDetails(GroupAccessType: "plus")) with
        {
            WorldId = World,
        };

        var card = EventCard.For(e, CardStyle.None, CardPicture.None);

        Assert.Null(card.Url);
        Assert.Null(card.AuthorUrl);
        Assert.Equal("Modbot", card.Footer);
    }

    /// <summary>
    /// Ten banners in one message is a wall rather than a record, so no card built here carries one.
    /// </summary>
    [Fact]
    public void NoCard_CarriesABanner()
    {
        foreach (var type in new[]
                 {
                     FactType.MemberBanned, FactType.GroupInstanceWarn, FactType.RoleGranted,
                     FactType.RoleUpdated, FactType.GroupInstanceAnnouncement, FactType.GroupPostCreated,
                     FactType.GroupInstanceCreated, FactType.GroupInstanceClosed, FactType.CalendarEventCreated,
                     FactType.JoinRequestRejected, FactType.UserProfileChanged, FactType.AvatarChanged,
                 })
        {
            var card = EventCard.For(Event(type), Style, new CardPicture(AuthorIcon: "attachment://pa.png"));

            Assert.Null(card.ImageUrl);
            Assert.Null(card.ThumbnailUrl);
        }
    }

    /// <summary>Nothing in the table throws, whatever the payload did or did not carry.</summary>
    [Fact]
    public void EverySendableType_BuildsACard()
    {
        foreach (var type in Modbot.Core.Discord.DiscordEventTypes.Sendable)
        {
            var card = EventCard.For(Event(type, "Something happened."), Style, CardPicture.None);

            Assert.False(string.IsNullOrWhiteSpace(card.Title));
            Assert.True(card.Title.Length <= 256);
        }
    }
}

/// <summary>
/// What a card is allowed to read out of a fact's payload: exactly the names the audit-log mapper
/// lifts, never VRChat's own <c>auditData</c>, and <c>changed</c> as before-and-after pairs.
/// </summary>
public class EventPayloadTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 2, 25, 36, TimeSpan.Zero);

    private static ModbotEvent Fact(string type, string data) => new()
    {
        Id = 7,
        Type = type,
        OccurredAt = At,
        ObservedAt = At,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = "usr_target",
        ActorPlatform = FactPlatform.VRChat,
        ActorId = "usr_actor",
        Source = FactSource.AuditLog,
        Data = data,
    };

    private static ModerationEventView Read(ModbotEvent fact)
        => ModerationEventView.From(fact, new Dictionary<string, string?>(StringComparer.Ordinal));

    [Fact]
    public void TheLiftedNames_SurviveTheTrip()
    {
        var view = Read(Fact(
            FactType.GroupPostCreated,
            """
            {"actorDisplayName":"E-Ray","description":"Group post created by E-Ray",
             "title":"Rules update","text":"Read them.","authorId":"usr_a","visibility":"group",
             "roleId":"grol_a","roleName":"Moderator","groupAccessType":"plus",
             "message":"Come along.","type":"event","accessType":"members"}
            """));

        Assert.Equal("Rules update", view.What.Title);
        Assert.Equal("Read them.", view.What.Text);
        Assert.Equal("usr_a", view.What.AuthorId);
        Assert.Equal("group", view.What.Visibility);
        Assert.Equal("grol_a", view.What.RoleId);
        Assert.Equal("Moderator", view.What.RoleName);
        Assert.Equal("plus", view.What.GroupAccessType);
        Assert.Equal("Come along.", view.What.Message);
        Assert.Equal("event", view.What.Kind);
        Assert.Equal("members", view.What.AccessType);
    }

    /// <summary>
    /// VRChat's own payload is the record, not the display: its shape is documented only as
    /// "dependent on the event type", so nothing is read out of it.
    /// </summary>
    [Fact]
    public void AuditData_IsNotRead()
    {
        var view = Read(Fact(
            FactType.GroupInstanceKick,
            """{"auditData":{"title":"not this","roleName":"nor this"}}"""));

        Assert.Null(view.What.Title);
        Assert.Null(view.What.RoleName);
    }

    [Fact]
    public void Changed_ReadsAsBeforeAndAfterPairs()
    {
        var view = Read(Fact(
            FactType.UserProfileChanged,
            """{"changed":{"displayName":{"old":"jessie","new":"Jessie"},"bio":{"old":null,"new":"hi"}}}"""));

        Assert.Collection(
            view.What.Changes,
            c =>
            {
                Assert.Equal("displayName", c.Name);
                Assert.Equal("jessie", c.Before);
                Assert.Equal("Jessie", c.After);
            },
            c =>
            {
                Assert.Equal("bio", c.Name);
                Assert.Null(c.Before);
                Assert.Equal("hi", c.After);
            });
    }

    /// <summary>A value that is not text keeps its own wording rather than being guessed at.</summary>
    [Fact]
    public void AChangedValueThatIsNotText_IsKeptAsItWasWritten()
    {
        var view = Read(Fact(
            FactType.RoleUpdated,
            """{"changed":{"isSelfAssignable":{"old":false,"new":true}}}"""));

        var change = Assert.Single(view.What.Changes);
        Assert.Equal("false", change.Before);
        Assert.Equal("true", change.After);
    }

    [Fact]
    public void AnythingUnderChangedThatIsNotAPair_IsSkipped()
    {
        var view = Read(Fact(
            FactType.RoleUpdated,
            """{"changed":{"permissions":{"old":[],"new":["x"]},"count":4,"half":{"old":1}}}"""));

        var change = Assert.Single(view.What.Changes);
        Assert.Equal("permissions", change.Name);
    }

    [Fact]
    public void APayloadThatIsNotJson_LeavesEverythingEmpty()
    {
        var view = Read(Fact(FactType.MemberBanned, "not json at all"));

        Assert.Null(view.Description);
        Assert.Empty(view.What.Changes);
    }

    /// <summary>
    /// The world and the instance are the fact's own columns, which the audit-log mapper fills for
    /// an instance event. A world Modbot has read has a name here; one it has not simply does not.
    /// </summary>
    [Fact]
    public void TheWorldsNameComesFromWhatModbotHasRead()
    {
        var fact = Fact(FactType.GroupInstanceWarn, "{}");
        fact.WorldId = "wrld_a";
        fact.InstanceId = "26093";

        var worlds = new Dictionary<string, string?>(StringComparer.Ordinal) { ["wrld_a"] = "The Black Cat" };

        var view = ModerationEventView.From(fact, new Dictionary<string, string?>(StringComparer.Ordinal), worlds);

        Assert.Equal("wrld_a", view.WorldId);
        Assert.Equal("26093", view.InstanceId);
        Assert.Equal("The Black Cat", view.WorldName);

        Assert.Null(ModerationEventView.From(fact, new Dictionary<string, string?>(StringComparer.Ordinal)).WorldName);
    }
}
