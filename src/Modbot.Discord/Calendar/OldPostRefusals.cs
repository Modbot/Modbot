using System.Collections.Concurrent;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Old calendar posts Discord refused to let the bot delete, and when each may be asked about
/// again. A refusal (403) is not "gone": the bot may be given the permission back, so the post is
/// kept and tried again, but only once a day so a refused post does not cost a call every twenty
/// seconds or crowd out the others.
/// </summary>
/// <remarks>
/// Kept in memory, not in the database: after a restart each refused post is asked about once
/// more, which is the right thing to do after an operator may have changed the bot's role.
/// A singleton, so the next pass sees what this one learned.
/// </remarks>
public sealed class OldPostRefusals
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    /// <summary>Do not ask about this post before <paramref name="until"/>.</summary>
    public void Hold(string messageId, DateTimeOffset until) => _until[messageId] = until;

    /// <summary>The ids of the posts still on hold at <paramref name="now"/>. Ones whose time has come are dropped.</summary>
    public List<string> OnHold(DateTimeOffset now)
    {
        var held = new List<string>();

        foreach (var (id, until) in _until)
        {
            if (until > now)
                held.Add(id);
            else
                _until.TryRemove(new KeyValuePair<string, DateTimeOffset>(id, until));
        }

        return held;
    }

    /// <summary>The post is done with, so nothing is kept about it.</summary>
    public void Forget(string messageId) => _until.TryRemove(messageId, out _);
}
