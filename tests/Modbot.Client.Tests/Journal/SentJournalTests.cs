using Modbot.Client.Ingest;
using Modbot.Client.Journal;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.Journal;

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

    private static ClientEvent Event(
        ClientEventType type = ClientEventType.InstanceJoined,
        string subject = "usr_8f2c",
        string? displayName = "Rin",
        string? avatarName = null)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (displayName is not null)
            data["displayName"] = displayName;
        if (avatarName is not null)
            data["avatarName"] = avatarName;

        return new ClientEvent
        {
            ClientEventId = Guid.NewGuid().ToString("n"),
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

        journal.RecordSent("cats", [Event(), Event(ClientEventType.InstanceLeft, "usr_aa", "Mei")]);

        var lines = journal.Recent().Select(e => e.Summary).ToList();

        Assert.Contains(lines, l => l.Contains("Rin (usr_8f2c)") && l.Contains("joined"));
        Assert.Contains(lines, l => l.Contains("Mei (usr_aa)") && l.Contains("left"));
    }

    [Fact]
    public void SaysWhenSomebodyWasAlreadyThereRatherThanCallingItAnArrival()
    {
        // The difference this wording protects is the one that stops one moderator walking into a
        // room becoming forty fake arrivals in the data.
        var summary = SentJournal.Describe(Event(ClientEventType.InstancePresenceObserved));

        Assert.Contains("was already in", summary);
        Assert.DoesNotContain("joined", summary);
    }

    [Fact]
    public void NamesTheAvatarOnAnAvatarChange()
    {
        var summary = SentJournal.Describe(
            Event(ClientEventType.AvatarChanged, avatarName: "Very Normal Robot"));

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
}
