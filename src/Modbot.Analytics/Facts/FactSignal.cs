namespace Modbot.Analytics.Facts;

/// <summary>
/// Tells whoever is waiting for new facts -- live event connections, long polls -- that the log may
/// have grown (API keys design §5.5).
/// </summary>
/// <remarks>
/// <para>
/// A signal, never the facts themselves: a waiter takes <see cref="Next"/> <em>before</em> it reads
/// the log, reads, and waits on the task only if there was nothing to send. A pulse between the read
/// and the wait has already completed that task, so nothing is lost to the race.
/// </para>
/// <para>
/// <see cref="FactWriter"/> pulses after each insert. That can be before the insert's transaction
/// commits, when a reader woken by it still sees nothing; the fact feed watcher in the API pulses
/// again when the newest committed id moves, and waiters also re-read every few seconds regardless.
/// A pulse is only ever a reason to look.
/// </para>
/// </remarks>
public sealed class FactSignal
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A task that completes at the next pulse.</summary>
    public Task Next() => Volatile.Read(ref _next).Task;

    public void Pulse()
    {
        var fired = Interlocked.Exchange(
            ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        fired.TrySetResult();
    }
}
