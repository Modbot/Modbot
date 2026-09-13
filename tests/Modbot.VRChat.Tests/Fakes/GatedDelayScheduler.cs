using System.Threading.Channels;
using Modbot.VRChat.RateLimiting;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// A delay a test holds open, so a producer's loop can be stepped one tick at a time.
/// </summary>
/// <remarks>
/// <see cref="TestDelayScheduler"/> turns a wait into a clock advance, which is what the limiter's
/// tests need; it also makes a <c>BackgroundService</c> spin as fast as the scheduler can dispatch
/// it. A producer loop has to be stepped instead: stop at the wait, let the test change something,
/// then let exactly one tick through — otherwise "the change reached the running producer" is a
/// race rather than an assertion.
/// </remarks>
public sealed class GatedDelayScheduler : IDelayScheduler
{
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();

    private Request? _held;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
    {
        var request = new Request(delay);

        // A producer shutting down cancels mid-wait; without this the loop would never notice.
        _ = ct.Register(() => request.Gate.TrySetCanceled(ct));

        _requests.Writer.TryWrite(request);

        return request.Gate.Task;
    }

    /// <summary>
    /// Waits until the producer asks for its next delay, and holds it there.
    /// </summary>
    /// <remarks>
    /// Holding rather than releasing is the point: it is the only moment at which a test knows
    /// the loop is not running, and therefore the only safe moment to change a setting and say
    /// what the producer did about it.
    /// </remarks>
    public async Task<TimeSpan> HoldNextAsync(CancellationToken ct)
    {
        _held = await _requests.Reader
            .ReadAsync(ct)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30), ct);

        return _held.Delay;
    }

    /// <summary>Lets the held tick through.</summary>
    public void Release()
    {
        var held = _held ?? throw new InvalidOperationException("No delay is being held.");

        _held = null;
        held.Gate.TrySetResult();
    }

    private sealed class Request(TimeSpan delay)
    {
        public TimeSpan Delay { get; } = delay;

        public TaskCompletionSource Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
