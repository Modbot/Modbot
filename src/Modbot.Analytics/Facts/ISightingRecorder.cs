namespace Modbot.Analytics.Facts;

/// <summary>
/// Told about each fact as the writer records it, so the people the fact names count as seen
/// the moment it lands rather than when a later pass reads it back out of the log.
/// </summary>
/// <remarks>
/// <para>
/// Why the writer tells somebody instead of doing it: which facts name a person, and where their
/// row lives, is the profile sync's knowledge (<c>UserSightings</c>, <c>vrchat_user</c>), and
/// that code depends on this project. The writer therefore takes the recorder as an optional
/// dependency and a host that has no profile sync (a bare analytics host, a test) simply has no
/// recorder.
/// </para>
/// <para>
/// Called only for a fact that was actually inserted, never for a deduplicated report: the first
/// report already counted, and a duplicate is not a second sighting.
/// </para>
/// </remarks>
public interface ISightingRecorder
{
    /// <summary>Marks whoever this fact names as seen at the fact's own time.</summary>
    Task RecordAsync(FactRecord fact, CancellationToken ct = default);
}
