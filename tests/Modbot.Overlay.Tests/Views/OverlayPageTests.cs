using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The main panel's three screens: the tabs that move between them, the events list, and the
/// person card with the two things this client can honestly do about a person.
/// </summary>
public class OverlayPageTests
{
    private const int Size = 512;

    private static InstanceContext Roster() => new(
        "39911",
        [
            new RosterMember("usr_rin", "Rin", RosterStanding.Flagged, 2, ["kicked before"]),
            new RosterMember("usr_kai", "Kai", RosterStanding.Member, 0, []),
        ]);

    private static LiveEvent Event(string id, string kind) => new(
        id,
        id,
        kind,
        DateTimeOffset.UnixEpoch,
        "39911",
        new LivePerson("usr_rin", "Rin", null, RosterStanding.Flagged, 2, []),
        kind == LiveEventKinds.FlaggedJoin,
        "kicked before",
        false);

    private static OverlayScreen Screen(OverlayPage page, UserSummary? person = null, params LiveEvent[] events) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
        Freshness.Fresh,
        Person: person,
        Page: page,
        Events: events);

    private static IReadOnlyList<PlacedTarget> Targets(OverlayScreen screen) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(Size, Size);
        var root = OverlayView.Build(screen);
        renderer.Render(root);
        return OverlayTargets.Find(root);
    });

    private static byte[] Render(OverlayScreen screen) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(Size, Size);
        return renderer.Render(OverlayView.Build(screen)).ToArray();
    });

    [Fact]
    public void EveryScreenCarriesTheTabsThatLeaveIt()
    {
        // A panel a moderator can get into and not out of with a controller is worse than no
        // panel, so the tabs are on every screen.
        foreach (var page in Enum.GetValues<OverlayPage>())
        {
            var person = new UserSummary("usr_rin", "Rin", RosterStanding.Flagged, 2, null, [], []);
            var pages = Targets(Screen(page, person))
                .Select(t => t.Target)
                .OfType<OverlayTarget.GoTo>()
                .Select(t => t.Page)
                .ToList();

            Assert.Contains(OverlayPage.Instance, pages);
            Assert.Contains(OverlayPage.Events, pages);
        }
    }

    [Fact]
    public void ATapOnTheEventsTabIsTheEventsTabAndNotWhateverIsUnderIt()
    {
        // The same lookup the mouse over VRChat and a controller's ray both use, at the middle of
        // the drawn tab. The tab is the only way to the Events screen now that the feed under the
        // window over VRChat is gone.
        var target = AvaloniaTestHost.Run(() =>
        {
            using var renderer = new AvaloniaFrameRenderer(Size, Size);
            var root = OverlayView.Build(Screen(OverlayPage.Instance));
            renderer.Render(root);

            var tab = OverlayTargets
                .Find(root)
                .First(t => t.Target is OverlayTarget.GoTo { Page: OverlayPage.Events });

            return OverlayTargets.At(root, tab.Bounds.Center);
        });

        Assert.Equal(new OverlayTarget.GoTo(OverlayPage.Events), target);
    }

    [Fact]
    public void ThereIsNoPersonTabUntilSomebodyIsOpen()
    {
        // A tab that opens a blank card is a dead control.
        var pages = Targets(Screen(OverlayPage.Instance))
            .Select(t => t.Target)
            .OfType<OverlayTarget.GoTo>()
            .Select(t => t.Page)
            .ToList();

        Assert.DoesNotContain(OverlayPage.Person, pages);
    }

    [Fact]
    public void TheEventsScreenListsWhatTheLiveLinkHeard()
    {
        var screen = Screen(
            OverlayPage.Events,
            null,
            Event("e2", LiveEventKinds.FlaggedJoin),
            Event("e1", LiveEventKinds.PersonLeft));

        var targets = Targets(screen).Select(t => t.Target).ToList();

        Assert.Contains(targets, t => t is OverlayTarget.Events);
        Assert.Contains(targets, t => t is OverlayTarget.Person { SubjectId: "usr_rin" });
        Assert.Contains(Render(screen), b => b != 0);
    }

    [Fact]
    public void AnEmptyEventsScreenStillDraws()
    {
        var pixels = Render(Screen(OverlayPage.Events));

        Assert.Equal(Size * Size * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void ThePersonScreenOffersBackAndRefreshAndNothingThatActs()
    {
        // The client's device token is ingest-scoped: it could not carry a ban, a kick or a warn
        // even if a control here tried. This pins the absence, because "add a ban button" is
        // exactly the well-meant change that would break the pairing model.
        var person = new UserSummary("usr_rin", "Rin", RosterStanding.Flagged, 2, DateTimeOffset.UnixEpoch, ["kicked before"], ["Member"]);

        var targets = Targets(Screen(OverlayPage.Person, person)).Select(t => t.Target).ToList();

        Assert.Contains(targets, t => t is OverlayTarget.ClosePerson);
        Assert.Contains(targets, t => t is OverlayTarget.RefreshPerson);

        // The whole list of target kinds the panel has. Anything new has to be added here on
        // purpose, which is the point.
        Assert.All(targets, t => Assert.True(
            t is OverlayTarget.GoTo or OverlayTarget.Person or OverlayTarget.ClosePerson
                or OverlayTarget.RefreshPerson or OverlayTarget.Roster or OverlayTarget.Events
                or OverlayTarget.DismissAlert,
            $"Unexpected overlay target {t.GetType().Name}."));
    }

    [Fact]
    public void AskingForThePersonScreenWithNobodyOpenShowsTheRoster()
    {
        var targets = Targets(Screen(OverlayPage.Person)).Select(t => t.Target).ToList();

        Assert.Contains(targets, t => t is OverlayTarget.Roster);
    }

    [Fact]
    public void ChangingScreenIsAChangeWorthRedrawing()
    {
        var instance = Screen(OverlayPage.Instance);
        var events = Screen(OverlayPage.Events);

        Assert.False(instance.LooksTheSameAs(events));
        Assert.True(instance.LooksTheSameAs(Screen(OverlayPage.Instance)));
    }

    [Fact]
    public void ANewEventIsAChangeWorthRedrawing()
    {
        var one = Screen(OverlayPage.Events, null, Event("e1", LiveEventKinds.PersonJoined));
        var two = Screen(OverlayPage.Events, null, Event("e2", LiveEventKinds.PersonJoined), Event("e1", LiveEventKinds.PersonJoined));

        Assert.False(one.LooksTheSameAs(two));
    }
}
