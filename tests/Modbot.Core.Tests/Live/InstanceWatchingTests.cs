using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Core.Tests.Live;

/// <summary>
/// When a moderator is watching an instance, and who the instance holds while they are and after they stop.
/// </summary>
public class InstanceWatchingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);

    private static readonly Guid AdaDevice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BenDevice = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    /// <summary>Two moderators' clients: Ada's reports as usr_ada, Ben's as usr_ben.</summary>
    private static readonly Dictionary<Guid, string> Owners = new()
    {
        [AdaDevice] = "usr_ada",
        [BenDevice] = "usr_ben",
    };

    private static readonly InstanceKey Instance = new("wrld_a", "1001");

    private static PresenceMark M(
        string type,
        string subject,
        double minutes,
        Guid? device = null,
        string instance = "1001",
        string world = "wrld_a",
        string? name = null)
        => new(type, subject, T0.AddMinutes(minutes), world, instance, device, name);

    private static InstancePeople Work(DateTimeOffset? closedAt, params PresenceMark[] marks)
        => InstanceWatching.Work(Instance, marks, Owners, closedAt);

    private const string Joined = FactType.InstanceJoined;
    private const string Here = FactType.InstancePresenceObserved;
    private const string Left = FactType.InstanceLeft;
    private const string Stopped = FactType.InstanceLogStopped;

    /// <summary>Ada walks in at T0 and finds Cid and Dee already there.</summary>
    private static PresenceMark[] AdaArrives() =>
    [
        M(Here, "usr_cid", -1.0 / 60, AdaDevice, name: "Cid"),
        M(Here, "usr_dee", 0, AdaDevice, name: "Dee"),
        M(Joined, "usr_ada", 0, AdaDevice, name: "Ada"),
    ];

    [Fact]
    public void PresenceNoPairedClientReported_IsNotAWatch()
    {
        var people = Work(null,
            M(Joined, "usr_cid", 0),
            M(Here, "usr_dee", 0, device: Guid.NewGuid()));

        Assert.False(people.IsWatched);
        Assert.Empty(people.Here);
        Assert.Null(people.LastWatchedAt);
        Assert.Empty(people.LastSeen);
    }

    /// <summary>
    /// The defect this rule was rewritten for. A client that starts while VRChat is already in an
    /// instance replays the arrival burst to learn where it is and reports none of it, so the
    /// server never gets a fact about the moderator -- only their reports of everybody else. The
    /// instance read as unwatched with its own moderator standing in it.
    /// </summary>
    [Fact]
    public void AClientReportingOnlyOtherPeople_IsStillWatching()
    {
        var people = Work(null,
            M(Joined, "usr_cid", 0, AdaDevice, name: "Cid"),
            M(Joined, "usr_dee", 3, AdaDevice, name: "Dee"));

        var watcher = Assert.Single(people.Watching);
        Assert.Equal("usr_ada", watcher.UserId);
        Assert.Equal(T0, watcher.Since);

        Assert.Equal(["usr_cid", "usr_dee"], people.Here.Select(p => p.UserId).Order());
        Assert.DoesNotContain(people.Here, p => p.UserId == "usr_ada");
    }

    /// <summary>The same client, later reporting somebody's departure and nothing else.</summary>
    [Fact]
    public void AClientReportingOnlySomebodyElseLeaving_IsStillWatching()
    {
        var people = Work(null, M(Left, "usr_cid", 0, AdaDevice, name: "Cid"));

        Assert.Equal("usr_ada", Assert.Single(people.Watching).UserId);
        Assert.Empty(people.Here);
    }

    [Fact]
    public void TwoClientsReportingFromOneInstance_AreTwoWatchers()
    {
        var people = Work(null,
            M(Joined, "usr_cid", 0, AdaDevice),
            M(Joined, "usr_dee", 1, BenDevice));

        Assert.Equal(["usr_ada", "usr_ben"], people.Watching.Select(w => w.UserId).Order());
    }

    /// <summary>
    /// One moderator with two PCs in one instance is one line, not two -- a reader wants to know
    /// who is there, not how many machines they run.
    /// </summary>
    [Fact]
    public void OneModeratorsTwoClients_AreOneWatcher()
    {
        var secondPc = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        var owners = new Dictionary<Guid, string>(Owners) { [secondPc] = "usr_ada" };

        var people = InstanceWatching.Work(
            Instance,
            [M(Joined, "usr_cid", 5, secondPc), .. AdaArrives()],
            owners,
            closedAt: null);

        var watcher = Assert.Single(people.Watching);
        Assert.Equal("usr_ada", watcher.UserId);
        Assert.Equal(T0, watcher.Since);
    }

    [Fact]
    public void AModeratorsArrivalBurst_StartsTheWatch_AndTheBurstIsHereBefore()
    {
        var people = Work(null, AdaArrives());

        var watcher = Assert.Single(people.Watching);
        Assert.Equal("usr_ada", watcher.UserId);
        Assert.Equal("Ada", watcher.DisplayName);

        // The first thing her client reported, which is Cid a second before her own join: VRChat
        // logs everybody already present before it logs you, and her client was there for both.
        Assert.Equal(T0.AddSeconds(-1), watcher.Since);

        Assert.Equal(["usr_ada", "usr_cid", "usr_dee"], people.Here.Select(p => p.UserId).Order());

        var cid = people.Here.Single(p => p.UserId == "usr_cid");
        Assert.False(cid.SeenArriving);
        Assert.Equal(T0.AddSeconds(-1), cid.Since);
        Assert.Equal("Cid", cid.DisplayName);

        Assert.True(people.Here.Single(p => p.UserId == "usr_ada").SeenArriving);
    }

    [Fact]
    public void SomebodyWhoWalksInDuringTheWatch_HasTheirExactArrivalTime()
    {
        var people = Work(null, [.. AdaArrives(), M(Joined, "usr_eve", 5, AdaDevice)]);

        var eve = people.Here.Single(p => p.UserId == "usr_eve");
        Assert.True(eve.SeenArriving);
        Assert.Equal(T0.AddMinutes(5), eve.Since);
    }

    [Fact]
    public void TheModeratorLeaving_EndsTheWatch_AndEveryoneBecomesLastSeen()
    {
        var people = Work(null, [.. AdaArrives(), M(Left, "usr_ada", 30, AdaDevice)]);

        Assert.False(people.IsWatched);
        Assert.Empty(people.Here);
        Assert.Equal(T0.AddMinutes(30), people.LastWatchedAt);
        Assert.Equal(["usr_cid", "usr_dee"], people.LastSeen.Select(p => p.UserId).Order());
    }

    [Fact]
    public void TheModeratorsLogStopping_EndsTheWatch()
    {
        var people = Work(null, [.. AdaArrives(), M(Stopped, "usr_ada", 20, AdaDevice)]);

        Assert.False(people.IsWatched);
        Assert.Equal(T0.AddMinutes(20), people.LastWatchedAt);
        Assert.Equal(["usr_cid", "usr_dee"], people.LastSeen.Select(p => p.UserId).Order());
    }

    [Fact]
    public void TheModeratorTurningUpInAnotherInstance_EndsTheWatch()
    {
        var people = Work(null, [.. AdaArrives(), M(Here, "usr_ada", 15, BenDevice, instance: "2002")]);

        Assert.False(people.IsWatched);
        Assert.Equal(T0.AddMinutes(15), people.LastWatchedAt);
    }

    [Fact]
    public void TheSameNumberInAnotherWorld_IsAnotherInstance()
    {
        var people = Work(null, [.. AdaArrives(), M(Joined, "usr_ada", 15, AdaDevice, world: "wrld_b")]);

        Assert.False(people.IsWatched);
        Assert.Equal(T0.AddMinutes(15), people.LastWatchedAt);
    }

    [Fact]
    public void TheInstanceClosing_EndsTheWatch()
    {
        var people = Work(T0.AddMinutes(40), AdaArrives());

        Assert.False(people.IsWatched);
        Assert.Empty(people.Here);
        Assert.Equal(T0.AddMinutes(40), people.LastWatchedAt);
        Assert.Equal(3, people.LastSeen.Count);
    }

    /// <summary>
    /// Ben's client saw Ada standing there. That makes Ben's client the one watching -- it is in
    /// the instance, which is what the roster rests on -- and says nothing about Ada's, which has
    /// reported nothing here.
    /// </summary>
    [Fact]
    public void AModeratorSeenBySomebodyElsesClient_IsInTheInstanceButNotWatching()
    {
        var people = Work(null, M(Here, "usr_ada", 0, BenDevice));

        Assert.Equal("usr_ben", Assert.Single(people.Watching).UserId);
        Assert.Contains(people.Here, p => p.UserId == "usr_ada");
    }

    [Fact]
    public void OverlappingWatches_AreOneUnbrokenWatch()
    {
        var people = Work(null,
        [
            .. AdaArrives(),
            M(Here, "usr_cid", 10, BenDevice),
            M(Joined, "usr_ben", 10, BenDevice),
            M(Left, "usr_ada", 20, AdaDevice),
        ]);

        var watcher = Assert.Single(people.Watching);
        Assert.Equal("usr_ben", watcher.UserId);

        var cid = people.Here.Single(p => p.UserId == "usr_cid");
        Assert.Equal(T0.AddSeconds(-1), cid.Since);
        Assert.Contains(people.Here, p => p.UserId == "usr_dee");
        Assert.DoesNotContain(people.Here, p => p.UserId == "usr_ada");
    }

    [Fact]
    public void ANewWatchAfterAGap_CountsOnlyWhatItSaw()
    {
        // Dee was there when Ada watched. Nobody watched for an hour, and when Ben walks in Dee is
        // not in his arrival burst -- so Dee is not listed, however recent Ada's sighting looks.
        var people = Work(null,
        [
            .. AdaArrives(),
            M(Left, "usr_ada", 10, AdaDevice),
            M(Here, "usr_cid", 70, BenDevice),
            M(Joined, "usr_ben", 70, BenDevice),
        ]);

        Assert.Equal(["usr_ben", "usr_cid"], people.Here.Select(p => p.UserId).Order());

        var cid = people.Here.Single(p => p.UserId == "usr_cid");
        Assert.False(cid.SeenArriving);
        Assert.Equal(T0.AddMinutes(70), cid.Since);
    }

    [Fact]
    public void AnExactArrival_BeatsALaterModeratorsHereBefore()
    {
        var people = Work(null,
        [
            .. AdaArrives(),
            M(Joined, "usr_eve", 5, AdaDevice),
            M(Here, "usr_eve", 10, BenDevice),
            M(Joined, "usr_ben", 10, BenDevice),
        ]);

        var eve = people.Here.Single(p => p.UserId == "usr_eve");
        Assert.True(eve.SeenArriving);
        Assert.Equal(T0.AddMinutes(5), eve.Since);
    }

    [Fact]
    public void LeavingAndComingBack_IsAFreshStay()
    {
        var people = Work(null,
        [
            .. AdaArrives(),
            M(Joined, "usr_eve", 5, AdaDevice),
            M(Left, "usr_eve", 6, AdaDevice),
            M(Joined, "usr_eve", 8, AdaDevice),
        ]);

        Assert.Equal(T0.AddMinutes(8), people.Here.Single(p => p.UserId == "usr_eve").Since);
    }

    [Fact]
    public void AfterAStoppedLogResumes_TheWatchStartsAgainFromTheRestatedInstance()
    {
        var people = Work(null,
        [
            .. AdaArrives(),
            M(Stopped, "usr_ada", 20, AdaDevice),
            M(Here, "usr_cid", 40, AdaDevice),
            M(Here, "usr_ada", 40, AdaDevice),
        ]);

        Assert.Equal(T0.AddMinutes(40), Assert.Single(people.Watching).Since);
        Assert.Equal(["usr_ada", "usr_cid"], people.Here.Select(p => p.UserId).Order());
        Assert.DoesNotContain(people.Here, p => p.UserId == "usr_dee");
    }

    /// <summary>
    /// Ada was here earlier and left. What her client saw on that visit belongs to that watch, and
    /// the walk back in starts a fresh one that counts only what it saw.
    /// </summary>
    [Fact]
    public void AFactFromAnEarlierWatchIsNotCounted_OutsideTheArrivalBurst()
    {
        var people = Work(null,
        [
            M(Here, "usr_old", -5, AdaDevice),
            M(Left, "usr_ada", -4, AdaDevice),
            .. AdaArrives(),
        ]);

        Assert.DoesNotContain(people.Here, p => p.UserId == "usr_old");
    }
}
