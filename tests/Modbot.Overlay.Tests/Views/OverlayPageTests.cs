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
        // panel, so the tabs are on every screen. The wrist is not one of the three: it is not a
        // tab and cannot be reached from one, and the way off it is to take the panel off the
        // wrist.
        foreach (var page in Enum.GetValues<OverlayPage>().Where(p => p is not OverlayPage.Wrist))
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
        Assert.All(targets, t => Assert.True(Allowed(t), $"Unexpected overlay target {t.GetType().Name}."));
    }

    /// <summary>
    /// The whole list of target kinds the panel has. Anything new has to be added here on purpose,
    /// which is the point. The filter row's four only choose what a list shows.
    /// </summary>
    private static bool Allowed(OverlayTarget target) =>
        target is OverlayTarget.GoTo or OverlayTarget.Person or OverlayTarget.ClosePerson
            or OverlayTarget.RefreshPerson or OverlayTarget.Roster or OverlayTarget.Events
            or OverlayTarget.DismissAlert
            or OverlayTarget.Filter or OverlayTarget.Pick or OverlayTarget.ClearFilters or OverlayTarget.TypeName;

    [Fact]
    public void EveryFilterStateStillOffersNothingThatActsOnAPerson()
    {
        var events = new[] { Event("e1", LiveEventKinds.PersonJoined) };

        foreach (var part in Enum.GetValues<FilterPart>())
        {
            var filters = new ListFilters(Who: Who.Flagged, Name: "ri", Open: part);
            var screens = new[]
            {
                Screen(OverlayPage.Instance) with { RosterFilters = filters },
                Screen(OverlayPage.Events, null, events) with { EventFilters = filters },
            };

            foreach (var screen in screens)
                Assert.All(Targets(screen).Select(t => t.Target), t => Assert.True(Allowed(t), $"Unexpected overlay target {t.GetType().Name}."));
        }
    }

    private static IReadOnlyList<FilterPart> Parts(OverlayScreen screen) =>
        [.. Targets(screen).Select(t => t.Target).OfType<OverlayTarget.Filter>().Select(f => f.Part)];

    [Fact]
    public void TheInstanceListAndTheAuditLogEachHaveTheirOwnFilters()
    {
        Assert.Equal(
            [FilterPart.Who, FilterPart.Rank, FilterPart.Time, FilterPart.Name, FilterPart.Sort],
            Parts(Screen(OverlayPage.Instance)));

        Assert.Equal(
            [FilterPart.Who, FilterPart.Rank, FilterPart.Kind, FilterPart.Time, FilterPart.Name],
            Parts(Screen(OverlayPage.Events, null, Event("e1", LiveEventKinds.PersonJoined))));
    }

    [Fact]
    public void AFilterRowTapIsForTheListItIsOver()
    {
        var lists = Targets(Screen(OverlayPage.Events, null, Event("e1", LiveEventKinds.PersonJoined)))
            .Select(t => t.Target)
            .OfType<OverlayTarget.Filter>()
            .Select(f => f.List)
            .Distinct();

        Assert.Equal([OverlayPage.Events], lists);
    }

    [Fact]
    public void AnEmptyAuditLogHasNoFiltersToShow()
    {
        Assert.Empty(Parts(Screen(OverlayPage.Events)));
    }

    [Fact]
    public void APickedFilterStaysInSightOverAListItEmptied()
    {
        // Otherwise a list the filters emptied would have no way back but to leave the instance.
        var screen = Screen(OverlayPage.Instance) with { RosterFilters = new ListFilters(Name: "nobody by this name") };

        Assert.NotEmpty(Parts(screen));
        Assert.Contains(Targets(screen), t => t.Target is OverlayTarget.ClearFilters);
        Assert.DoesNotContain(Targets(screen), t => t.Target is OverlayTarget.Person);
    }

    [Fact]
    public void ClearIsOfferedOnlyWhileSomethingIsPicked()
    {
        Assert.DoesNotContain(Targets(Screen(OverlayPage.Instance)), t => t.Target is OverlayTarget.ClearFilters);
        Assert.Contains(
            Targets(Screen(OverlayPage.Instance) with { RosterFilters = new ListFilters(Order: RosterOrder.Name) }),
            t => t.Target is OverlayTarget.ClearFilters);
    }

    [Fact]
    public void AnOpenRankFilterOffersEveryRankAndNotKnown()
    {
        var screen = Screen(OverlayPage.Instance) with { RosterFilters = new ListFilters(Open: FilterPart.Rank) };

        var choices = Targets(screen)
            .Select(t => t.Target)
            .OfType<OverlayTarget.Pick>()
            .Where(p => p.Part is FilterPart.Rank)
            .Select(p => p.Choice)
            .ToList();

        Assert.Equal(RankPick.Offered.Select(r => r is { } rank ? (int)rank : -1), choices);
    }

    [Fact]
    public void AnOpenNameFilterIsABoxHoldingTheName()
    {
        var screen = Screen(OverlayPage.Instance) with { RosterFilters = new ListFilters(Name: "ri", Open: FilterPart.Name) };

        var box = Assert.Single(Targets(screen).Select(t => t.Target).OfType<OverlayTarget.TypeName>());
        Assert.Equal(new OverlayTarget.TypeName(OverlayPage.Instance, "ri"), box);
    }

    [Fact]
    public void AFilteredRosterDrawsOnlyWhoItKept()
    {
        var screen = Screen(OverlayPage.Instance) with { RosterFilters = new ListFilters(Who: Who.Flagged) };

        var people = Targets(screen).Select(t => t.Target).OfType<OverlayTarget.Person>().Select(p => p.SubjectId);

        Assert.Equal(["usr_rin"], people);
    }

    [Fact]
    public void AFilteredAuditLogDrawsOnlyTheKindsPicked()
    {
        var events = new[] { Event("e1", LiveEventKinds.PersonJoined), Event("e2", LiveEventKinds.PersonLeft) };
        var joined = new KindPick().Toggle(LiveEventKinds.PersonJoined);

        var screen = Screen(OverlayPage.Events, null, events) with { EventFilters = new ListFilters(Kinds: joined) };

        Assert.Single(Targets(screen), t => t.Target is OverlayTarget.Person);
    }

    [Fact]
    public void AChangedFilterIsAChangeWorthRedrawing()
    {
        var plain = Screen(OverlayPage.Instance);

        Assert.False(plain.LooksTheSameAs(plain with { RosterFilters = new ListFilters(Open: FilterPart.Who) }));
        Assert.False(plain.LooksTheSameAs(plain with { EventFilters = new ListFilters(Who: Who.Staff) }));
        Assert.True(plain.LooksTheSameAs(plain with { RosterFilters = ListFilters.None }));
    }

    [Fact]
    public void TheClockRedrawsAListOncePerMinuteAndNothingElse()
    {
        var at = new DateTimeOffset(2026, 9, 26, 21, 0, 10, TimeSpan.Zero);
        var list = Screen(OverlayPage.Instance) with { Now = at };

        Assert.True(list.LooksTheSameAs(list with { Now = at.AddSeconds(30) }));
        Assert.False(list.LooksTheSameAs(list with { Now = at.AddMinutes(1) }));

        var person = new UserSummary("usr_rin", "Rin", RosterStanding.Flagged, 2, null, [], []);
        var card = Screen(OverlayPage.Person, person) with { Now = at };
        Assert.True(card.LooksTheSameAs(card with { Now = at.AddMinutes(5) }));
    }

    [Fact]
    public void ChangedArrivalTimesAreAChangeWorthRedrawing()
    {
        var at = new DateTimeOffset(2026, 9, 26, 21, 0, 0, TimeSpan.Zero);
        var screen = Screen(OverlayPage.Instance) with { Arrivals = new Dictionary<string, DateTimeOffset?> { ["usr_rin"] = at } };

        Assert.True(screen.LooksTheSameAs(screen with { Arrivals = new Dictionary<string, DateTimeOffset?> { ["usr_rin"] = at } }));
        Assert.False(screen.LooksTheSameAs(screen with { Arrivals = new Dictionary<string, DateTimeOffset?> { ["usr_rin"] = null } }));
        Assert.False(screen.LooksTheSameAs(screen with { Arrivals = null }));
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

    /// <summary>
    /// The wrist screen is not the roster with smaller type: no tabs, no rows, nothing to scroll.
    /// A panel a sixth the width has room for a glance and nothing else.
    /// </summary>
    [Fact]
    public void TheWristScreenCarriesNoTabsAndNoRosterRows()
    {
        var targets = Targets(Screen(OverlayPage.Wrist)).Select(t => t.Target).ToList();

        Assert.DoesNotContain(targets, t => t is OverlayTarget.GoTo);
        Assert.DoesNotContain(targets, t => t is OverlayTarget.Person);
        Assert.DoesNotContain(targets, t => t is OverlayTarget.Roster);
    }

    /// <summary>A flagged arrival is the one thing on the wrist worth pressing, and it clears.</summary>
    [Fact]
    public void AFlaggedArrivalOnTheWristCanBeCleared()
    {
        var alert = new FlaggedJoinAlert("a1", "usr_rin", "Rin", "39911", "kicked before", 2, DateTimeOffset.UnixEpoch);
        var screen = Screen(OverlayPage.Wrist) with { Alert = alert };

        var targets = Targets(screen).Select(t => t.Target).ToList();

        Assert.Contains(targets, t => t is OverlayTarget.DismissAlert);
    }

    /// <summary>Moving on or off the wrist redraws, because the panel shows something else there.</summary>
    [Fact]
    public void GoingToTheWristIsAChangeWorthRedrawing()
    {
        Assert.False(Screen(OverlayPage.Instance).LooksTheSameAs(Screen(OverlayPage.Wrist)));
        Assert.True(Screen(OverlayPage.Wrist).LooksTheSameAs(Screen(OverlayPage.Wrist)));
    }
}
