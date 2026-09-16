namespace Modbot.Core.Cloud;

/// <summary>
/// A nudge from whatever noticed a room open or close, so the report is sent then rather than at
/// the end of the next wait.
/// </summary>
/// <remarks>
/// <para>
/// A schedule on its own would be either slow or wasteful: a group's event would appear on
/// modbot.co minutes after it opened, or the report would be sent every few seconds to a Cloud that
/// almost always has nothing new to hear. So the report goes on a slow schedule and the group
/// instance poll pokes it whenever the list actually changed.
/// </para>
/// <para>
/// One nudge is one wake-up, not a queue: several rooms opening in the same poll are one send.
/// </para>
/// </remarks>
public sealed class PublicRoomsNudge : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Asks for a send as soon as the loop can. Never blocks and never throws.</summary>
    public void Poke()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Two pokes at once; one wake-up is what was wanted anyway.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Waits for a nudge or for <paramref name="upTo"/> to pass, whichever comes first.
    /// </summary>
    public async Task WaitAsync(TimeSpan upTo, CancellationToken ct)
    {
        try
        {
            await _wake.WaitAsync(upTo, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller checks the token.
        }
    }

    public void Dispose() => _wake.Dispose();
}
