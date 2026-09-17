using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Journal;

/// <summary>
/// The journal is the answer given to a moderator who does not read C#. It has to be legible, it
/// has to be bounded, and it has to still be there tomorrow — "what did this thing send
/// yesterday" is a question people ask after they get suspicious, not before.
/// </summary>
public class SentJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-journal-tests", Guid.NewGuid().ToString("n"));

    private string Path_ => Path.Combine(_directory, "cats.journal.jsonl");

    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private static CompanionEvent Event(
        CompanionEventType type = CompanionEventType.InstanceJoined,
        string subject = "usr_8f2c",
        string? displayName = "Rin",
        string? avatarName = null)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (displayName is not null)
            data["displayName"] = displayName;
        if (avatarName is not null)
            data["avatarName"] = avatarName;

        return new CompanionEvent
        {
            CompanionEventId = Guid.NewGuid().ToString("n"),
            Type = type,
            OccurredAt = new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero),
            SubjectId = subject,
            WorldId = "wrld_4b34",
            InstanceId = "39911",
            GroupId = "grp_cats",
            Data = data,
        };
    }

    [Fact]
    public void RecordsOneLineOfPlainEnglishPerDisclosure()
    {
        var journal = new SentJournal(Path_, _clock);

        journal.RecordSent("cats", [Event(), Event(CompanionEventType.InstanceLeft, "usr_aa", "Mei")]);

        var lines = journal.Recent().Select(e => e.Summary).ToList();

        Assert.Contains("Rin joined your world", lines);
        Assert.Contains("Mei left your world", lines);
    }

    /// <summary>
    /// An observation in a group no paired server manages is a row of its own, with the sentence
    /// and no destination -- and if Modbot Cloud took the same event, one row, not two.
    /// </summary>
    [Fact]
    public void WhatWentNowhereIsStillOnTheScreenWithoutAGroup()
    {
        var journal = new SentJournal(Path_, _clock);
        Assert.True(Modbot.Companion.Instances.InstanceLocation.TryParse("wrld_4b34:39911~group(grp_dogs)~groupAccessType(members)", out var instance));
        var observation = new Modbot.Companion.Instances.ObservedPresence(
            Modbot.Companion.Instances.PresenceKind.Joined, new DateTime(2026, 9, 12, 20, 14, 7), "usr_8f2c", "Rin", instance);

        journal.RecordSeen(observation);

        var row = Assert.Single(journal.Events());
        Assert.True(row.Seen);
        Assert.False(row.IsNote);
        Assert.Equal("Rin joined your world", row.Summary);
        Assert.Null(row.ServerState);
        Assert.Null(row.CloudState);
    }

    [Fact]
    public void SaysWhenSomebodyWasAlreadyThereRatherThanCallingItAnArrival()
    {
        // The difference this wording protects is the one that stops one moderator walking into a
        // instance becoming forty fake arrivals in the data.
        var summary = SentJournal.Describe(Event(CompanionEventType.InstancePresenceObserved));

        Assert.Contains("was already in", summary);
        Assert.DoesNotContain("joined", summary);
    }

    [Fact]
    public void NamesTheAvatarOnAnAvatarChange()
    {
        var summary = SentJournal.Describe(
            Event(CompanionEventType.AvatarChanged, avatarName: "Very Normal Robot"));

        Assert.Contains("Very Normal Robot", summary);
    }

    [Fact]
    public void FallsBackToTheIdWhenTheLogGaveNoName()
    {
        var summary = SentJournal.Describe(Event(displayName: null));

        Assert.Contains("usr_8f2c", summary);
        Assert.DoesNotContain("()", summary);
    }

    [Fact]
    public void SurvivesARestart()
    {
        new SentJournal(Path_, _clock).RecordSent("cats", [Event()]);

        var reopened = new SentJournal(Path_, _clock);

        Assert.Equal(1, reopened.Count);
        Assert.Contains("Rin", Assert.Single(reopened.Recent()).Summary);
    }

    [Fact]
    public void KeepsAppendingAcrossRestartsRatherThanStartingOver()
    {
        new SentJournal(Path_, _clock).RecordSent("cats", [Event()]);
        new SentJournal(Path_, _clock).RecordSent("cats", [Event(subject: "usr_bb", displayName: "Kai")]);

        var lines = new SentJournal(Path_, _clock).Recent().Select(e => e.Summary).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Contains("Rin"));
        Assert.Contains(lines, l => l.Contains("Kai"));
    }

    [Fact]
    public void ShowsNewestFirst()
    {
        var journal = new SentJournal(Path_, _clock);

        journal.RecordSent("cats", [Event(subject: "usr_first", displayName: "First")]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        journal.RecordSent("cats", [Event(subject: "usr_last", displayName: "Last")]);

        Assert.Contains("Last", journal.Recent()[0].Summary);
    }

    [Fact]
    public void IsBoundedAndTheBoundSurvivesARestart()
    {
        var journal = new SentJournal(Path_, _clock, capacity: 5);

        for (var i = 0; i < 40; i++)
            journal.RecordSent("cats", [Event(subject: $"usr_{i}", displayName: $"Person {i}")]);

        Assert.Equal(5, journal.Count);
        Assert.Equal(5, new SentJournal(Path_, _clock, capacity: 5).Count);
        Assert.Contains("Person 39", new SentJournal(Path_, _clock, capacity: 5).Recent()[0].Summary);
    }

    [Fact]
    public void RecordsRefusalsBesideDisclosuresBecauseSilenceAndRefusalLookTheSame()
    {
        var journal = new SentJournal(Path_, _clock);

        journal.RecordWithheld("cats", "Not sent — reporting is paused.", count: 3);

        var entry = Assert.Single(journal.Recent());
        Assert.Equal(JournalEntryKind.Withheld, entry.Kind);
        Assert.Contains("×3", entry.Summary);
    }

    [Fact]
    public void ATruncatedLastLineCostsOneLineRatherThanTheWholeRecord()
    {
        new SentJournal(Path_, _clock).RecordSent("cats", [Event(), Event(subject: "usr_bb", displayName: "Kai")]);
        File.AppendAllText(Path_, "{\"at\":\"2026-09-12T20:1");

        Assert.Equal(2, new SentJournal(Path_, _clock).Count);
    }

    [Fact]
    public void ClearingRemovesTheRecordFromTheDiskToo()
    {
        var journal = new SentJournal(Path_, _clock);
        journal.RecordSent("cats", [Event()]);

        journal.Clear();

        Assert.Equal(0, journal.Count);
        Assert.Equal(0, new SentJournal(Path_, _clock).Count);
    }

    private static ObservedPresence Observation(string subject = "usr_8f2c", int second = 7)
    {
        Assert.True(
            InstanceLocation.TryParse("wrld_4b34:39911~group(grp_cats)~region(eu)", out var instance));

        return new ObservedPresence(
            PresenceKind.Joined,
            new DateTime(2026, 9, 12, 20, 14, second),
            subject,
            "Rin",
            instance);
    }

    [Fact]
    public void BothHalvesOfTheClientWorkOutTheSameKeyForTheSameEvent()
    {
        // Nobody hands the key over: the paired server's half and the Modbot Cloud half each work
        // it out from what was observed. If they ever disagreed, one event would become two rows.
        Assert.Equal(SentJournal.KeyFor(Observation()), SentJournal.KeyFor(Observation()));
        Assert.NotEqual(SentJournal.KeyFor(Observation()), SentJournal.KeyFor(Observation("usr_other")));
        Assert.NotEqual(SentJournal.KeyFor(Observation()), SentJournal.KeyFor(Observation(second: 8)));
    }

    [Fact]
    public void AnEventSentToBothPlacesIsOneRowThatSaysWhereEachStands()
    {
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toServer = Event();
        var toCloud = Event();

        journal.RecordQueued("cats", JournalDestination.Server, key, toServer);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);
        journal.RecordSent("cats", [toServer]);

        var row = Assert.Single(journal.Events());

        Assert.Equal("cats", row.ServerId);
        Assert.Equal(JournalEntryKind.Sent, row.ServerState);
        Assert.Equal(JournalEntryKind.Waiting, row.CloudState);
        Assert.Equal("Rin joined your world", row.Summary);
    }

    [Fact]
    public void AnEventOnlyModbotCloudWasToldAboutIsStillOneRow()
    {
        // The usual case for anybody with no server paired, and for every instance outside a
        // group: Modbot Cloud hears about it and nothing else does.
        var journal = new SentJournal(Path_, _clock);
        var toCloud = Event();

        journal.RecordQueued(
            SentJournal.CloudName, JournalDestination.Cloud, SentJournal.KeyFor(Observation()), toCloud);
        journal.RecordSent(SentJournal.CloudName, [toCloud], JournalDestination.Cloud);

        var row = Assert.Single(journal.Events());

        Assert.Null(row.ServerId);
        Assert.Null(row.ServerState);
        Assert.Equal(JournalEntryKind.Sent, row.CloudState);
    }

    [Fact]
    public void TheCountOnTheScreenIsEventsRatherThanSends()
    {
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toServer = Event();
        var toCloud = Event();

        journal.RecordQueued("cats", JournalDestination.Server, key, toServer);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);
        journal.RecordSent("cats", [toServer]);
        journal.RecordSent(SentJournal.CloudName, [toCloud], JournalDestination.Cloud);

        Assert.Equal(4, journal.Count);
        Assert.Single(journal.Events());
    }

    [Fact]
    public void AJournalWrittenByAnOlderVersionStillReads()
    {
        // Lines from before the Cloud backup: a numbered kind, no destination and no key. Every
        // one of them was about a paired server, and they still have to show up as rows.
        Directory.CreateDirectory(_directory);
        File.WriteAllLines(
            Path_,
            [
                """{"at":"2026-09-12T20:14:07+00:00","kind":0,"serverId":"cats","summary":"20:14:07 — told them Rin (usr_8f2c) joined wrld_4b34:39911"}""",
                """{"at":"2026-09-12T20:15:00+00:00","kind":2,"serverId":"cats","summary":"Paused. Nothing is being captured or sent for this server."}""",
            ]);

        var rows = new SentJournal(Path_, _clock).Events();

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsNote);
        Assert.Contains("Paused", rows[0].Summary);
        Assert.Equal(JournalEntryKind.Sent, rows[1].ServerState);
        Assert.Equal("cats", rows[1].ServerId);
        Assert.Contains("Rin", rows[1].Summary);
    }

    [Fact]
    public void RefusalsAndNotesAreStillRowsOfTheirOwn()
    {
        var journal = new SentJournal(Path_, _clock);

        journal.RecordWithheld("cats", "Not sent — reporting is paused.");
        _clock.Advance(TimeSpan.FromMinutes(1));
        journal.RecordNote("cats", "Resumed reporting.");

        var rows = journal.Events();

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsNote);
        Assert.Contains("Resumed", rows[0].Summary);
        Assert.Equal(JournalEntryKind.Withheld, rows[1].ServerState);
        Assert.Contains("paused", rows[1].Summary);
    }

    [Fact]
    public void AnEventTheServerWithheldButModbotCloudTookSaysWhatItWas()
    {
        // A paused server captures nothing, so its line holds only the reason. The description
        // comes from the destination that did take the event, on the same row.
        var journal = new SentJournal(Path_, _clock);
        var key = SentJournal.KeyFor(Observation());
        var toCloud = Event();

        journal.RecordWithheld("cats", "Not sent — reporting is paused.", eventKey: key);
        journal.RecordQueued(SentJournal.CloudName, JournalDestination.Cloud, key, toCloud);

        var row = Assert.Single(journal.Events());

        Assert.Equal(JournalEntryKind.Withheld, row.ServerState);
        Assert.Equal(JournalEntryKind.Waiting, row.CloudState);
        Assert.Equal("Rin joined your world", row.Summary);
    }

    [Fact]
    public void EventsRefusedForGoodAreMarkedFailedRatherThanLeftWaitingForever()
    {
        var journal = new SentJournal(Path_, _clock);
        var toServer = Event();

        journal.RecordQueued("cats", JournalDestination.Server, SentJournal.KeyFor(Observation()), toServer);
        journal.RecordFailed("cats", [toServer]);

        Assert.Equal(JournalEntryKind.Failed, Assert.Single(journal.Events()).ServerState);
    }
}
