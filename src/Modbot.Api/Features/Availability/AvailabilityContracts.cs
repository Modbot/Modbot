namespace Modbot.Api.Features.Availability;

/// <summary>One hour of somebody's week in which they are free.</summary>
/// <param name="Day">0 to 6, Monday first.</param>
/// <param name="Hour">0 to 23: the hour that starts at this time, on the person's own clock.</param>
/// <param name="State"><c>free</c> or <c>ifNeeded</c>.</param>
public sealed record AvailabilityCell(int Day, int Hour, string State);

/// <summary>What a person has saved for their own week.</summary>
/// <param name="TimeZone">The IANA zone the cells are in. Null until they have saved once.</param>
/// <param name="Cells">The hours they are free. An hour that is not listed is not free.</param>
/// <param name="SavedAt">When they last saved. Null until they have.</param>
public sealed record MyAvailabilityView(
    string? TimeZone,
    IReadOnlyList<AvailabilityCell> Cells,
    DateTimeOffset? SavedAt);

/// <summary>A person's whole week, as they send it. It replaces what was saved.</summary>
/// <param name="TimeZone">An IANA zone, such as <c>Europe/London</c>. Required.</param>
/// <param name="Cells">At most 168, one for each hour at most.</param>
public sealed record MyAvailabilityRequest(string? TimeZone, IReadOnlyList<AvailabilityCell>? Cells);

/// <summary>One person on the team's list and their week.</summary>
/// <param name="Id">The Modbot account's id.</param>
/// <param name="Name">The account's username.</param>
/// <param name="Roles">The names of the roles they hold, highest first.</param>
/// <param name="TimeZone">The zone their cells are in. Null when they have not saved a week yet.</param>
public sealed record TeamAvailabilityPerson(
    Guid Id,
    string Name,
    IReadOnlyList<string> Roles,
    string? TimeZone,
    IReadOnlyList<AvailabilityCell> Cells);

/// <summary>Everyone who can enter their times, and the hours each is free.</summary>
public sealed record TeamAvailabilityView(IReadOnlyList<TeamAvailabilityPerson> People);
