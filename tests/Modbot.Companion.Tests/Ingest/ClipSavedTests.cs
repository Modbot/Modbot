using System.Security.Cryptography;
using Modbot.Companion.Clips;
using Modbot.Companion.Ingest;
using Modbot.Companion.Journal;
using Modbot.Companion.Presentation;
using Modbot.Companion.Time;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Ingest;

/// <summary>
/// Telling a paired server a clip was saved: what goes on the wire, which server hears of it, and
/// that the box for it is off unless somebody ticks it (clips design spec §16).
/// </summary>
public sealed class ClipSavedTests : IDisposable
{
    private sealed class CountingTransport : IIngestTransport
    {
        public List<EventBatch> Sent { get; } = [];

        public Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken ct)
        {
            Sent.Add(batch);
            return Task.FromResult(new IngestResult(IngestOutcome.Accepted, batch.Events.Count));
        }
    }

    private static readonly TimeZoneInfo Utc =
        TimeZoneInfo.CreateCustomTimeZone("Test/Utc", TimeSpan.Zero, "UTC", "UTC");

    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-clip-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 1, 21, 14, 0, TimeSpan.Zero));
    private readonly CountingTransport _transport = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServerConnection Connection(SentJournal? journal = null)
    {
        var pairing = new ServerPairing("cats", new Uri("https://modbot.example"), "token", "grp_cats");
        var serverClock = new ServerClock(_clock);

        return new ServerConnection(
            pairing,
            new FileEventBuffer(Path.Combine(_directory, "cats.jsonl"), _clock),
            new PresenceEventMapper(new LogTimestampConverter(Utc), serverClock),
            serverClock,
            _transport,
            _clock,
            companionVersion: "2026.10.0",
            journal: journal);
    }

    private ClipReport Clip(string groupId = "grp_cats") => new(
        "usr_mod", "Alex", "wrld_cat", "98874", groupId, _clock.UtcNow, Hash, 52_428_800);

    [Fact]
    public void TheEventCarriesTheFingerprintAndNothingElseAboutTheClip()
    {
        var mapper = new PresenceEventMapper(new LogTimestampConverter(Utc), new ServerClock(_clock));

        var wire = mapper.MapClipSaved(Clip());

        Assert.Equal(CompanionEventType.ClipSaved, wire.Type);
        Assert.Equal("usr_mod", wire.SubjectId);
        Assert.Equal("wrld_cat", wire.WorldId);
        Assert.Equal("98874", wire.InstanceId);
        Assert.Equal("grp_cats", wire.GroupId);
        Assert.Equal(_clock.UtcNow, wire.OccurredAt);
        Assert.Null(wire.OccurredBefore);

        // The whole of the data: no file name, no folder, no path.
        Assert.Equal(["clipBytes", "clipHash", "displayName"], wire.Data.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Hash, wire.Data["clipHash"]);
        Assert.Equal("52428800", wire.Data["clipBytes"]);
        Assert.Equal("Alex", wire.Data["displayName"]);
    }

    [Fact]
    public async Task OnlyTheServerWhoseGroupOwnsTheInstanceHearsOfIt()
    {
        var connection = Connection();

        Assert.False(connection.AcceptClipSaved(Clip("grp_dogs")));
        Assert.Equal(0, connection.Pending);

        Assert.True(connection.AcceptClipSaved(Clip()));
        Assert.Equal(1, connection.Pending);

        // Not one of the changes that go within two seconds: it waits for the next batch.
        Assert.False(connection.IsDueToSend());
        _clock.Advance(ServerConnection.DefaultBatchInterval);
        await connection.PumpAsync(Ct);

        var sent = Assert.Single(Assert.Single(_transport.Sent).Events);
        Assert.Equal(CompanionEventType.ClipSaved, sent.Type);
    }

    [Fact]
    public void APausedConnectionIsToldNothingAndSaysSo()
    {
        var journal = new SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        var connection = Connection(journal);
        connection.IsPaused = true;

        Assert.False(connection.AcceptClipSaved(Clip()));
        Assert.Equal(0, connection.Pending);
        Assert.Contains(journal.Recent(), e => e.Kind == JournalEntryKind.Withheld && e.Summary.Contains("saved clip", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEventsPageNamesASavedClip()
    {
        var mapper = new PresenceEventMapper(new LogTimestampConverter(Utc), new ServerClock(_clock));

        Assert.Equal("You saved a clip here", SentJournal.Describe(mapper.MapClipSaved(Clip())));
    }

    /// <summary>
    /// A clip the server refuses — here, because the device's owner has no VRChat account linked —
    /// is written on the Events page as failed, with the reason, not as sent.
    /// </summary>
    [Fact]
    public async Task AnEventTheServerRefusesIsWrittenAsFailedWithTheReason()
    {
        var journal = new SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        var refusing = new RefusingTransport();
        var pairing = new ServerPairing("cats", new Uri("https://modbot.example"), "token", "grp_cats");
        var serverClock = new ServerClock(_clock);
        var connection = new ServerConnection(
            pairing,
            new FileEventBuffer(Path.Combine(_directory, "refused.jsonl"), _clock),
            new PresenceEventMapper(new LogTimestampConverter(Utc), serverClock),
            serverClock,
            refusing,
            _clock,
            companionVersion: "2026.10.0",
            journal: journal);

        Assert.True(connection.AcceptClipSaved(Clip()));
        _clock.Advance(ServerConnection.DefaultBatchInterval);
        await connection.PumpAsync(Ct);

        var row = Assert.Single(journal.Events(), r => !r.IsNote);
        Assert.Equal(JournalEntryKind.Failed, row.State);
        Assert.Contains(
            journal.Recent(),
            e => e.Kind == JournalEntryKind.Note
                && e.Summary.Contains(ServerConnection.Explain("not_the_device_owner"), StringComparison.Ordinal));
        Assert.Equal(0, connection.Pending);
    }

    private sealed class RefusingTransport : IIngestTransport
    {
        public Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken ct)
            => Task.FromResult(new IngestResult(
                IngestOutcome.Accepted,
                Rejected: 1,
                Refused: [new RefusedEvent(0, "not_the_device_owner")]));
    }

    [Fact]
    public void TellingTheServerIsOffUnlessSomebodyTicksIt()
    {
        Assert.False(ClipSettings.Default.TellServer);

        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{ "clips": { "on": true } }""");
        Assert.False(CompanionSettings.Load(path).Clips.TellServer);

        Assert.True(CompanionSettings.SaveClips(path, ClipSettings.Default with { On = true, TellServer = true }));
        Assert.True(CompanionSettings.Load(path).Clips.TellServer);
        Assert.Contains("\"tellServer\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFingerprintIsTheFilesSha256AndSize()
    {
        var path = Path.Combine(_directory, "The Black Cat_98874_2026-10-01 21-14-00.mp4");
        var bytes = RandomNumberGenerator.GetBytes(200_000);
        await File.WriteAllBytesAsync(path, bytes, Ct);

        var fingerprint = await ClipFingerprint.OfAsync(path, Ct);

        Assert.NotNull(fingerprint);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), fingerprint.Value.Hash);
        Assert.Equal(bytes.Length, fingerprint.Value.Bytes);
    }

    [Fact]
    public async Task AFileThatIsGoneHasNoFingerprint()
        => Assert.Null(await ClipFingerprint.OfAsync(Path.Combine(_directory, "gone.mp4"), Ct));
}
