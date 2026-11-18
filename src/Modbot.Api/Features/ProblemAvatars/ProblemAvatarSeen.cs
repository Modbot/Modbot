namespace Modbot.Api.Features.ProblemAvatars;

/// <summary>
/// Remembers which person-and-avatar pairs have already raised an alert in an instance, so one
/// person wearing one problem avatar alerts once there.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it is needed on top of the fact log's own de-duplication.</strong> The log keeps one
/// fact per report, and a person already in the instance is reported again by every moderator who
/// walks in afterwards. Each of those is a new fact that names the same avatar, and each would sound
/// the same bleep for the same person.
/// </para>
/// <para>
/// <strong>In memory, on purpose.</strong> An alert is about a moment (see <see cref="Modbot.Api.Features.Companion.Alerts.AlertHub"/>),
/// so a restart that lets one repeat once is cheaper than a table whose only job is to stop that.
/// Entries older than <see cref="Keep"/> are dropped as new ones arrive, so the list cannot grow
/// without end.
/// </para>
/// </remarks>
public sealed class ProblemAvatarSeen
{
    /// <summary>How long a pair is remembered. Longer than any instance a moderator sits in.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromHours(12);

    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Notes the pair, and says whether it is the first time.
    /// </summary>
    /// <param name="nameKey">The avatar's <see cref="Modbot.Core.Data.Entities.ProblemAvatar.NameKey"/>.</param>
    /// <returns>True when this pair has not alerted here within <see cref="Keep"/>.</returns>
    public bool TryMark(string subjectId, string nameKey, string? worldId, string instanceId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(nameKey);
        ArgumentNullException.ThrowIfNull(instanceId);

        // A control character cannot be in a user id, an avatar name or an instance id, so the key
        // cannot run two of them together.
        var key = $"{subjectId}\u0001{nameKey}\u0001{worldId}\u0001{instanceId}";

        lock (_gate)
        {
            foreach (var old in _seen.Where(p => now - p.Value > Keep).Select(p => p.Key).ToList())
                _seen.Remove(old);

            return _seen.TryAdd(key, now);
        }
    }
}
