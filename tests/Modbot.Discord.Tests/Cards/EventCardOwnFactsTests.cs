using System.Text;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Tests.Cards;

/// <summary>
/// The cards for Modbot's own calendar, for the Discord server, and for the things Modbot keeps:
/// each names what it is about and never shows a bare id where the name should be (reported
/// 2026-10-02: "Planned event changed", "Event failed to publish" and "Joined a voice channel"
/// cards that showed only an id).
/// </summary>
/// <remarks>
/// Built from the facts as their producers write them, through <see cref="ModerationEventView.From"/>,
/// so a payload name read wrongly fails here rather than in a channel.
/// </remarks>
public class EventCardOwnFactsTests
{
    private const string Address = "https://modbot.example.com";
    private const string EventId = "01a0fe12-162e-7be7-a174-265be04648ae";
    private const string Moderator = "8f0c5d7e-4c4b-4a5e-9a51-1f8a3b0c2d11";
    private const string Member = "465050188063440901";
    private const string VoiceChannel = "1100000000000000001";
    private const string OtherChannel = "1100000000000000002";
    private const string World = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Eight = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nine = new(2026, 10, 9, 21, 0, 0, TimeSpan.Zero);

    private static readonly CardStyle Style = new(Address, "The Kingdom", Address + "/icon-192.png");

    private static ModbotEvent Fact(
        string type,
        JsonObject? data,
        FactPlatform platform = FactPlatform.Modbot,
        string subject = EventId,
        string? actor = Moderator,
        FactPlatform? actorPlatform = FactPlatform.Modbot,
        string? worldId = null) => new()
    {
        Id = 7,
        Type = type,
        OccurredAt = At,
        SubjectPlatform = platform,
        SubjectId = subject,
        ActorPlatform = actor is null ? null : actorPlatform,
        ActorId = actor,
        WorldId = worldId,
        Data = data?.ToJsonString() ?? "{}",
    };

    private static DiscordEmbedContent Card(
        ModbotEvent fact,
        IReadOnlyDictionary<string, string?>? worlds = null,
        IReadOnlyDictionary<string, string?>? discordNames = null)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal) { [Moderator] = "sam" };
        return EventCard.For(ModerationEventView.From(fact, names, worlds, discordNames), Style, CardPicture.None);
    }

    private static string? Field(DiscordEmbedContent card, string name)
        => card.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    /// <summary>Everything a moderator reads on the card, with every link's address taken out.</summary>
    private static string Visible(DiscordEmbedContent card)
    {
        var all = new StringBuilder();
        all.Append(card.AuthorName).Append('\n').Append(card.Title).Append('\n').Append(card.Description);

        foreach (var field in card.Fields)
            all.Append('\n').Append(field.Name).Append(": ").Append(field.Value);

        var text = all.ToString();
        var visible = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ']' && i + 1 < text.Length && text[i + 1] == '(' && text.IndexOf(')', i + 2) is var close && close > 0)
            {
                visible.Append(']');
                i = close;
                continue;
            }

            visible.Append(text[i]);
        }

        return visible.ToString();
    }

    private static JsonObject Fields(string title, DateTimeOffset startsAt, string? description = "Films.", string? worldId = null) => new()
    {
        ["title"] = title,
        ["description"] = description,
        ["imageUrl"] = null,
        ["vrchatImageId"] = null,
        ["category"] = "film",
        ["startsAt"] = startsAt.ToString("O"),
        ["endsAt"] = startsAt.AddHours(2).ToString("O"),
        ["timeZone"] = "Europe/London",
        ["worldId"] = worldId,
        ["accessType"] = "members",
        ["notifyMembers"] = false,
        ["channelId"] = null,
    };

    // ── The calendar ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AChange_NamesTheEvent_AndSaysWhatChanged()
    {
        var card = Card(Fact(FactType.PlannedEventChanged, new JsonObject
        {
            ["title"] = "Movie Night",
            ["before"] = Fields("Movie Night", Eight),
            ["after"] = Fields("Movie Night", Nine),
        }));

        Assert.Equal("Movie Night", card.Title);
        Assert.Equal("Planned event changed", card.AuthorName);
        Assert.Equal($"{Address}/calendar?event={EventId}", card.Url);

        var changed = Field(card, "Changed");
        Assert.NotNull(changed);
        Assert.Contains($"**Starts**: {DiscordTime.Absolute(Eight)} → {DiscordTime.Absolute(Nine)}", changed, StringComparison.Ordinal);
        Assert.Contains($"**Ends**: {DiscordTime.Absolute(Eight.AddHours(2))} → {DiscordTime.Absolute(Nine.AddHours(2))}", changed, StringComparison.Ordinal);
        Assert.DoesNotContain("Title", changed, StringComparison.Ordinal);
        Assert.DoesNotContain("Time zone", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void AChange_ToTheWordsAndTheWorld_SaysThemInTheFormsOwnWords()
    {
        var worlds = new Dictionary<string, string?>(StringComparer.Ordinal) { [World] = "The Black Cat" };

        var card = Card(
            Fact(FactType.PlannedEventChanged, new JsonObject
            {
                ["title"] = "Movie Night",
                ["before"] = Fields("Film Night", Eight, "Films."),
                ["after"] = Fields("Movie Night", Eight, "Films and snacks.", World),
            }),
            worlds);

        var changed = Field(card, "Changed")!;
        Assert.Contains("**Title**: “Film Night” → “Movie Night”", changed, StringComparison.Ordinal);
        Assert.Contains("**Description**: “Films.” → “Films and snacks.”", changed, StringComparison.Ordinal);
        Assert.Contains("**World**: nothing → [The Black Cat](", changed, StringComparison.Ordinal);
        Assert.DoesNotContain(World, Visible(card), StringComparison.Ordinal);
    }

    [Fact]
    public void AChange_WithNothingAModeratorCanSee_SaysSo()
    {
        var card = Card(Fact(FactType.PlannedEventChanged, new JsonObject
        {
            ["title"] = "Movie Night",
            ["before"] = Fields("Movie Night", Eight),
            ["after"] = Fields("Movie Night", Eight),
        }));

        Assert.Equal("No visible change", Field(card, "Changed"));
    }

    [Fact]
    public void AChange_ToManyFields_ListsTheLimit_AndCountsTheRest()
    {
        var before = new JsonObject();
        var after = new JsonObject();

        for (var i = 0; i < 12; i++)
        {
            before[$"extra{i}"] = "a";
            after[$"extra{i}"] = "b";
        }

        var card = Card(Fact(FactType.PlannedEventChanged, new JsonObject
        {
            ["title"] = "Movie Night",
            ["before"] = before,
            ["after"] = after,
        }));

        Assert.EndsWith("\nand 4 more", Field(card, "Changed"), StringComparison.Ordinal);
    }

    [Fact]
    public void ACreatedEvent_IsNamed_WithWhenItStarts()
    {
        var card = Card(Fact(FactType.PlannedEventCreated, Fields("Movie Night", Eight)));

        Assert.Equal("Movie Night", card.Title);
        Assert.Equal("Event planned", card.AuthorName);
        Assert.Equal(DiscordTime.Absolute(Eight), Field(card, "Starts"));
    }

    [Theory]
    [InlineData(FactType.PlannedEventCancelled, "Planned event cancelled")]
    [InlineData(FactType.PlannedEventDeleted, "Planned event deleted")]
    public void ACancelledOrDeletedEvent_IsNamed(string type, string label)
    {
        var card = Card(Fact(type, new JsonObject { ["title"] = "Movie Night" }));

        Assert.Equal("Movie Night", card.Title);
        Assert.Equal(label, card.AuthorName);
    }

    [Fact]
    public void ADeletedEvent_LinksNowhere_BecauseThereIsNothingToOpen()
        => Assert.Null(Card(Fact(FactType.PlannedEventDeleted, new JsonObject { ["title"] = "Movie Night" })).Url);

    public static TheoryData<string> CalendarTypes => new()
    {
        FactType.PlannedEventCreated,
        FactType.PlannedEventChanged,
        FactType.PlannedEventCancelled,
        FactType.PlannedEventDeleted,
        FactType.PlannedDateCancelled,
        FactType.PlannedDateChanged,
        FactType.PlannedEventOpened,
        FactType.PlannedEventFinished,
        FactType.PlannedEventInstanceOpened,
        FactType.PlannedEventInstanceFailed,
        FactType.PlannedEventPublishFailed,
        FactType.PlannedEventPublished,
        FactType.PlannedEventTakenDown,
        FactType.PlannedEventPictureUploaded,
        FactType.CalendarWorldPicked,
    };

    /// <summary>The bug as reported: the event's id as the line a moderator reads, and no name.</summary>
    [Theory]
    [MemberData(nameof(CalendarTypes))]
    public void NoCalendarCard_ShowsTheEventsId(string type)
    {
        var named = Card(Fact(type, new JsonObject { ["title"] = "Movie Night" }));
        var untitled = Card(Fact(type, new JsonObject()));

        Assert.Equal("Movie Night", named.Title);
        Assert.Equal("A calendar event", untitled.Title);
        Assert.DoesNotContain(EventId, Visible(named), StringComparison.Ordinal);
        Assert.DoesNotContain(EventId, Visible(untitled), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedPublish_NamesTheEvent_WhereItFailed_AndWhy()
    {
        const string Refused = "The bot may not manage server events; it needs Manage Events.";

        var card = Card(Fact(
            FactType.PlannedEventPublishFailed,
            new JsonObject { ["title"] = "Movie Night", ["place"] = CalendarPlaces.DiscordEvent, ["error"] = Refused },
            actor: null));

        Assert.Equal("Movie Night", card.Title);
        Assert.Equal("Event failed to publish", card.AuthorName);
        Assert.Equal("Discord event", Field(card, "Where"));
        Assert.Equal(Refused, Field(card, "Why"));
    }

    [Fact]
    public void AFailedPublishToVRChat_SaysWhichPermissionTheAccountLacks()
    {
        var card = Card(Fact(
            FactType.PlannedEventPublishFailed,
            new JsonObject
            {
                ["title"] = "Movie Night",
                ["place"] = CalendarPlaces.VRChat,
                ["action"] = "create",
                ["error"] = "You do not have permission to do that.",
                ["fix"] = "Modbot's VRChat account needs Manage Group Calendar in this group.",
            },
            actor: null));

        Assert.Equal("VRChat calendar", Field(card, "Where"));
        Assert.Equal(
            "You do not have permission to do that.\nModbot's VRChat account needs Manage Group Calendar in this group.",
            Field(card, "Why"));
    }

    [Fact]
    public void AFailedPublishFromBeforeThePlaceWasKept_IsStillNamed()
    {
        var card = Card(Fact(FactType.PlannedEventPublishFailed, new JsonObject { ["title"] = "Movie Night" }, actor: null));

        Assert.Equal("Movie Night", card.Title);
        Assert.Null(Field(card, "Where"));
        Assert.Null(Field(card, "Why"));
    }

    [Fact]
    public void AnInstanceThatFailedToOpen_SaysWhereAndWhy()
    {
        var worlds = new Dictionary<string, string?>(StringComparer.Ordinal) { [World] = "The Black Cat" };

        var card = Card(
            Fact(
                FactType.PlannedEventInstanceFailed,
                new JsonObject { ["title"] = "Movie Night", ["error"] = "VRChat answered 403.", ["status"] = 403 },
                actor: null,
                worldId: World),
            worlds);

        Assert.StartsWith("[The Black Cat](", Field(card, "Where"), StringComparison.Ordinal);
        Assert.Equal("VRChat answered 403.", Field(card, "Why"));
    }

    [Fact]
    public void AnInviteThatFailed_IsAboutThePerson_AndNamesTheEvent()
    {
        var card = Card(Fact(
            FactType.PlannedEventInviteFailed,
            new JsonObject { ["eventId"] = EventId, ["title"] = "Movie Night", ["problem"] = "They are not friends with the bot." },
            platform: FactPlatform.Discord,
            subject: Member,
            actor: null));

        Assert.Equal("Invite to Movie Night failed", card.Title);
        Assert.Equal($"<@{Member}>", Field(card, "Who"));
        Assert.Equal("They are not friends with the bot.", Field(card, "Why"));
    }

    // ── Discord ──────────────────────────────────────────────────────────────────────────────

    private static ModbotEvent Voice(string type, JsonObject data, FactPlatform? actorPlatform = null, string? actor = null)
        => Fact(type, data, FactPlatform.Discord, Member, actor, actorPlatform);

    [Fact]
    public void AVoiceJoin_ByAnUnknownName_SaysWhoAndWhichChannel()
    {
        var card = Card(Voice(FactType.DiscordVoiceJoined, new JsonObject { ["channelId"] = VoiceChannel }));

        Assert.Equal("Joined a voice channel", card.Title);
        Assert.Null(card.AuthorName);
        Assert.Equal($"<@{Member}>", Field(card, "Who"));
        Assert.Equal($"<#{VoiceChannel}>", Field(card, "Channel"));
        Assert.Equal("Who", card.Fields[0].Name);
    }

    [Fact]
    public void AVoiceMove_SaysFromWhereToWhere()
    {
        var card = Card(Voice(
            FactType.DiscordVoiceMoved,
            new JsonObject { ["channelId"] = OtherChannel, ["from"] = VoiceChannel }));

        Assert.Equal($"<#{VoiceChannel}> → <#{OtherChannel}>", Field(card, "Channel"));
    }

    [Fact]
    public void AKnownName_HeadsTheCard_WithNoWhoField()
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal) { [Member] = "jessie" };

        var card = Card(Voice(FactType.DiscordVoiceLeft, new JsonObject { ["channelId"] = VoiceChannel }), discordNames: names);

        Assert.Equal("jessie", card.AuthorName);
        Assert.Null(Field(card, "Who"));
        Assert.Equal($"{Address}/discord/members?subject=discord-person%3A{Member}", card.AuthorUrl);
    }

    [Fact]
    public void TheNameTheFactKept_IsUsed_WhenTheMemberListHasNone()
    {
        var card = Card(Fact(
            FactType.DiscordMemberLeft, new JsonObject { ["displayName"] = "jessie" }, FactPlatform.Discord, Member, actor: null));

        Assert.Equal("jessie", card.AuthorName);
    }

    public static TheoryData<string> DiscordPersonTypes => new()
    {
        FactType.DiscordMemberJoined,
        FactType.DiscordMemberLeft,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberUnbanned,
        FactType.DiscordMemberKicked,
        FactType.DiscordMemberTimedOut,
        FactType.DiscordMemberTimeoutRemoved,
        FactType.DiscordMemberNicknameChanged,
        FactType.DiscordVoiceJoined,
        FactType.DiscordVoiceLeft,
        FactType.DiscordVoiceMoved,
        FactType.DiscordRoleGranted,
        FactType.DiscordRoleRevoked,
        FactType.DiscordMessagesRemoved,
        FactType.DiscordCommandRun,
    };

    /// <summary>An unknown name falls back to Discord's mention, never to a bare id.</summary>
    [Theory]
    [MemberData(nameof(DiscordPersonTypes))]
    public void AnUnknownDiscordName_IsAMention_NeverABareId(string type)
    {
        var card = Card(Fact(type, new JsonObject(), FactPlatform.Discord, Member, actor: null));

        Assert.Equal($"<@{Member}>", Field(card, "Who"));
        Assert.DoesNotContain(Member, Visible(card).Replace($"<@{Member}>", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void ADiscordModerator_WhoseNameIsUnknown_IsAMentionInBy()
    {
        const string Mod = "300000000000000003";

        var card = Card(Fact(
            FactType.DiscordMemberBanned, new JsonObject(), FactPlatform.Discord, Member, Mod, FactPlatform.Discord));

        Assert.Equal($"<@{Mod}>", Field(card, "By"));
    }

    [Fact]
    public void ADiscordRole_IsNamedInTheTitle_OrMentioned()
    {
        var named = Card(Fact(
            FactType.DiscordRoleGranted,
            new JsonObject { ["roleId"] = "77", ["roleName"] = "Moderators" },
            FactPlatform.Discord, Member, actor: null));
        var unnamed = Card(Fact(
            FactType.DiscordRoleRevoked, new JsonObject { ["roleId"] = "77" }, FactPlatform.Discord, Member, actor: null));

        Assert.Equal("Given the Moderators role on Discord", named.Title);
        Assert.Equal("Discord role revoked", unnamed.Title);
        Assert.Equal("<@&77>", Field(unnamed, "Role"));
    }

    [Fact]
    public void ANicknameChange_SaysFromWhatToWhat()
    {
        var card = Card(Fact(
            FactType.DiscordMemberNicknameChanged,
            new JsonObject { ["old"] = "jess", ["new"] = "jessie", ["displayName"] = "jessie" },
            FactPlatform.Discord, Member, actor: null));

        Assert.Equal("“jess” → “jessie”", Field(card, "Nickname"));
    }

    [Fact]
    public void AChannelMade_IsTitledByItsName()
    {
        var card = Card(Fact(
            FactType.DiscordChannelCreated, new JsonObject { ["name"] = "general" }, FactPlatform.Discord, VoiceChannel, actor: null));

        Assert.Equal("#general", card.Title);
        Assert.Equal("Discord channel created", card.AuthorName);
        Assert.DoesNotContain(VoiceChannel, Visible(card), StringComparison.Ordinal);
    }

    // ── Things Modbot keeps ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AListMade_IsTitledByItsName()
    {
        const string List = "5d0b6c1e-7a1f-4b44-b0a8-2f2b8f2c0a11";

        var card = Card(Fact(FactType.ListCreated, new JsonObject { ["name"] = "Regulars" }, subject: List));

        Assert.Equal("Regulars", card.Title);
        Assert.Equal("List made", card.AuthorName);
        Assert.DoesNotContain(List, Visible(card), StringComparison.Ordinal);
    }

    [Fact]
    public void AThingWithNoName_IsHeadedByTheGroup_NotByItsId()
    {
        var card = Card(Fact(FactType.CalendarFeedRegenerated, new JsonObject(), subject: "calendar-feed"));

        Assert.Equal("Calendar feed link replaced", card.Title);
        Assert.Equal("The Kingdom", card.AuthorName);
        Assert.DoesNotContain("calendar-feed", Visible(card), StringComparison.Ordinal);
    }
}
