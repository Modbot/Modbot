namespace Modbot.Analytics.Activity;

/// <summary>
/// How many readings a member-count peak rests on.
/// </summary>
/// <remarks>
/// <para>
/// The group-info sync reads VRChat's member count and online member count about every five
/// minutes and keeps every reading, so a window Modbot was running through has a reading on every
/// day of it. A window it was not — the deployment is younger than the range, the sync was down, or
/// an operator's presence retention window has deleted the older readings — has gaps, and the
/// highest number inside a gap-ridden window is the highest Modbot happened to see rather than the
/// highest there was.
/// </para>
/// <para>
/// Counted in days rather than in readings, because that is the unit the gap shows up in: a sync
/// that ran for an hour a day would be a strange fault, while a sync that started last Tuesday is
/// the ordinary case this is for.
/// </para>
/// </remarks>
/// <param name="WindowDays">Days in the window asked for.</param>
/// <param name="DaysWithReadings">Days in the window with at least one reading.</param>
/// <param name="Readings">Readings in the window.</param>
public sealed record MemberCountCoverage(int WindowDays, int DaysWithReadings, int Readings)
{
    /// <summary>No readings at all — what an empty window comes back as.</summary>
    public static MemberCountCoverage Nothing { get; } = new(0, 0, 0);

    /// <summary>Below this share of the window's days carrying a reading, the peak is called thin.</summary>
    public const decimal ThinBelow = 0.5m;

    /// <summary>Whether fewer than half the window's days carry a reading.</summary>
    public bool Thin => WindowDays > 0 && DaysWithReadings < WindowDays * ThinBelow;
}
