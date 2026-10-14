namespace Modbot.Core.Supporters;

/// <summary>
/// Wakes the supporter role job early: a payment came in, or staff linked a supporter.
/// </summary>
/// <remarks>
/// A singleton shared by the API, which takes in payments, and the bot, which gives the roles. The
/// job runs every minute regardless, so a missed wake only means a minute's wait.
/// </remarks>
public sealed class SupporterSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Something changed. Many calls before the job wakes count as one.</summary>
    public void Changed()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another caller released it first; one pending wake is all that is wanted.
        }
    }

    /// <summary>Waits for <see cref="Changed"/> or for the timeout, whichever comes first.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}
