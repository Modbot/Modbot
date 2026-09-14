namespace Modbot.Core.Data.Entities;

/// <summary>
/// How big Modbot's data was on one UTC day. One row per day, and never more than one.
/// </summary>
/// <remarks>
/// <para>
/// Recorded because PostgreSQL keeps no record of what a table used to occupy: without these rows
/// the Data settings page could show today's size and an estimate, but never the year behind it.
/// </para>
/// <para>
/// Days before the first row cannot be filled in. A deployment that predates this table starts its
/// history on the day it was upgraded, and that is expected rather than a gap to repair.
/// </para>
/// </remarks>
public class StorageDay
{
    /// <summary>The UTC day, like every other day key in Modbot.</summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// The size the Data settings page shows as the database: every fact partition and the daily
    /// totals, indexes included. The same figure, so the recorded line meets today's value.
    /// </summary>
    public long Bytes { get; set; }

    /// <summary>Rows in the fact log, as the storage measurement counted them that day.</summary>
    public long Facts { get; set; }
}
