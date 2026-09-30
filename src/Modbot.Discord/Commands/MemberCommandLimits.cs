using System.Collections.Concurrent;

namespace Modbot.Discord.Commands;

/// <summary>
/// How often one Discord account may use <c>/me</c> and its buttons: <see cref="PerMinute"/> in
/// any minute (Discord /me design §6).
/// </summary>
/// <remarks>
/// <para>
/// <c>/me</c> is the one command every member of the server can run, and each run reads the
/// database and writes an access record. Discord already slows a person who clicks very fast, but
/// not enough to keep somebody from filling the audit log, so Modbot counts too.
/// </para>
/// <para>
/// Kept in memory, per process. A restart forgets it, which is fine: the point is a person holding
/// a key down, not a determined attacker, and the deletion requests carry their own limits in the
/// database.
/// </para>
/// </remarks>
public sealed class MemberCommandLimits
{
    public const int PerMinute = 5;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>How many accounts are remembered before the oldest are forgotten.</summary>
    private const int MaxRemembered = 10_000;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _uses = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts one use for <paramref name="discordUserId"/> and says whether it is allowed. A use
    /// that is refused is not counted, so waiting a minute always works.
    /// </summary>
    public bool TryUse(string discordUserId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        if (_uses.Count > MaxRemembered)
            Forget(now);

        var uses = _uses.GetOrAdd(discordUserId, _ => new Queue<DateTimeOffset>());

        lock (uses)
        {
            while (uses.Count > 0 && now - uses.Peek() >= Window)
                uses.Dequeue();

            if (uses.Count >= PerMinute)
                return false;

            uses.Enqueue(now);
            return true;
        }
    }

    /// <summary>Drops every account with no use in the last minute.</summary>
    private void Forget(DateTimeOffset now)
    {
        foreach (var (id, uses) in _uses)
        {
            lock (uses)
            {
                if (uses.Count == 0 || now - uses.Last() >= Window)
                    _uses.TryRemove(id, out _);
            }
        }
    }
}
