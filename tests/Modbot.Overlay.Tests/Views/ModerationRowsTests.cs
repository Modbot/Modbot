using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// A moderation row on the Audit Log: the server's sentence marked with where it comes from, among
/// the join and leave rows by time, in both looks, and cut by the kind filter and the name search.
/// </summary>
public class ModerationRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 18, 30, 0, TimeSpan.Zero);

    private static LiveEvent Presence(string id, string kind, string name, int minutesAgo)
        => new(
            id,
            id,
            kind,
            Now.AddMinutes(-minutesAgo),
            "39911",
            new LivePerson("usr_" + name, name, "Trusted User", RosterStanding.Ordinary, 0, []),
            false,
            null,
            false);

    private static LiveEvent Moderation(string id, string sentence, string origin, int minutesAgo, string kind = "fact", string? instance = null)
        => new(
            id,
            id,
            kind,
            Now.AddMinutes(-minutesAgo),
            instance,
            null,
            false,
            null,
            false,
            Sentence: sentence,
            Origin: origin);

    private static IReadOnlyList<string?> Texts(
        IReadOnlyList<LiveEvent> events,
        OverlayLook look,
        OverlayPage page = OverlayPage.Events,
        ListFilters? filters = null)
    {
        var screen = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", []), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: page,
            Events: events,
            EventFilters: filters,
            Now: Now);

        return AvaloniaTestHost.Run<IReadOnlyList<string?>>(() =>
            [.. OverlayView.Build(screen, null, look).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)]);
    }

    [Theory]
    [InlineData(false, "VRChat")]
    [InlineData(false, "Discord")]
    [InlineData(false, "Modbot")]
    [InlineData(true, "VRChat")]
    [InlineData(true, "Discord")]
    [InlineData(true, "Modbot")]
    public void AModerationRowShowsTheServersSentenceAndWhereItComesFrom(bool inVRChatLook, string origin)
    {
        var look = inVRChatLook ? AuditLogRowsTests.VRChatLook() : OverlayLook.Headset;
        const string sentence = "Mira kicked Ada out of The Black Cat #39047.";

        var texts = Texts([Moderation("1", sentence, origin, 3)], look);

        Assert.Contains(sentence, texts);
        Assert.Contains(origin, texts);
        Assert.Contains(Now.AddMinutes(-3).ToLocalTime().ToString("HH:mm"), texts);
    }

    [Fact]
    public void TheWristShowsTheSentenceAndWhereItComesFromWhenAModerationRowIsNewest()
    {
        const string sentence = "Mira kicked Ada out of The Black Cat #39047.";

        var texts = Texts([Moderation("1", sentence, "Discord", 1), Presence("0", LiveEventKinds.PersonJoined, "Kai", 5)], OverlayLook.Headset, OverlayPage.Wrist);

        Assert.Contains(sentence, texts);
        Assert.Contains("Discord", texts);
        Assert.DoesNotContain("fact", texts);
        Assert.DoesNotContain("Kai", texts);
    }

    [Fact]
    public void TheWristStillShowsTheNameAndKindWhenAPresenceRowIsNewest()
    {
        var texts = Texts([Presence("1", LiveEventKinds.PersonJoined, "Kai", 1), Moderation("0", "Kai joined the group.", "VRChat", 5)], OverlayLook.Headset, OverlayPage.Wrist);

        Assert.Contains("Kai", texts);
        Assert.Contains("Joined", texts);
        Assert.DoesNotContain("Kai joined the group.", texts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheKindFilterCallsTheServersRowsActions(bool inVRChatLook)
    {
        var look = inVRChatLook ? AuditLogRowsTests.VRChatLook() : OverlayLook.Headset;

        var texts = Texts([], look, filters: new ListFilters(Open: FilterPart.Kind));

        Assert.Contains("Actions", texts);
        Assert.DoesNotContain("Moderation", texts);
    }

    [Fact]
    public void AModerationCardInVRChatsLookFitsItsWordsInsideItsEdge()
    {
        // A sentence long enough for every line it is allowed, in a panel as wide as the headset's.
        var sentence = string.Join(' ', Enumerable.Repeat("Mira kicked Ada out of The Black Cat #39047.", 6));

        var screen = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", []), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: OverlayPage.Events,
            Events: [Moderation("1", sentence, "Discord", 3)],
            Now: Now);

        var (needed, room) = AvaloniaTestHost.Run(() =>
        {
            var root = OverlayView.Build(screen, null, AuditLogRowsTests.VRChatLook());
            root.Measure(new Avalonia.Size(640, 900));
            root.Arrange(new Avalonia.Rect(0, 0, 640, 900));

            var card = root.GetLogicalDescendants().OfType<Border>().First(b => b.Height == 68);
            var words = card.GetLogicalDescendants().OfType<StackPanel>().First(s => s.Children.OfType<TextBlock>().Any(t => t.Text == sentence));
            var column = words.Bounds.Width;

            words.Measure(new Avalonia.Size(column, double.PositiveInfinity));
            return (words.DesiredSize.Height, card.Height - 2 * card.BorderThickness.Top);
        });

        Assert.True(needed <= room, $"the words need {needed}px and the card has {room}px inside its edge");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModerationRowsSitAmongThePresenceRowsInTheOrderTheyCame(bool inVRChatLook)
    {
        var look = inVRChatLook ? AuditLogRowsTests.VRChatLook() : OverlayLook.Headset;

        // Newest first, as the drive loop keeps them.
        var texts = Texts(
            [
                Presence("3", LiveEventKinds.PersonLeft, "Kai", 1),
                Moderation("2", "Kai joined the group.", "VRChat", 2),
                Presence("1", LiveEventKinds.PersonJoined, "Kai", 5),
            ],
            look);

        var left = texts.ToList().IndexOf("Left");
        var group = texts.ToList().IndexOf("Kai joined the group.");
        var joined = texts.ToList().IndexOf("Joined");

        Assert.True(left >= 0 && group > left && joined > group, string.Join(" | ", texts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEventWithNoSentenceIsStillAPresenceRow(bool inVRChatLook)
    {
        var look = inVRChatLook ? AuditLogRowsTests.VRChatLook() : OverlayLook.Headset;

        var texts = Texts([Presence("1", LiveEventKinds.PersonJoined, "Kai", 5)], look);

        Assert.Contains("Joined", texts);
        Assert.Contains("Kai", texts);
    }

    [Fact]
    public void AnEventIsAModerationRowOnlyWhenTheServerSentASentence()
    {
        Assert.False(Presence("1", LiveEventKinds.PersonJoined, "Kai", 1).IsModeration);
        Assert.False((Presence("1", LiveEventKinds.PersonJoined, "Kai", 1) with { Sentence = "" }).IsModeration);
        Assert.True(Moderation("2", "Kai joined the group.", "VRChat", 1).IsModeration);
    }

    private static readonly IReadOnlyList<LiveEvent> Log =
    [
        Moderation("4", "Test moderator banned Kai from the group.", "Modbot", 1),
        Presence("3", LiveEventKinds.PersonLeft, "Ash", 2),
        Moderation("2", "Kai joined the Discord server.", "Discord", 4, kind: "instance_changed"),
        Presence("1", LiveEventKinds.PersonJoined, "Kai", 6),
    ];

    private static List<string> Ids(ListFilters filters)
        => [.. ListFiltering.Events(Log, filters, Now).Select(e => e.Id)];

    [Fact]
    public void WithNoKindPickedEveryRowIsShown()
        => Assert.Equal(["4", "3", "2", "1"], Ids(ListFilters.None));

    [Fact]
    public void TheModerationKindKeepsModerationRowsWhateverKindTheServerGaveThem()
        => Assert.Equal(["4", "2"], Ids(new ListFilters(Kinds: new KindPick().Toggle(LiveEventKinds.Moderation))));

    [Fact]
    public void AnotherKindLeavesModerationRowsOut()
        => Assert.Equal(["1"], Ids(new ListFilters(Kinds: new KindPick().Toggle(LiveEventKinds.PersonJoined))));

    [Fact]
    public void ModerationIsOfferedAsAKind()
        => Assert.Contains(LiveEventKinds.Moderation, KindPick.Offered);

    [Fact]
    public void TheNameSearchFindsANameInsideASentenceAndOnAPresenceRow()
    {
        Assert.Equal(["4", "2", "1"], Ids(new ListFilters(Name: "kai")));
        Assert.Equal(["3"], Ids(new ListFilters(Name: "ash")));
    }

    [Fact]
    public void AModerationRowHasNoStandingOrRankSoThoseFiltersLeaveItOut()
    {
        Assert.DoesNotContain("4", Ids(new ListFilters(Who: Who.NotInGroup)));
        Assert.DoesNotContain("2", Ids(new ListFilters(Ranks: new RankPick().Toggle(null))));
    }

    [Fact]
    public void TheTimeFilterMeasuresAModerationRowLikeAnyOther()
        => Assert.Equal(["4", "3", "2"], Ids(new ListFilters(Time: TimeWindow.FiveMinutes)));
}
