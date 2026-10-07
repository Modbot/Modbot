using System.Collections.Concurrent;

namespace Modbot.Discord.Gateway;

/// <summary>
/// Remembers when a command last answered in public in each channel, so a second public answer
/// inside the command's window is made private instead (Discord commands design §3.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Decided before the command is acknowledged.</strong> Discord fixes who sees a reply
/// when the interaction is acknowledged, so the gateway asks here first and acknowledges as public
/// only when this says yes. Asking counts: the first public run in a channel takes the window, and
/// a run that arrives while it is taken is private.
/// </para>
/// <para>
/// Kept in memory, per process, like the member command limits: a restart forgets it, which at
/// worst allows one more public answer.
/// </para>
/// </remarks>
public sealed class PublicReplyWindow
{
    /// <summary>How many channels are remembered before the ones past their window are forgotten.</summary>
    private const int MaxRemembered = 5_000;

    private readonly ConcurrentDictionary<(string Command, ulong Channel), DateTimeOffset> _last = new();

    /// <summary>
    /// Whether this run may answer in public: true when the command has not answered in public in
    /// this channel within <paramref name="window"/>, and then this run takes the window. False
    /// leaves the window as it was, so waiting it out always works.
    /// </summary>
    public bool TryTake(string command, ulong channelId, DateTimeOffset now, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (_last.Count > MaxRemembered)
            Forget(now, window);

        var key = (command, channelId);

        while (true)
        {
            if (!_last.TryGetValue(key, out var before))
            {
                if (_last.TryAdd(key, now))
                    return true;

                continue;
            }

            if (now - before < window)
                return false;

            if (_last.TryUpdate(key, now, before))
                return true;
        }
    }

    private void Forget(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (key, at) in _last)
        {
            if (now - at >= window)
                _last.TryRemove(new KeyValuePair<(string Command, ulong Channel), DateTimeOffset>(key, at));
        }
    }
}
