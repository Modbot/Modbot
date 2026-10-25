using Modbot.Companion.Clips;
using Modbot.Companion.Ingest;
using Modbot.Shared.Clips;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// How a clip request is answered: the switch, the wait, which recording, the cut, the upload, and
/// the word when there is nothing to upload. Fakes stand in for the recorder, the cutter and the
/// server; the files are real ones in a folder of the test's own.
/// </summary>
public sealed class ClipSenderTests : IDisposable
{
    private const long Max = 1000;

    private static readonly DateTimeOffset Action = new(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "modbot-clip-sender-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(Action + TimeSpan.FromSeconds(40));
    private readonly FakeSource _source = new();
    private readonly FakeCutter _cutter = new();
    private readonly FakeUploader _uploader = new();
    private readonly List<string> _notes = [];
    private readonly List<TimeSpan> _waits = [];
    private readonly ServerPairing _pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    private bool _sendIsOn = true;
    private ClipSender? _sender;

    public ClipSenderTests()
    {
        _source.Recording = true;
        _source.Live.Add(new RecordingSpan("live-1", Action - TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));
        _source.WholeBytes = 400;
    }

    public void Dispose()
    {
        _sender?.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private ClipSender Sender() => _sender ??= new ClipSender(
        _source,
        _cutter,
        _uploader,
        _clock,
        serverOffset: _ => TimeSpan.Zero,
        sendIsOn: () => _sendIsOn,
        note: _notes.Add,
        folder: _folder,
        maxBytes: Max,
        delay: (span, _) =>
        {
            _waits.Add(span);
            return Task.CompletedTask;
        });

    private ClipRequestMessage Request(Guid? id = null, TimeSpan? expiresAfterAction = null) => new(
        ClipRequestWire.Kind,
        id ?? Guid.NewGuid(),
        ClipRequestWire.Ban,
        Action,
        Action + (expiresAfterAction ?? TimeSpan.FromMinutes(10)));

    [Fact]
    public async Task ACutThatFitsIsUploadedAndTheFilesAreDeleted()
    {
        _cutter.Results.Enqueue(CutOf(300));

        await Sender().Ask(_pairing, Request());

        var upload = Assert.Single(_uploader.Uploads);
        Assert.Equal(300, upload.Bytes);
        Assert.Empty(_uploader.Answers);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_folder),
            f => Path.GetFileName(f).StartsWith("clip-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItWaitsUntilThirtyTwoSecondsAfterTheActionBeforeLooking()
    {
        _clock.UtcNow = Action + TimeSpan.FromSeconds(5);
        _cutter.Results.Enqueue(CutOf(300));

        await Sender().Ask(_pairing, Request());

        Assert.Equal(TimeSpan.FromSeconds(27), Assert.Single(_waits));
    }

    [Fact]
    public async Task ItDoesNotWaitWhenTheTimeHasAlreadyCome()
    {
        _cutter.Results.Enqueue(CutOf(300));

        await Sender().Ask(_pairing, Request());

        Assert.Empty(_waits);
    }

    [Fact]
    public async Task ARequestSeenTwiceIsActedOnOnce()
    {
        _cutter.Results.Enqueue(CutOf(300));
        var request = Request();

        await Sender().Ask(_pairing, request);
        await Sender().Ask(_pairing, request);

        Assert.Single(_uploader.Uploads);
        Assert.Equal(1, _cutter.Calls);
    }

    [Fact]
    public async Task ARequestPastItsExpiryIsNotActedOn()
    {
        await Sender().Ask(_pairing, Request(expiresAfterAction: TimeSpan.FromSeconds(30)));

        Assert.Empty(_uploader.Uploads);
        Assert.Empty(_uploader.Answers);
        Assert.Equal(0, _cutter.Calls);
        Assert.Equal(0, _source.Copies);
    }

    [Fact]
    public async Task WithTheSwitchOffTheAnswerIsClipsOff()
    {
        _sendIsOn = false;

        await Sender().Ask(_pairing, Request());

        Assert.Equal(ClipRequestWire.ClipsOff, Assert.Single(_uploader.Answers));
        Assert.Empty(_uploader.Uploads);
        Assert.Equal(0, _source.Copies);
    }

    [Fact]
    public async Task WhenNotRecordingTheAnswerIsClipsOff()
    {
        _source.Recording = false;

        await Sender().Ask(_pairing, Request());

        Assert.Equal(ClipRequestWire.ClipsOff, Assert.Single(_uploader.Answers));
        Assert.Empty(_uploader.Uploads);
    }

    [Fact]
    public async Task AnActionInNoRecordingIsAnsweredClipsOff()
    {
        _source.Live.Clear();
        _source.Live.Add(new RecordingSpan("old", Action - TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5)));

        await Sender().Ask(_pairing, Request());

        Assert.Equal(ClipRequestWire.ClipsOff, Assert.Single(_uploader.Answers));
        Assert.Equal(0, _source.Copies);
    }

    [Fact]
    public async Task ACutThatFailedUploadsTheWholeRecordingWhenItIsUnderTheLimit()
    {
        _cutter.Default = CutFailed();
        _source.WholeBytes = 400;

        await Sender().Ask(_pairing, Request());

        var upload = Assert.Single(_uploader.Uploads);
        Assert.Equal(400, upload.Bytes);
        Assert.Empty(_uploader.Answers);
    }

    [Fact]
    public async Task ACutThatFailedWithAWholeRecordingOverTheLimitIsAnsweredCouldNotCut()
    {
        _cutter.Default = CutFailed();
        _source.WholeBytes = 5000;

        await Sender().Ask(_pairing, Request());

        Assert.Equal(ClipRequestWire.CouldNotCut, Assert.Single(_uploader.Answers));
        Assert.Empty(_uploader.Uploads);
    }

    [Fact]
    public async Task ACutThatFailsOnceIsTriedAgainStartingLater()
    {
        // A slow recorder, so the planned clip is long enough to start before the action and
        // there is something to start later than.
        _source.BitsPerSecond = 120;
        _cutter.Results.Enqueue(CutFailed());
        _cutter.Results.Enqueue(CutOf(250));

        await Sender().Ask(_pairing, Request());

        Assert.Equal(2, _cutter.Calls);
        Assert.True(_cutter.Starts[1] > _cutter.Starts[0]);
        Assert.Equal(250, Assert.Single(_uploader.Uploads).Bytes);
    }

    [Fact]
    public async Task ARecordingThatCannotBeTakenOutTwiceIsAnsweredCouldNotCut()
    {
        _source.NothingToCopy = true;

        await Sender().Ask(_pairing, Request());

        Assert.Equal(ClipRequestWire.CouldNotCut, Assert.Single(_uploader.Answers));
        Assert.Equal(2, _source.Copies);
    }

    [Fact]
    public async Task AnUploadThatFailedIsAnsweredUploadFailed()
    {
        _cutter.Results.Enqueue(CutOf(300));
        _uploader.UploadResult = ClipUploadResult.Failed;

        await Sender().Ask(_pairing, Request());

        Assert.Equal(ClipRequestWire.UploadFailed, Assert.Single(_uploader.Answers));
    }

    [Fact]
    public async Task AnUploadTheServerSaysIsNotOpenStopsQuietly()
    {
        _cutter.Results.Enqueue(CutOf(300));
        _uploader.UploadResult = ClipUploadResult.NotOpen;

        await Sender().Ask(_pairing, Request());

        Assert.Empty(_uploader.Answers);
    }

    [Fact]
    public async Task AnAnswerTheServerSaysIsNotOpenIsNotNoted()
    {
        _sendIsOn = false;
        _uploader.AnswerResult = ClipUploadResult.NotOpen;

        await Sender().Ask(_pairing, Request());

        Assert.Empty(_notes);
    }

    [Fact]
    public async Task AnAnswerThatWentIsNotedForTheAuditLogPage()
    {
        _sendIsOn = false;

        await Sender().Ask(_pairing, Request());

        Assert.Single(_notes);
    }

    [Fact]
    public async Task TwoActionsInTheSameRecordingAreAnsweredFromOneCopy()
    {
        _cutter.Results.Enqueue(CutOf(300));
        _cutter.Results.Enqueue(CutOf(310));

        await Sender().Ask(_pairing, Request());
        await Sender().Ask(_pairing, Request());

        Assert.Equal(2, _uploader.Uploads.Count);
        Assert.Equal(1, _source.Copies);
    }

    [Fact]
    public async Task LeftoverFilesFromAnEarlierRunAreDeletedAtStart()
    {
        Directory.CreateDirectory(_folder);
        var leftover = Path.Combine(_folder, "recording-old.mp4");
        await File.WriteAllBytesAsync(leftover, [1, 2, 3], TestContext.Current.CancellationToken);

        Sender();

        Assert.False(File.Exists(leftover));
    }

    private ClipCut CutOf(long bytes) => new(true, TimeSpan.Zero, TimeSpan.FromSeconds(30), bytes, null);

    private static ClipCut CutFailed() => new(false, default, default, 0, "it did not fit");

    private sealed class FakeSource : IClipSource
    {
        public bool Recording { get; set; }
        public long WholeBytes { get; set; }
        public bool NothingToCopy { get; set; }
        public int Copies { get; private set; }
        public List<RecordingSpan> Live { get; } = [];

        public bool IsRecording => Recording;
        public long BitsPerSecond { get; set; } = 1_820_000;
        public IReadOnlyList<RecordingSpan> Spans() => Live.ToList();

        public async Task<SavedRecording?> CopyAsync(string rollingPath, string destination, CancellationToken ct)
        {
            Copies++;
            if (NothingToCopy)
                return null;

            var span = Live.First(s => s.Path == rollingPath);
            await File.WriteAllBytesAsync(destination, new byte[WholeBytes], ct);
            Live.Remove(span);
            return new SavedRecording(destination, span.StartedAt, span.Length, WholeBytes);
        }
    }

    private sealed class FakeCutter : IClipCutter
    {
        public Queue<ClipCut> Results { get; } = new();
        public ClipCut Default { get; set; } = new(false, default, default, 0, "nothing queued");
        public int Calls { get; private set; }
        public List<TimeSpan> Starts { get; } = [];

        public async Task<ClipCut> CutAsync(
            string sourcePath, TimeSpan start, TimeSpan end, long maxBytes, string destinationPath, CancellationToken ct)
        {
            Calls++;
            Starts.Add(start);
            var result = Results.Count > 0 ? Results.Dequeue() : Default;
            if (result.Succeeded)
                await File.WriteAllBytesAsync(destinationPath, new byte[result.Bytes], ct);

            return result;
        }
    }

    private sealed class FakeUploader : IClipUploader
    {
        public List<(Guid Id, long Bytes)> Uploads { get; } = [];
        public List<string> Answers { get; } = [];
        public ClipUploadResult UploadResult { get; set; } = ClipUploadResult.Accepted;
        public ClipUploadResult AnswerResult { get; set; } = ClipUploadResult.Accepted;

        public Task<ClipUploadResult> UploadAsync(ServerPairing pairing, Guid requestId, string path, CancellationToken ct)
        {
            Uploads.Add((requestId, new FileInfo(path).Length));
            return Task.FromResult(UploadResult);
        }

        public Task<ClipUploadResult> AnswerAsync(ServerPairing pairing, Guid requestId, string outcome, CancellationToken ct)
        {
            Answers.Add(outcome);
            return Task.FromResult(AnswerResult);
        }
    }
}
