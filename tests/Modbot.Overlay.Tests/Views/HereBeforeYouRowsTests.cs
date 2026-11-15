using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// Somebody who was already here when the moderator arrived: their row says how long the moderator has
/// been here, which is the least long they have been, as a short time and not in words; their card and
/// the Audit Log's row say the words. On every panel and in both looks, kept up to date a minute at a time.
/// </summary>
public class HereBeforeYouRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);

    private static RosterMember Person(string name) => new("usr_" + name, name, RosterStanding.Ordinary, 0, []);

    private static readonly RosterMember Kai = Person("Kai");
    private static readonly RosterMember Jo = Person("Jo");

    private static LiveEvent Here(string name)
        => new(
            "1",
            "1",
            LiveEventKinds.PersonHere,
            Now.AddMinutes(-64),
            "39911",
            new LivePerson("usr_" + name, name, null, RosterStanding.Ordinary, 0, []),
            false,
            null,
            false);

    private static OverlayScreen Instance(DateTimeOffset? moderatorArrived, bool notSynced = false)
        => new(
            notSynced ? null : "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", [Kai, Jo]), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Arrivals: new Dictionary<string, DateTimeOffset?>
            {
                // Kai was here before the moderator, and Jo came in after.
                ["usr_Kai"] = null,
                ["usr_Jo"] = Now.AddMinutes(-3),
            },
            Now: Now,
            NotSynced: notSynced,
            ModeratorArrived: moderatorArrived);

    private static OverlayScreen Log(OverlayPage page, DateTimeOffset? moderatorArrived, ListFilters? filters = null)
        => Instance(moderatorArrived) with
        {
            Page = page,
            Events = [Here("Kai")],
            EventFilters = filters,
        };

    private static OverlayLook VRChatLook()
    {
        static PaletteColour c(byte r, byte g, byte b) => new(r, g, b);

        return OverlayLook.FromColours(new OverlayColours(
            Panel: c(0x18, 0x12, 0x24),
            Bar: c(0x10, 0x0C, 0x1A),
            Button: c(0x2A, 0x20, 0x44),
            Border: c(0x38, 0x2C, 0x5A),
            Hover: c(0x30, 0x26, 0x4E),
            Selected: c(0x50, 0x3A, 0x90),
            SelectedBorder: c(0x80, 0x60, 0xE0),
            SelectedText: c(0xF5, 0xF5, 0xF5),
            Icon: c(0xA0, 0x90, 0xD0),
            Text: c(0xF0, 0xF0, 0xF8),
            Subtext: c(0xB0, 0xA8, 0xC8)));
    }

    private static IReadOnlyList<string?> Texts(OverlayScreen screen, OverlayLook? look = null)
        => AvaloniaTestHost.Run<IReadOnlyList<string?>>(() =>
            [.. OverlayView.Build(screen, null, look ?? OverlayLook.Headset).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARosterRowSaysHowLongTheModeratorHasBeenHere(bool inVRChatLook)
    {
        var texts = Texts(Instance(Now.AddMinutes(-5)), inVRChatLook ? VRChatLook() : null);

        Assert.Contains("~5m+", texts);
        Assert.Contains("3m", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.Contains("here before you", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("already here", texts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnHourOrMoreReadsInHoursAndMinutes(bool inVRChatLook)
    {
        var texts = Texts(Instance(Now.AddMinutes(-64)), inVRChatLook ? VRChatLook() : null);

        Assert.Contains("~1h 4m+", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.Contains("here before you", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoRecordOfTheModeratorsArrivalTheRowSaysNothingAtAll(bool notSynced)
    {
        var texts = Texts(Instance(null, notSynced));

        Assert.DoesNotContain(texts, t => t is not null && t.Contains("here before you", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(texts, t => t is not null && t.StartsWith('~'));
        Assert.Contains("3m", texts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePersonsCardSaysTheyWereHereBeforeYou(bool inVRChatLook)
    {
        var kai = new UserSummary("usr_Kai", "Kai", RosterStanding.Ordinary, 0, null, [], []);
        var jo = new UserSummary("usr_Jo", "Jo", RosterStanding.Ordinary, 0, null, [], []);

        var before = Texts(Instance(Now.AddMinutes(-5)) with { Page = OverlayPage.Person, Person = kai }, inVRChatLook ? VRChatLook() : null);
        var after = Texts(Instance(Now.AddMinutes(-5)) with { Page = OverlayPage.Person, Person = jo }, inVRChatLook ? VRChatLook() : null);

        Assert.Contains("Here before you", before);
        Assert.DoesNotContain("Here before you", after);
    }

    [Fact]
    public void TheAuditLogRowSaysItToo()
    {
        var texts = Texts(Log(OverlayPage.Events, Now.AddMinutes(-64)));

        Assert.Contains("(here before you) ~1hr 4m+", texts);
        Assert.Contains("Kai", texts);
        Assert.DoesNotContain("Already here", texts);
    }

    [Fact]
    public void TheKindFilterOffersTheNewWordsToo()
    {
        var texts = Texts(Log(OverlayPage.Events, Now.AddMinutes(-5), new ListFilters(Open: FilterPart.Kind)));

        Assert.Contains("(here before you)", texts);
        Assert.DoesNotContain("Already here", texts);
    }

    [Fact]
    public void TheWristPanelSaysItForTheNewestEvent()
    {
        var texts = Texts(Log(OverlayPage.Wrist, Now.AddMinutes(-5)));

        Assert.Contains("(here before you) ~5m+", texts);
        Assert.DoesNotContain("Already here", texts);
    }

    [Fact]
    public void TheListsAreDrawnAgainOnTheMinuteAndNotBetween()
    {
        foreach (var page in new[] { OverlayPage.Instance, OverlayPage.Events, OverlayPage.Wrist })
        {
            var screen = Log(page, Now.AddMinutes(-5));

            Assert.True(screen.LooksTheSameAs(screen with { Now = Now.AddSeconds(30) }), page.ToString());
            Assert.False(screen.LooksTheSameAs(screen with { Now = Now.AddMinutes(1) }), page.ToString());
        }
    }

    [Fact]
    public void ALateArrivalOfTheModeratorsOwnTimeIsDrawn()
    {
        var unknown = Instance(null);

        Assert.False(unknown.LooksTheSameAs(Instance(Now.AddMinutes(-5))));
        Assert.True(unknown.LooksTheSameAs(Instance(null)));
    }
}
