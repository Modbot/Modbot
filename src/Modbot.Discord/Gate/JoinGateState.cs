namespace Modbot.Discord.Gate;

/// <summary>
/// What the join gate keeps between passes, shared by the pass, the buttons and the API's actions
/// (join gate design §6 and §10).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One change to the gate's rows at a time.</strong> A button press, a moderator's Let in
/// and the minute pass can all reach the same person at once; without the lock the pass could see
/// the member role a press had just given and close the row as "let in in Discord".
/// </para>
/// <para>
/// <strong>Whether the bot could give the member role at its last try</strong> decides whether time
/// counts against the people waiting: somebody who cannot get in because Modbot cannot give the role
/// is not to be removed for it. Kept in memory: after a restart the next try finds out again.
/// </para>
/// </remarks>
public sealed class JoinGateState
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Why the last try to give the member role failed, or null when it worked or nobody tried.</summary>
    public string? RoleProblem { get; private set; }

    /// <summary>When <see cref="RoleProblem"/> was last seen.</summary>
    public DateTimeOffset? RoleProblemAt { get; private set; }

    public void RoleRefused(string why, DateTimeOffset at)
    {
        RoleProblem = why;
        RoleProblemAt = at;
    }

    public void ClearRoleProblem()
    {
        RoleProblem = null;
        RoleProblemAt = null;
    }

    /// <summary>When the gate message was last rewritten or checked, so a deleted one is noticed within the hour.</summary>
    public DateTimeOffset? MessageCheckedAt { get; set; }

    public async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        return new Release(_lock);
    }

    /// <summary>Asks for a pass now: somebody joined, linked, or a moderator lifted the hold.</summary>
    public void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake is all that is wanted.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);

    private sealed class Release(SemaphoreSlim held) : IDisposable
    {
        private SemaphoreSlim? _held = held;

        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
    }
}
