namespace Modbot.Core.Data.Entities;

/// <summary>How free a person is in one hour of their week.</summary>
public static class AvailabilityStates
{
    /// <summary>Free in this hour.</summary>
    public const string Free = "free";

    /// <summary>Could do it if needed. Counted as free only when the person looking asks for it.</summary>
    public const string IfNeeded = "ifNeeded";

    /// <summary>Whether this is one of the two states a stored hour can be. A free hour is no row at all.</summary>
    public static bool IsKnown(string? state) => state is Free or IfNeeded;
}

/// <summary>
/// One hour of one person's week in which they are free or free if needed. The table is
/// <c>staff_availability</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No row means not free.</strong> Only the hours a person marked are stored, at most
/// <see cref="StaffAvailabilityRules.WeekHours"/> per person.
/// </para>
/// <para>
/// <strong>In the person's own time zone</strong> (<see cref="StaffAvailabilityZone"/>), as a day of
/// the week and an hour of the day, and never as an instant. "Tuesdays 18:00 to 22:00" is a habit of
/// the person's own clock, and it keeps meaning that when their clock goes forward or back; whoever
/// is looking converts it for the dates they are looking at (availability design §4).
/// </para>
/// </remarks>
public class StaffAvailability
{
    /// <summary>The Modbot account. The rows go when the account does.</summary>
    public Guid UserId { get; set; }

    /// <summary>Monday is 0 and Sunday is 6.</summary>
    public int Day { get; set; }

    /// <summary>The hour that starts at this time, 0 to 23, on the person's own clock.</summary>
    public int Hour { get; set; }

    /// <summary>One of <see cref="AvailabilityStates"/>.</summary>
    public string State { get; set; } = AvailabilityStates.Free;
}

/// <summary>
/// The time zone a person's <see cref="StaffAvailability"/> hours are in. One row per account; the
/// table is <c>staff_availability_zone</c>.
/// </summary>
public class StaffAvailabilityZone
{
    /// <summary>The Modbot account. The row goes when the account does.</summary>
    public Guid UserId { get; set; }

    /// <summary>An IANA zone name, such as <c>Europe/London</c>.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>When the person last saved their week, from <c>IModbotClock</c>.</summary>
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>What a person's week may hold.</summary>
public static class StaffAvailabilityRules
{
    /// <summary>Seven days of twenty-four hours.</summary>
    public const int WeekHours = 7 * 24;

    /// <summary>The longest zone name kept. Checked against the zone database by <c>CalendarRepeat.FindZone</c>.</summary>
    public const int MaxZoneLength = 64;
}
