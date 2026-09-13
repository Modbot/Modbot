namespace Modbot.VRChat.RateLimiting;

/// <summary>
/// A mutual-exclusion gate that hands the next turn to the highest-priority waiter.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.1 describes this as a <c>SemaphoreSlim(1)</c>, and one call at a time is exactly the
/// behaviour. <see cref="SemaphoreSlim"/> cannot provide the other half, though: it releases
/// waiters in arrival order, and spec 4.1 also requires interactive moderation actions to preempt
/// queued background sync. A moderator's ban must not wait behind a member page that was queued
/// first, so turn order is chosen on release rather than on arrival.
/// </para>
/// <para>
/// Ownership is transferred on release rather than re-contended for, which is what makes the
/// priority decision stick: if the gate merely reopened, whichever task the scheduler happened to
/// wake first would win and the ordering would be advisory.
/// </para>
/// </remarks>
internal sealed class PriorityGate
{
    private readonly Lock _sync = new();
    private readonly List<Waiter> _waiters = [];
    private bool _held;
    private long _sequence;

    public async Task<IDisposable> EnterAsync(VRChatCallPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_sync)
        {
            if (!_held)
            {
                _held = true;
                return new Releaser(this);
            }

            waiter = new Waiter(priority, _sequence++);
            _waiters.Add(waiter);
        }

        await using var registration = ct.Register(static state =>
        {
            var w = (Waiter)state!;
            w.Completion.TrySetCanceled();
        }, waiter).ConfigureAwait(false);

        try
        {
            await waiter.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation and the grant can race, but only one of them wins the completion
            // source. Arriving here means cancellation won, so Release saw its TrySetResult fail
            // and moved on to another waiter -- the gate is not left held by a task that gave up.
            lock (_sync)
            {
                _waiters.Remove(waiter);
            }

            throw;
        }

        return new Releaser(this);
    }

    private void Release()
    {
        while (true)
        {
            Waiter? next;
            lock (_sync)
            {
                if (_waiters.Count == 0)
                {
                    _held = false;
                    return;
                }

                next = _waiters[0];
                for (var i = 1; i < _waiters.Count; i++)
                {
                    var candidate = _waiters[i];
                    if (candidate.Priority > next.Priority ||
                        (candidate.Priority == next.Priority && candidate.Sequence < next.Sequence))
                    {
                        next = candidate;
                    }
                }

                _waiters.Remove(next);
            }

            // Ownership stays taken; it is now this waiter's. If it has already given up, loop
            // and pick another rather than leaving the gate held by nobody.
            if (next.Completion.TrySetResult())
                return;
        }
    }

    private sealed class Waiter(VRChatCallPriority priority, long sequence)
    {
        public VRChatCallPriority Priority { get; } = priority;

        public long Sequence { get; } = sequence;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Releaser(PriorityGate gate) : IDisposable
    {
        private PriorityGate? _gate = gate;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _gate, null);
            owner?.Release();
        }
    }
}
