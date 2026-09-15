namespace Modbot.Core.Discord;

/// <summary>
/// Wakes the Discord role job early: a link was saved or ended, or a member joined.
/// </summary>
/// <remarks>
/// A singleton shared by the API, which saves links, and the bot, which gives the roles. The job
/// runs once a minute regardless, so a missed wake only means a minute's wait; this is what makes
/// the role appear while the member is still looking at the page that said "Linked".
/// </remarks>
public sealed class DiscordLinkSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Something changed. Many calls before the job wakes count as one.</summary>
    public void Changed()
    {
        // The count is at most one: a second wake while one is pending adds nothing.
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
    /// <returns>True when woken by a change.</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}
