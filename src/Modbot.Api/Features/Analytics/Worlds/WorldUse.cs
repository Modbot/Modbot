namespace Modbot.Api.Features.Analytics.Worlds;

/// <summary>One instance of a world, as far as "was the world used" needs it.</summary>
/// <param name="OpenedAt">When Modbot first saw it open.</param>
/// <param name="ClosedAt">When it ended, or null while it is still open.</param>
/// <param name="Peak">The most people VRChat counted in it at once, or null before any count.</param>
/// <param name="PeakUnsure">True when that peak rests only on counts that could not tell people apart.</param>
public sealed record WorldInstance(DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, int? Peak, bool PeakUnsure);

/// <summary>
/// How much one world was used, from its instances alone.
/// </summary>
/// <remarks>
/// <para>
/// Every number here comes from <c>vrchat_instance</c>, which the group sync keeps for every group
/// instance whether or not anybody from the team was in it. That is what makes it the answer to
/// "which worlds get used": a world with no companion in it still shows how long it was open and
/// how full it got.
/// </para>
/// <para>
/// An instance's length follows <c>InstanceRows</c> exactly (to its close, or to now while open), so
/// the world's time open and the lengths the Instances page lists for it add up. Two instances of the
/// same world open at once count their shared time once: "time open" is time the world had at least
/// one instance open, not a sum of instance lengths.
/// </para>
/// </remarks>
/// <param name="Instances">Instances opened in the window.</param>
/// <param name="MinutesOpen">Minutes with at least one of them open, overlaps counted once.</param>
/// <param name="MostAtOnce">The highest peak of any of them, or null when none was ever counted.</param>
/// <param name="MostAtOnceUnsure">True when every instance that reached that peak reached it on unsure counts only.</param>
/// <param name="LastOpenedAt">When the newest of them opened, or null when there are none.</param>
public sealed record WorldUse(
    int Instances,
    decimal MinutesOpen,
    int? MostAtOnce,
    bool MostAtOnceUnsure,
    DateTimeOffset? LastOpenedAt)
{
    public static readonly WorldUse Nothing = new(0, 0m, null, false, null);

    /// <param name="now">From <c>IModbotClock</c>. An open instance runs to it, never past it.</param>
    public static WorldUse Of(IReadOnlyCollection<WorldInstance> instances, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (instances.Count == 0) return Nothing;

        var minutes = 0m;
        DateTimeOffset? runStart = null;
        var runEnd = DateTimeOffset.MinValue;

        foreach (var i in instances.OrderBy(i => i.OpenedAt))
        {
            var end = i.ClosedAt ?? now;
            if (end < i.OpenedAt) end = i.OpenedAt;

            if (runStart is null || i.OpenedAt > runEnd)
            {
                if (runStart is not null) minutes += (decimal)(runEnd - runStart.Value).TotalMinutes;
                runStart = i.OpenedAt;
                runEnd = end;
            }
            else if (end > runEnd)
            {
                runEnd = end;
            }
        }

        minutes += (decimal)(runEnd - runStart!.Value).TotalMinutes;

        var counted = instances.Where(i => i.Peak is not null).ToList();
        int? most = counted.Count == 0 ? null : counted.Max(i => i.Peak);
        var unsure = most is not null && counted.Where(i => i.Peak == most).All(i => i.PeakUnsure);

        return new WorldUse(
            instances.Count,
            Math.Round(minutes, 1),
            most,
            unsure,
            instances.Max(i => i.OpenedAt));
    }
}
