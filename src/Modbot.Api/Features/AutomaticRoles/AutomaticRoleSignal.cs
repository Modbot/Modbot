namespace Modbot.Api.Features.AutomaticRoles;

/// <summary>
/// Wakes the automatic roles job when a companion reports something it reads: a connection opening
/// or closing, new Clips settings, or a move to another instance.
/// </summary>
/// <remarks>
/// A nudge, not the news: it carries nothing, and the job reads everything it needs from the
/// database and the companions' state when it runs. Many nudges before the job looks count as one.
/// In memory, like <see cref="Companion.Live.CompanionConnections"/>; a restart loses at most one
/// nudge, and the job also runs on its own timer.
/// </remarks>
public sealed class AutomaticRoleSignal
{
    private readonly SemaphoreSlim _woken = new(0, 1);

    /// <summary>Tells the job that something it reads changed.</summary>
    public void Tell()
    {
        try
        {
            _woken.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already told and not yet looked at: one wake-up is enough.
        }
    }

    /// <summary>Completes when told, or after <paramref name="timeout"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
        => await _woken.WaitAsync(timeout, ct).ConfigureAwait(false);
}
