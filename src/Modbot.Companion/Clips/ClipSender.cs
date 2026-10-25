using Modbot.Companion.Ingest;
using Modbot.Core.Time;
using Modbot.Shared.Clips;

namespace Modbot.Companion.Clips;

/// <summary>
/// Answers each clip request the server makes: checks the switch, waits, takes the right recording,
/// cuts it, sends it, or says why not.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> It reads nothing of VRChat and nothing of the
/// moderator's own clips. It asks the recorder to close one of its two rolling recordings and move
/// it into a folder of its own (<c>clips/sending</c>, inside the companion's data folder), cuts a
/// shorter copy there, and deletes both when they have been sent. Anything left in that folder by an
/// earlier run is deleted when this starts. Nothing is kept for later.</para>
/// <para><strong>What leaves the machine.</strong> Through <see cref="IClipUploader"/>, to the
/// server that asked and nowhere else, one clip (or one word saying why there is none) per request,
/// and only while Send clips for my actions automatically is ticked. A request that has closed is
/// not answered. Each request id is acted on once.</para>
/// <para><strong>Why it asks the recorder to give up a recording.</strong> A recording still being
/// written cannot be cut, so the one that holds the action is closed first. Several actions close
/// together are answered from the same copy while it is kept (15 minutes).</para>
/// </remarks>
public sealed class ClipSender : IDisposable
{
    private const int RememberRequests = 200;

    private static readonly TimeSpan KeepCopies = TimeSpan.FromMinutes(15);

    private readonly IClipSource _source;
    private readonly IClipCutter _cutter;
    private readonly IClipUploader _uploader;
    private readonly IModbotClock _clock;
    private readonly Func<ServerPairing, TimeSpan> _serverOffset;
    private readonly Func<bool> _sendIsOn;
    private readonly Action<string> _note;
    private readonly string _folder;
    private readonly long _maxBytes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _cutting = new(1, 1);
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = new();
    private readonly List<ClosedCopy> _closed = [];

    /// <summary>A recording taken out of the recorder, kept for the next request that falls in it.</summary>
    private sealed class ClosedCopy(SavedRecording saved, DateTimeOffset savedAt)
    {
        public SavedRecording Saved { get; } = saved;
        public DateTimeOffset SavedAt { get; } = savedAt;
        public int Users { get; set; }
    }

    /// <summary>What a request comes to once it is cut: a file to send, or a word to answer with.</summary>
    private sealed record Prepared(string? SendPath, string? Word, string Reason, ClosedCopy? Held, string? CutPath);

    /// <param name="serverOffset">What to add to this PC's clock to get the server's, for one pairing.</param>
    /// <param name="sendIsOn">Whether Send clips for my actions automatically is ticked.</param>
    /// <param name="note">Writes one line to the companion's own Audit Log page.</param>
    /// <param name="folder">Where the copies and cuts are made.</param>
    public ClipSender(
        IClipSource source,
        IClipCutter cutter,
        IClipUploader uploader,
        IModbotClock clock,
        Func<ServerPairing, TimeSpan> serverOffset,
        Func<bool> sendIsOn,
        Action<string> note,
        string folder,
        long maxBytes = ClipSendRules.MaxBytes,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _source = source;
        _cutter = cutter;
        _uploader = uploader;
        _clock = clock;
        _serverOffset = serverOffset;
        _sendIsOn = sendIsOn;
        _note = note;
        _folder = folder;
        _maxBytes = maxBytes;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));

        DeleteLeftovers();
    }

    /// <summary>
    /// Takes one request. Returns at once for one already seen or already past; otherwise the task
    /// ends when the request has been answered. It never throws.
    /// </summary>
    public Task Ask(ServerPairing pairing, ClipRequestMessage request)
    {
        // The server's times, restated on this PC's clock, before anything else is done with them.
        var offset = _serverOffset(pairing);
        var action = request.OccurredAt - offset;
        var expires = request.ExpiresAt - offset;

        lock (_seen)
        {
            if (!_seen.Add(request.RequestId))
                return Task.CompletedTask;

            _seenOrder.Enqueue(request.RequestId);
            while (_seenOrder.Count > RememberRequests)
                _seen.Remove(_seenOrder.Dequeue());
        }

        if (Past(expires))
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                await RunAsync(pairing, request, action, expires, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _note($"A clip of the {request.Action} was asked for, but something went wrong and none was sent: {ex.Message}");
            }
        });
    }

    private bool Past(DateTimeOffset expires) => _clock.UtcNow >= expires;

    private bool CanSend => _sendIsOn() && _source.IsRecording;

    private async Task RunAsync(
        ServerPairing pairing, ClipRequestMessage request, DateTimeOffset action, DateTimeOffset expires, CancellationToken ct)
    {
        if (!CanSend)
        {
            await AnswerAsync(pairing, request, ClipRequestWire.ClipsOff, NotSending(), expires, ct).ConfigureAwait(false);
            return;
        }

        var wait = ClipSendRules.WaitUntil(action, expires) - _clock.UtcNow;
        if (wait > TimeSpan.Zero)
            await _delay(wait, ct).ConfigureAwait(false);

        if (Past(expires))
            return;

        if (!CanSend)
        {
            await AnswerAsync(pairing, request, ClipRequestWire.ClipsOff, NotSending(), expires, ct).ConfigureAwait(false);
            return;
        }

        Prepared prepared;
        await _cutting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            prepared = await PrepareAsync(request, action, ct).ConfigureAwait(false);
        }
        finally
        {
            _cutting.Release();
        }

        try
        {
            if (prepared.SendPath is { } path)
                await SendAsync(pairing, request, path, expires, ct).ConfigureAwait(false);
            else
                await AnswerAsync(pairing, request, prepared.Word!, prepared.Reason, expires, ct).ConfigureAwait(false);
        }
        finally
        {
            if (prepared.Held is { } held)
            {
                lock (_closed)
                    held.Users--;
            }

            Delete(prepared.CutPath);
        }
    }

    private string NotSending() => _sendIsOn()
        ? "Clips was not recording"
        : "Send clips for my actions automatically is off";

    /// <summary>Takes the right recording out of the recorder, cuts it and decides what goes.</summary>
    private async Task<Prepared> PrepareAsync(ClipRequestMessage request, DateTimeOffset action, CancellationToken ct)
    {
        ForgetOldCopies();

        ClosedCopy? copy = null;
        ClipPlan? plan = null;

        // Twice, because the recording that held the action can have been closed and started again
        // between looking and asking.
        for (var attempt = 0; attempt < 2 && copy is null; attempt++)
        {
            plan = ClipSendRules.Plan(action, Candidates(), _source.BitsPerSecond, _maxBytes);
            if (plan is null)
                return new Prepared(null, ClipRequestWire.ClipsOff, $"the {request.Action} was not in the recording", null, null);

            copy = FindCopy(plan.Span.Path);
            if (copy is not null)
                break;

            var destination = Path.Combine(_folder, $"recording-{Guid.NewGuid():N}.mp4");
            Directory.CreateDirectory(_folder);

            var saved = await _source.CopyAsync(plan.Span.Path, destination, ct).ConfigureAwait(false);
            if (saved is null)
                continue;

            copy = new ClosedCopy(saved, _clock.UtcNow);
            lock (_closed)
                _closed.Add(copy);
        }

        if (copy is null)
            return new Prepared(null, ClipRequestWire.CouldNotCut, "the recording could not be taken out of the recorder", null, null);

        lock (_closed)
            copy.Users++;

        // Planned again on the finished file, which can run a little past what the recorder said.
        var whole = new RecordingSpan(copy.Saved.Path, copy.Saved.StartedAt, copy.Saved.Length);
        plan = ClipSendRules.Plan(action, [whole], _source.BitsPerSecond, _maxBytes);
        if (plan is null)
            return new Prepared(null, ClipRequestWire.CouldNotCut, "the action was not in the recording", copy, null);

        var cutPath = Path.Combine(_folder, $"clip-{request.RequestId:N}.mp4");
        ClipCut cut;
        try
        {
            cut = await _cutter.CutAsync(copy.Saved.Path, plan.Start, plan.End, _maxBytes, cutPath, ct).ConfigureAwait(false);

            // The cutter fails rather than write more than the limit, so a cut that did not fit
            // gets one more go, starting later.
            if (!cut.Succeeded && plan.Later() is { } later)
            {
                Delete(cutPath);
                cut = await _cutter.CutAsync(copy.Saved.Path, later.Start, later.End, _maxBytes, cutPath, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cut = new ClipCut(false, default, default, 0, ex.Message);
        }

        switch (ClipSendRules.AfterCut(cut.Succeeded, cut.Bytes, copy.Saved.Bytes, _maxBytes))
        {
            case ClipNext.UploadCut:
                return new Prepared(cutPath, null, string.Empty, copy, cutPath);

            case ClipNext.UploadWhole:
                Delete(cutPath);
                return new Prepared(copy.Saved.Path, null, string.Empty, copy, null);

            default:
                Delete(cutPath);
                return new Prepared(null, ClipRequestWire.CouldNotCut, "it could not be cut and the whole recording is too big to send", copy, null);
        }
    }

    private async Task SendAsync(
        ServerPairing pairing, ClipRequestMessage request, string path, DateTimeOffset expires, CancellationToken ct)
    {
        using var upload = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var left = expires - _clock.UtcNow;
        upload.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);

        ClipUploadResult result;
        try
        {
            result = await _uploader.UploadAsync(pairing, request.RequestId, path, upload.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The request closed while the clip was going. Nothing may be sent after that.
            return;
        }

        switch (result)
        {
            case ClipUploadResult.Accepted:
                _note($"Sent a clip of the {request.Action} to {pairing.OverlayLabel}.");
                break;

            case ClipUploadResult.Failed:
                await AnswerAsync(pairing, request, ClipRequestWire.UploadFailed, "it could not be sent", expires, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Answers with a word, once, and writes the reason to the Audit Log page.</summary>
    private async Task AnswerAsync(
        ServerPairing pairing, ClipRequestMessage request, string word, string reason, DateTimeOffset expires, CancellationToken ct)
    {
        if (Past(expires))
            return;

        var result = await _uploader.AnswerAsync(pairing, request.RequestId, word, ct).ConfigureAwait(false);
        if (result is ClipUploadResult.NotOpen)
            return;

        _note($"A clip of the {request.Action} was asked for by {pairing.OverlayLabel}, but none was sent: {reason}.");
    }

    /// <summary>The recordings being written, and the copies already taken out.</summary>
    private List<RecordingSpan> Candidates()
    {
        var spans = new List<RecordingSpan>(_source.Spans());

        lock (_closed)
        {
            spans.AddRange(_closed.Select(c => new RecordingSpan(c.Saved.Path, c.Saved.StartedAt, c.Saved.Length)));
        }

        return spans;
    }

    private ClosedCopy? FindCopy(string path)
    {
        lock (_closed)
        {
            var found = _closed.FirstOrDefault(c => c.Saved.Path == path);
            if (found is not null && !File.Exists(found.Saved.Path))
            {
                _closed.Remove(found);
                return null;
            }

            return found;
        }
    }

    private void ForgetOldCopies()
    {
        List<ClosedCopy> old;
        lock (_closed)
        {
            old = _closed.Where(c => c.Users == 0 && _clock.UtcNow - c.SavedAt > KeepCopies).ToList();
            foreach (var c in old)
                _closed.Remove(c);
        }

        foreach (var c in old)
            Delete(c.Saved.Path);
    }

    private void DeleteLeftovers()
    {
        try
        {
            if (!Directory.Exists(_folder))
                return;

            foreach (var file in Directory.EnumerateFiles(_folder))
                Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Delete(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start, which clears the folder.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();

        lock (_closed)
        {
            foreach (var c in _closed)
                Delete(c.Saved.Path);

            _closed.Clear();
        }

        _stop.Dispose();
    }
}
