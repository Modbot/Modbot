using System.Collections;
using System.Reflection;
using Modbot.Companion.Credits;
using Modbot.Companion.Ingest;
using Modbot.Companion.Journal;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Presentation;
using Modbot.Companion.Voice;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// Whether the window would draw what it is already showing.
/// </summary>
/// <remarks>
/// The window is built again from a snapshot once a second, and everything built again is a new
/// control: the button under the pointer starts its hover from nothing, and a list that was open
/// closes with the control it belonged to. So the window asks the snapshot first. This is that
/// question, and it has to answer "the same" when nothing has moved and "not the same" whenever
/// anything has, because a page that is skipped wrongly is a page telling a moderator something
/// that is no longer true.
/// </remarks>
public class SnapshotLooksTheSameTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-snapshot-same-tests", Guid.NewGuid().ToString("n"));

    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private CompanionAppState State()
        => new(_clock, new SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock));

    private static ServerRow Server(string id = "cats", int pending = 0) => new(
        id,
        "https://modbot.example/",
        "grp_cats",
        "Cats",
        null,
        ConnectionState.Healthy,
        false,
        pending,
        12,
        3,
        "Up to date. Everything observed for this group has been reported.");

    private static JournalRow Event(string summary = "Somebody joined") =>
        new(DateTimeOffset.UnixEpoch, summary, "cats", JournalEntryKind.Sent, null);

    private static CompanionAppSnapshot Snapshot(
        IReadOnlyList<ServerRow>? servers = null,
        IReadOnlyList<JournalRow>? events = null,
        IReadOnlyList<CompanionWarning>? warnings = null,
        long linesRead = 100) => new(
            servers ?? [Server()],
            events ?? [Event()],
            LogHealthStatus.Healthy,
            "Reading VRChat's log.",
            linesRead,
            40,
            12,
            warnings ?? [],
            null,
            "https://modbot.example/pair");

    [Fact]
    public void TwoSnapshotsOfAClientThatHasNotMovedLookTheSame()
    {
        // The plain case, and the one the whole thing exists for: nothing happened this second,
        // so nothing on screen is touched.
        var state = State();

        Assert.True(state.Snapshot().LooksTheSameAs(state.Snapshot()));
    }

    [Fact]
    public void ASnapshotLooksTheSameAsItself()
    {
        var snapshot = Snapshot();

        Assert.True(snapshot.LooksTheSameAs(snapshot));
    }

    [Fact]
    public void AChangedCountDoesNot()
    {
        // While VRChat is running these move every second, which is why skipping alone was never
        // going to be the whole answer.
        Assert.False(Snapshot(linesRead: 100).LooksTheSameAs(Snapshot(linesRead: 101)));
    }

    [Fact]
    public void AChangedListDoesNot()
    {
        Assert.False(Snapshot().LooksTheSameAs(Snapshot(servers: [Server(pending: 4)])));
        Assert.False(Snapshot().LooksTheSameAs(Snapshot(events: [Event("Somebody left")])));
        Assert.False(Snapshot().LooksTheSameAs(Snapshot(events: [Event(), Event()])));
        Assert.False(Snapshot().LooksTheSameAs(
            Snapshot(warnings: [new CompanionWarning(WarningSeverity.Warning, "Something")])));
    }

    [Fact]
    public void AListBuiltAgainWithTheSameThingsInItLooksTheSame()
    {
        // CompanionAppState builds new lists every time it is asked for a snapshot, so this is
        // what "nothing has changed" actually looks like coming out of it.
        var one = Snapshot(servers: [Server()], events: [Event()]);
        var two = Snapshot(servers: [Server()], events: [Event()]);

        Assert.NotSame(one.Servers, two.Servers);
        Assert.NotSame(one.Events, two.Events);
        Assert.True(one.LooksTheSameAs(two));
    }

    [Fact]
    public void TheVoicesAndDevicesAreComparedOneByOneRatherThanByTheListTheyCameIn()
    {
        // The voice host lists the output devices again every few seconds, which hands the window
        // a new list holding the same devices. That is not a change and must not redraw the page.
        var voice = VoiceStatus.None with
        {
            Devices = [new OutputDevice("speakers", "Speakers")],
            Voices = [new NamedVoice("Amy", 0)],
        };

        var same = voice with
        {
            Devices = [new OutputDevice("speakers", "Speakers")],
            Voices = [new NamedVoice("Amy", 0)],
        };

        var other = voice with { Devices = [new OutputDevice("headset", "Headset")] };

        Assert.True((Snapshot() with { Voice = voice }).LooksTheSameAs(Snapshot() with { Voice = same }));
        Assert.False((Snapshot() with { Voice = voice }).LooksTheSameAs(Snapshot() with { Voice = other }));
    }

    [Fact]
    public void TheThankYouListsAreComparedOneByOneToo()
    {
        var credits = new CreditsList(
            [new CreditPerson("Someone", "https://example.test", "", null, null, null)],
            [],
            [new CreditContributor("someone", "https://example.test", "")]);

        var same = new CreditsList(
            [new CreditPerson("Someone", "https://example.test", "", null, null, null)],
            [],
            [new CreditContributor("someone", "https://example.test", "")]);

        var other = credits with { Contributors = [] };

        Assert.True((Snapshot() with { Credits = credits }).LooksTheSameAs(Snapshot() with { Credits = same }));
        Assert.False((Snapshot() with { Credits = credits }).LooksTheSameAs(Snapshot() with { Credits = other }));
    }

    [Fact]
    public void AChangeAnywhereElseInTheSnapshotIsNoticedWithoutAnybodyListingIt()
    {
        // Everything but the lists is left to the record's own equality, which is the point: a
        // field added to the snapshot later is compared without anybody remembering to come here.
        Assert.False(Snapshot().LooksTheSameAs(Snapshot() with { LogFolder = "C:\\elsewhere" }));
        Assert.False(Snapshot().LooksTheSameAs(Snapshot() with { DebugMode = true }));
        Assert.False(Snapshot().LooksTheSameAs(
            Snapshot() with { LastPairing = new PairingNotice(PairingNoticeKind.Succeeded, "Paired") }));
        Assert.False(Snapshot().LooksTheSameAs(Snapshot() with { LogStatus = LogHealthStatus.NotUnderstood }));
    }

    [Fact]
    public void ANewListInTheSnapshotThatNobodyComparedWouldDrawThePageAgain()
    {
        // The fail-safe. A list-bearing part of the snapshot is either compared here item by item
        // or left to the record, and the record compares a list by the list it came in — so two
        // new lists holding the same things read as a change and the page is built again. That is
        // exactly what every page did before any of this existed, so a part added later and
        // forgotten costs a redraw and can never leave a page stale.
        //
        // This test names the snapshot's own lists, which are the ones compared item by item. Add
        // a list to the snapshot and it fails, which is the moment to decide whether the new one
        // belongs with them or is happy costing a redraw. (The two lists that sit one level down,
        // in the voice and in the thank-you lists, have a test each above.)
        var lists = typeof(CompanionAppSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                && p.PropertyType != typeof(string)
                && typeof(IEnumerable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Events", "Servers", "Warnings"], lists);
    }
}
