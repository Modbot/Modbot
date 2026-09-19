using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;

namespace Modbot.Companion.Tests.Instances;

public class InstanceSessionTrackerTests
{
    private const string Group = "wrld_w:1~group(grp_g)~region(use)";
    private const string Local = "usr_local";

    private readonly InstanceSessionTracker _tracker = new();
    private DateTime _at = new(2026, 9, 3, 20, 0, 0);

    private IReadOnlyList<ObservedPresence> Feed(params VRChatLogEvent[] events)
        => [.. events.SelectMany(_tracker.Observe)];

    private DateTime Tick() => _at = _at.AddSeconds(1);

    private JoiningInstanceEvent Joining(string location = Group) => new(Tick(), location);

    private PlayerJoinedEvent Joined(string id, string name = "") => new(Tick(), name, id);

    private PlayerLeftEvent Left(string id, string name = "") => new(Tick(), name, id);

    // --- Arrival: everybody already present "joins" -------------------------------------------

    [Fact]
    public void EveryoneAlreadyPresentOnArrivalIsObserved_NotAnArrival()
    {
        var observed = Feed(
            Joining(),
            Joined("usr_a"),
            Joined("usr_b"),
            Joined(Local, "me"),
            new LocalPlayerIdentifiedEvent(Tick(), "me"));

        Assert.Equal(
            [PresenceKind.PresenceObserved, PresenceKind.PresenceObserved, PresenceKind.Joined],
            observed.Select(o => o.Kind));
        Assert.Equal(["usr_a", "usr_b", Local], observed.Select(o => o.SubjectId));
    }

    [Fact]
    public void TheLocalUsersOwnArrivalIsExact()
    {
        // The burst is phantom for everyone else -- they were already there. The local user really
        // did arrive at this moment, so recording it as "present, arrival time unknown" would throw
        // away the one precise timestamp in the burst.
        var observed = Feed(
            Joining(),
            Joined("usr_a"),
            Joined(Local, "me"),
            new LocalPlayerIdentifiedEvent(Tick(), "me"));

        var mine = Assert.Single(observed, o => o.SubjectId == Local);
        Assert.Equal(PresenceKind.Joined, mine.Kind);
    }

    [Fact]
    public void ArrivalsAfterTheBurstAreGenuine()
    {
        var observed = Feed(
            Joining(),
            Joined("usr_a"),
            Joined(Local, "me"),
            new LocalPlayerIdentifiedEvent(Tick(), "me"),
            new RemotePlayerEnteredRoomEvent(Tick()),
            Joined("usr_late"));

        var late = Assert.Single(observed, o => o.SubjectId == "usr_late");
        Assert.Equal(PresenceKind.Joined, late.Kind);
    }

    [Fact]
    public void OnceTheLocalIdIsKnownTheBurstClosesAtTheLocalUsersOwnJoin()
    {
        // From the second instance onwards there is no need to wait for the "is local" line.
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"));

        var observed = Feed(
            Joining(),
            Joined("usr_a"),
            Joined(Local, "me"),
            Joined("usr_late"));

        Assert.Equal(
            [PresenceKind.PresenceObserved, PresenceKind.Joined, PresenceKind.Joined],
            observed.Select(o => o.Kind));
    }

    [Fact]
    public void NothingIsEmittedUntilTheBurstCloses()
    {
        // Held back on purpose: until the local user's own join is seen, there is no way to know
        // whether a buffered join is roster or arrival, and guessing early is how six moderators
        // turn one join into six.
        Assert.Empty(Feed(Joining(), Joined("usr_a"), Joined("usr_b")));
    }

    [Fact]
    public void AnUnterminatedBurstIsTreatedAsRosterRatherThanAsArrivals()
    {
        // If the local user's join never appears, the safe reading is "these people were here",
        // which is weaker than the truth, rather than "these people just arrived", which is wrong.
        var observed = Feed(
            Joining(),
            Joined("usr_a"),
            Joined("usr_b"),
            new LocalPlayerLeftRoomEvent(Tick()));

        Assert.All(observed, o => Assert.Equal(PresenceKind.PresenceObserved, o.Kind));
    }

    // --- Departure: everybody still present "leaves" -------------------------------------------

    [Fact]
    public void LeavesAfterOnLeftRoomArePhantomAndDropped()
    {
        var present = Feed(
            Joining(),
            Joined(Local, "me"),
            new LocalPlayerIdentifiedEvent(Tick(), "me"),
            Joined("usr_a"),
            Joined("usr_b"));
        Assert.Equal(3, present.Count);

        var departing = Feed(
            new LocalPlayerLeftRoomEvent(Tick()),
            Left("usr_a"),
            Left("usr_b"),
            Left(Local, "me"));

        // The local user's own departure, once. Nobody else left: they are still standing there.
        var mine = Assert.Single(departing);
        Assert.Equal(PresenceKind.Left, mine.Kind);
        Assert.Equal(Local, mine.SubjectId);
    }

    [Fact]
    public void LeavesBeforeOnLeftRoomAreGenuine()
    {
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"), Joined("usr_a"));

        var observed = Feed(new RemotePlayerLeftRoomEvent(Tick()), Left("usr_a"));

        var left = Assert.Single(observed);
        Assert.Equal(PresenceKind.Left, left.Kind);
        Assert.Equal("usr_a", left.SubjectId);
    }

    [Fact]
    public void OnPlayerLeftRoomDoesNotOpenThePhantomBurst()
    {
        // One character away from OnLeftRoom and the opposite meaning. Treating it as the local
        // user leaving would silently discard every genuine departure for the rest of the session.
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"), Joined("usr_a"));

        Assert.NotEmpty(Feed(new RemotePlayerLeftRoomEvent(Tick()), Left("usr_a")));
    }

    [Fact]
    public void ADepartureBurstMayNameSomebodyWhoseArrivalWasNeverLogged()
    {
        // Real behaviour from the fixture: hevy1015 is in the leave burst with no OnPlayerJoined
        // anywhere in the file -- they entered two seconds before the local user left, and the
        // join line never got written. Anything that assumes leaves pair with joins breaks here.
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"));

        var observed = Feed(new LocalPlayerLeftRoomEvent(Tick()), Left("usr_never_seen_joining"));

        // The moderator's own exact departure, and nothing invented for the stranger.
        Assert.Equal(Local, Assert.Single(observed).SubjectId);
    }

    // --- Instance identity ---------------------------------------------------------------------

    [Fact]
    public void EveryObservationCarriesTheInstanceItHappenedIn()
    {
        var observed = Feed(
            Joining("wrld_black_cat:85019~group(grp_cat)~region(use)"),
            Joined(Local, "me"),
            new LocalPlayerIdentifiedEvent(Tick(), "me"));

        var mine = Assert.Single(observed);
        Assert.Equal("wrld_black_cat", mine.Instance.WorldId);
        Assert.Equal("85019", mine.Instance.InstanceId);
        Assert.Equal("grp_cat", mine.Instance.GroupId);
    }

    [Fact]
    public void NothingIsEmittedBeforeAnInstanceIsKnown()
    {
        // The client may start while VRChat is already running. Until a transition is seen there is
        // no instance to attribute anything to, and attributing it to the wrong one is the leak the
        // routing boundary exists to prevent.
        Assert.Empty(Feed(Joined("usr_a"), Left("usr_a"), new LocalPlayerLeftRoomEvent(Tick())));
    }

    [Fact]
    public void MovingToANewInstanceDiscardsTheOldRoster()
    {
        Feed(Joining("wrld_a:1~group(grp_g)"), Joined(Local, "me"),
             new LocalPlayerIdentifiedEvent(Tick(), "me"), Joined("usr_a"));

        var observed = Feed(
            Joining("wrld_b:2~group(grp_g)"),
            Joined("usr_a"),
            Joined(Local, "me"));

        // usr_a is roster in the new instance, not a carried-over arrival.
        Assert.Equal(
            [PresenceKind.PresenceObserved, PresenceKind.Joined],
            observed.Select(o => o.Kind));
        Assert.All(observed, o => Assert.Equal("wrld_b", o.Instance.WorldId));
    }

    [Fact]
    public void AnUnparseableLocationStopsReportingRatherThanGuessing()
    {
        Assert.Empty(Feed(Joining("this is not a location"), Joined("usr_a"), Joined(Local, "me")));
    }

    // --- Avatars ------------------------------------------------------------------------------

    [Fact]
    public void AvatarLinesDuringTheArrivalBurstArePhantomToo()
    {
        // VRChat logs "Switching X to avatar Y" for everyone already present when the local user
        // arrives. Nobody changed avatar; that is simply what they are wearing. Recording them
        // would inflate avatar-change counts by the instance population every time a moderator
        // walks in -- the same failure as the join burst, and neither research note mentions it.
        var observed = Feed(
            Joining(),
            new AvatarSwitchedEvent(Tick(), "usr_a_name to avatar Smol shark"),
            Joined("usr_a", "usr_a_name"),
            Joined(Local, "me"),
            new LocalPlayerIdentifiedEvent(Tick(), "me"));

        Assert.DoesNotContain(observed, o => o.Kind == PresenceKind.AvatarChanged);
    }

    [Fact]
    public void AnAvatarChangeWhilePresentResolvesToTheWearersId()
    {
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"),
             Joined("usr_a", "ΛƧƬΛ"));

        var observed = Feed(new AvatarSwitchedEvent(Tick(), "ΛƧƬΛ to avatar Smol shark"));

        var change = Assert.Single(observed);
        Assert.Equal(PresenceKind.AvatarChanged, change.Kind);
        Assert.Equal("usr_a", change.SubjectId);
        Assert.Equal("Smol shark", change.AvatarName);
    }

    [Fact]
    public void AnAvatarChangeForSomebodyNotPresentIsDropped()
    {
        // Without a roster entry there is no user id, and a fact about a display name alone is
        // worth nothing: names are mutable and collide.
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"));

        Assert.Empty(Feed(new AvatarSwitchedEvent(Tick(), "a stranger to avatar Crow")));
    }

    [Fact]
    public void TheAmbiguousAvatarSplitIsResolvedAgainstTheRoster()
    {
        // Display name "a to avatar b" wearing avatar "c". Reading the first separator would
        // attribute the change to a person called "a" who is not here.
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"),
             Joined("usr_tricky", "a to avatar b"));

        var change = Assert.Single(Feed(new AvatarSwitchedEvent(Tick(), "a to avatar b to avatar c")));
        Assert.Equal("usr_tricky", change.SubjectId);
        Assert.Equal("c", change.AvatarName);
    }

    [Fact]
    public void TheOtherAmbiguousAvatarSplitIsAlsoResolvedAgainstTheRoster()
    {
        // Display name "u" wearing an avatar called "a to avatar b".
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"),
             Joined("usr_u", "u"));

        var change = Assert.Single(Feed(new AvatarSwitchedEvent(Tick(), "u to avatar a to avatar b")));
        Assert.Equal("usr_u", change.SubjectId);
        Assert.Equal("a to avatar b", change.AvatarName);
    }

    [Fact]
    public void ADepartedUserLeavesTheRoster()
    {
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"),
             Joined("usr_a", "gone"));
        Feed(Left("usr_a", "gone"));

        Assert.Empty(Feed(new AvatarSwitchedEvent(Tick(), "gone to avatar Crow")));
    }

    // --- Local identity -------------------------------------------------------------------------

    [Fact]
    public void TheLocalUserIsRememberedAcrossInstances()
    {
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"));

        Assert.Equal(Local, _tracker.LocalUserId);
        Assert.Equal("me", _tracker.LocalDisplayName);
    }

    [Fact]
    public void ARenamedLocalUserIsPickedUpFromTheNextIsLocalLine()
    {
        Feed(Joining(), Joined(Local, "me"), new LocalPlayerIdentifiedEvent(Tick(), "me"));
        Feed(Joining(), Joined(Local, "me, renamed"), new LocalPlayerIdentifiedEvent(Tick(), "me, renamed"));

        Assert.Equal("me, renamed", _tracker.LocalDisplayName);
        Assert.Equal(Local, _tracker.LocalUserId);
    }

    // --- The world's readable name, for naming a saved clip -------------------------------------

    [Fact]
    public void TheWorldsReadableNameIsKeptForTheInstanceItArrivedIn()
    {
        Feed(Joining(), new WorldNameEvent(Tick(), "The Black Cat"));

        Assert.Equal("The Black Cat", _tracker.WorldName);
    }

    [Fact]
    public void ANewInstanceDoesNotKeepTheLastWorldsName()
    {
        // The name arrives on the line after Joining, so between the two there is no name. A name
        // held over would put the last world's name on a clip recorded in this one.
        Feed(Joining(), new WorldNameEvent(Tick(), "The Black Cat"));
        Feed(Joining("wrld_other:2~group(grp_g)"));

        Assert.Null(_tracker.WorldName);

        Feed(new WorldNameEvent(Tick(), "Popcorn Palace"));
        Assert.Equal("Popcorn Palace", _tracker.WorldName);
    }

    [Fact]
    public void ThereIsNoWorldNameOnceTheModeratorHasLeft()
    {
        Feed(Joining(), new WorldNameEvent(Tick(), "The Black Cat"));
        Feed(new LocalPlayerLeftRoomEvent(Tick()));

        Assert.Null(_tracker.WorldName);
        Assert.Null(_tracker.CurrentInstance);
    }

    [Fact]
    public void AForgottenSessionForgetsTheWorldName()
    {
        Feed(Joining(), new WorldNameEvent(Tick(), "The Black Cat"));
        _tracker.ForgetSession();

        Assert.Null(_tracker.WorldName);
    }

    [Fact]
    public void AWorldNameCompletesNoPresenceFact()
    {
        // It names a file. It says nothing about who is where, and it must not turn into an event
        // that is reported to anybody.
        Assert.Empty(Feed(Joining(), new WorldNameEvent(Tick(), "The Black Cat")));
    }
}
