using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Journal;

/// <summary>
/// The one word a screen says about an event, over every combination of what the paired server and
/// the hidden backup have done with it.
/// </summary>
/// <remarks>
/// <para>This is the rule a moderator reads without knowing it is a rule, so it is worth being
/// exhaustive about. The thing it must never do is let the backup make an event look worse than it
/// is: "failed" on that screen means a group's record is missing something, and a backup that
/// refused a batch does not mean that. The thing it must also never do is let the backup make an
/// event look better than it is — a paused client still says withheld, whatever the backup got
/// up to, because the moderator paused reporting and is owed the truth about that.</para>
/// <para>Rows are built through the journal rather than by hand wherever the folding matters, so
/// the rule is tested over what the client actually writes.</para>
/// </remarks>
public class SentRuleTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-sent-rule-tests", Guid.NewGuid().ToString("n"));

    private readonly FakeClock _clock = new();

    private string Path_ => Path.Combine(_directory, "cats.journal.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private static ObservedPresence Observation(string subject = "usr_8f2c")
    {
        Assert.True(
            InstanceLocation.TryParse("wrld_4b34:39911~group(grp_cats)~region(eu)", out var instance));

        return new ObservedPresence(
            PresenceKind.Joined,
            new DateTime(2026, 9, 12, 20, 14, 7),
            subject,
            "Rin",
            instance);
    }

    private static CompanionEvent Event() => new()
    {
        CompanionEventId = Guid.NewGuid().ToString("n"),
        Type = CompanionEventType.InstanceJoined,
        OccurredAt = new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero),
        SubjectId = "usr_8f2c",
        WorldId = "wrld_4b34",
        InstanceId = "39911",
        GroupId = "grp_cats",
        Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["displayName"] = "Rin" },
    };

    private static JournalRow Row(JournalEntryKind? server, JournalEntryKind? cloud)
        => new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), "Rin joined your world", "cats", server, cloud);

    // ── The rule, every combination ───────────────────────────────────────────────────────────

    [Theory]
    // The paired server took it. Nothing the backup does afterwards changes that.
    [InlineData(JournalEntryKind.Sent, JournalEntryKind.Waiting, JournalEntryKind.Sent)]
    [InlineData(JournalEntryKind.Sent, JournalEntryKind.Failed, JournalEntryKind.Sent)]
    [InlineData(JournalEntryKind.Sent, JournalEntryKind.Sent, JournalEntryKind.Sent)]
    [InlineData(JournalEntryKind.Sent, null, JournalEntryKind.Sent)]
    // Nothing has it yet: waiting, and a backup that is also waiting or has given up on a batch
    // never turns that into failed.
    [InlineData(JournalEntryKind.Waiting, JournalEntryKind.Waiting, JournalEntryKind.Waiting)]
    [InlineData(JournalEntryKind.Waiting, JournalEntryKind.Failed, JournalEntryKind.Waiting)]
    [InlineData(JournalEntryKind.Waiting, null, JournalEntryKind.Waiting)]
    // Something has it: the backup accepted it while the paired server is still queuing.
    [InlineData(JournalEntryKind.Waiting, JournalEntryKind.Sent, JournalEntryKind.Sent)]
    // The moderator's own decisions and the server's own refusals stand, whatever the backup did.
    [InlineData(JournalEntryKind.Withheld, JournalEntryKind.Sent, JournalEntryKind.Withheld)]
    [InlineData(JournalEntryKind.Withheld, null, JournalEntryKind.Withheld)]
    [InlineData(JournalEntryKind.Failed, JournalEntryKind.Sent, JournalEntryKind.Failed)]
    [InlineData(JournalEntryKind.Failed, null, JournalEntryKind.Failed)]
    public void WhatOneRowSays(JournalEntryKind? server, JournalEntryKind? cloud, JournalEntryKind expected)
        => Assert.Equal(expected, Row(server, cloud).State);

    [Theory]
    [InlineData(JournalEntryKind.Sent)]
    [InlineData(JournalEntryKind.Waiting)]
    [InlineData(JournalEntryKind.Failed)]
    [InlineData(null)]
    public void AnEventNoPairedServerWasEverGivenSaysNothing(JournalEntryKind? cloud)
    {
        // Every instance outside a managed group, and everything at all for a client with nothing
        // paired. The time and the sentence are the whole truth a screen can tell about it: no
        // group's record is involved, so no word about one would be honest.
        Assert.Null(Row(null, cloud).State);
    }

    [Fact]
    public void WithTheBackupOffARowSaysExactlyWhatTheServerDid()
    {
        // The switch is in settings.json and the environment, and a client that has it off writes
        // no backup lines at all. Nothing about the rule depends on the backup being there.
        foreach (var state in new[]
                 {
                     JournalEntryKind.Sent,
                     JournalEntryKind.Waiting,
                     JournalEntryKind.Withheld,
                     JournalEntryKind.Failed,
                 })
        {
            Assert.Equal(state, Row(state, null).State);
        }
    }

    // ── The same rule, over what the client actually writes ───────────────────────────────────

    [Fact]
    public void TheServerTookItAndTheBackupHasNotAnsweredYet()
    {
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toServer = Event();
        var toCloud = Event();

        journal.RecordQueued("cats", JournalDestination.Server, key, toServer);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);
        journal.RecordSent("cats", [toServer]);

        Assert.Equal(JournalEntryKind.Sent, Assert.Single(journal.Events()).State);
    }

    [Fact]
    public void TheServerTookItAndTheBackupThenRefusedTheBatch()
    {
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toServer = Event();
        var toCloud = Event();

        journal.RecordQueued("cats", JournalDestination.Server, key, toServer);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);
        journal.RecordSent("cats", [toServer]);
        journal.RecordFailed(SentJournal.CloudName, [toCloud], JournalDestination.Cloud);

        var row = Assert.Single(journal.Events());

        Assert.Equal(JournalEntryKind.Sent, row.State);
        Assert.Equal(JournalEntryKind.Failed, row.CloudState);
    }

    [Fact]
    public void TheBackupTookItWhileTheServerIsStillQueuing()
    {
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toServer = Event();
        var toCloud = Event();

        journal.RecordQueued("cats", JournalDestination.Server, key, toServer);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);
        journal.RecordSent(SentJournal.CloudName, [toCloud], JournalDestination.Cloud);

        Assert.Equal(JournalEntryKind.Sent, Assert.Single(journal.Events()).State);
    }

    [Fact]
    public void BothTookIt()
    {
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toServer = Event();
        var toCloud = Event();

        journal.RecordQueued("cats", JournalDestination.Server, key, toServer);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);
        journal.RecordSent("cats", [toServer]);
        journal.RecordSent(SentJournal.CloudName, [toCloud], JournalDestination.Cloud);

        Assert.Equal(JournalEntryKind.Sent, Assert.Single(journal.Events()).State);
    }

    [Fact]
    public void NeitherHasTakenIt()
    {
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());

        journal.RecordQueued("cats", JournalDestination.Server, key, Event());
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, Event());

        Assert.Equal(JournalEntryKind.Waiting, Assert.Single(journal.Events()).State);
    }

    [Fact]
    public void WithTheBackupOffTheOnlyLinesAreTheServersOwn()
    {
        var journal = new SentJournal(Path_, _clock);
        var toServer = Event();

        journal.RecordQueued("cats", JournalDestination.Server, SentJournal.KeyFor(Observation()), toServer);
        Assert.Equal(JournalEntryKind.Waiting, Assert.Single(journal.Events()).State);

        journal.RecordSent("cats", [toServer]);

        var row = Assert.Single(journal.Events());
        Assert.Equal(JournalEntryKind.Sent, row.State);
        Assert.Null(row.CloudState);
    }

    [Fact]
    public void APausedClientStillSaysWithheldHoweverWellTheBackupIsDoing()
    {
        // Pausing stops reporting to that server and does not stop the backup, which is its own
        // independent flow. The row is about the group's record, so it says what happened to the
        // group's record: nothing was sent to that server, and nothing was captured for it.
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toCloud = Event();

        journal.RecordWithheld("cats", "Not sent — reporting is paused.", eventKey: key);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);
        journal.RecordSent(SentJournal.CloudName, [toCloud], JournalDestination.Cloud);

        var row = Assert.Single(journal.Events());

        Assert.Equal(JournalEntryKind.Withheld, row.State);
        Assert.Equal(JournalEntryKind.Sent, row.CloudState);
    }

    [Fact]
    public void AnEventOnlyTheBackupWasGivenIsStillARowAndSaysNothingAboutWhereItWent()
    {
        // The usual case for every instance outside a managed group. The row is there, because the
        // page shows every event this client processed; it carries no state, because no paired
        // server was involved and the backup is not something the screen talks about.
        var journal = new SentJournal(Path_, _clock);
        var toCloud = Event();

        journal.RecordQueued(
            SentJournal.CloudName, JournalDestination.Cloud, SentJournal.KeyFor(Observation()), toCloud);
        journal.RecordSent(SentJournal.CloudName, [toCloud], JournalDestination.Cloud);

        var row = Assert.Single(journal.Events());

        Assert.Null(row.State);
        Assert.Null(row.ServerId);
        Assert.False(row.IsNote);
        Assert.Equal("Rin joined your world", row.Summary);
    }

    [Fact]
    public void ANoteIsStillANoteAndNotAState()
    {
        var journal = new SentJournal(Path_, _clock);
        journal.RecordNote("cats", "Paused");

        var row = Assert.Single(journal.Events());

        Assert.True(row.IsNote);
        Assert.Null(row.State);
    }
}
