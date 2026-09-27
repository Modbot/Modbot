namespace Modbot.Core.Data.Entities;

/// <summary>
/// One read of a world's page while the group had an instance open in it: how many people were in
/// the world, and the world's own list of its instances with a head count each. The table is
/// <c>world_head_count</c>.
/// </summary>
/// <remarks>
/// <para>
/// What lets an instance's popup say how it compared with the other instances in the same world at
/// the time (spec 2026-09-27-instance-against-its-world-design.md). The group's own head counts
/// (<see cref="InstanceHeadCount"/>) only ever cover the group's instances; this is the only record
/// Modbot keeps of anybody else's.
/// </para>
/// <para>
/// <strong>Kept as VRChat sent it.</strong> <see cref="Instances"/> is the page's <c>instances</c>
/// array exactly as it arrived, not taken apart into rows. Which instances VRChat puts in that list
/// had not been checked against a live response when this was built (2026-09-27), so the whole
/// thing is kept and read apart when a popup asks, rather than being cut down to what was guessed
/// to matter.
/// </para>
/// <para>
/// A row per read, not per change: the list's counts move on almost every read of a busy world, so
/// keeping only changes would save nothing.
/// </para>
/// </remarks>
public class WorldHeadCount
{
    public long Id { get; set; }

    /// <summary>The world, as VRChat's opaque id.</summary>
    public string WorldId { get; set; } = string.Empty;

    /// <summary>When the page was read.</summary>
    public DateTimeOffset CountedAt { get; set; }

    /// <summary>The page's <c>occupants</c>: everyone in the world. Null when the body had none.</summary>
    public int? Occupants { get; set; }

    /// <summary>The page's <c>publicOccupants</c>. Null when the body had none.</summary>
    public int? PublicOccupants { get; set; }

    /// <summary>The page's <c>privateOccupants</c>. Null when the body had none.</summary>
    public int? PrivateOccupants { get; set; }

    /// <summary>
    /// The page's <c>instances</c> array as it arrived -- <c>[["16354~region(eu)", 12], …]</c>, an
    /// instance id and a head count each. Null when the body had no such array.
    /// </summary>
    public string? Instances { get; set; }
}
