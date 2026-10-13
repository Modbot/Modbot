namespace Modbot.Api.Features.Partners;

/// <summary>A partner as staff fill it in.</summary>
/// <param name="Name">The group's name. Left empty, it is read from what Modbot already knows of <paramref name="GroupId"/>.</param>
/// <param name="GroupId">VRChat's id for the group, as typed. Never checked against an id format.</param>
/// <param name="Status"><c>active</c>, <c>paused</c> or <c>ended</c>. Left out, <c>active</c>.</param>
/// <param name="Envoy">Who represents us to them, as typed.</param>
/// <param name="Contacts">Who to talk to in their group, one entry each, as typed.</param>
/// <param name="LastCollabOn">The day of the last thing done together.</param>
/// <param name="LastCollabNote">What it was.</param>
public sealed record PartnerRequest(
    string? Name,
    string? GroupId,
    string? Status,
    string? Envoy,
    IReadOnlyList<string>? Contacts,
    DateOnly? LastCollabOn,
    string? LastCollabNote,
    string? Notes);

/// <summary>A partner.</summary>
public sealed record PartnerView(
    Guid Id,
    string Name,
    string? GroupId,
    string Status,
    string? Envoy,
    IReadOnlyList<string> Contacts,
    DateOnly? LastCollabOn,
    string? LastCollabNote,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Every partner, the most recent collab first.</summary>
public sealed record PartnersView(IReadOnlyList<PartnerView> Partners);
