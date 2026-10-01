using System.Text.Json.Nodes;

namespace Modbot.Api.Features.Lists;

/// <summary>A saved list as a person fills it in: a name and the rules (lists design §2).</summary>
/// <param name="Rules">The rule tree, in the shape the giveaway rule builder reads and writes.</param>
public sealed record ListRequest(string Name, JsonNode? Rules);

/// <summary>A rule tree on its own, to see who it lets through before saving anything.</summary>
public sealed record ListPreviewRequest(JsonNode? Rules);

/// <summary>Which file to export a list's people as.</summary>
/// <param name="Format"><c>csv</c> or <c>json</c>.</param>
public sealed record ListExportRequest(string? Format);

/// <summary>What would change if a list changed.</summary>
/// <param name="Giveaways">Giveaways still being run that name the list among their rules.</param>
/// <param name="AutoInvites">Auto-invites name the list among their rules.</param>
public sealed record ListUseView(IReadOnlyList<string> Giveaways, bool AutoInvites);

/// <param name="RuleLines">The rules in plain words, one line each.</param>
/// <param name="CreatedBy">The username of the account that made it, while that account exists.</param>
public sealed record ListView(
    Guid Id,
    string Name,
    JsonNode? Rules,
    IReadOnlyList<string> RuleLines,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ListUseView UsedBy);

/// <param name="CanManage">Whether the person asking may make, change and delete lists.</param>
public sealed record ListsView(
    IReadOnlyList<ListView> Lists,
    bool CanManage);

/// <summary>One person in a list.</summary>
/// <param name="Key">How Modbot names them: <c>vrchat:usr_…</c>, or <c>discord:…</c> with no VRChat account.</param>
/// <param name="FromPolledData">A rule about them was answered from polled presence reports, so it is close rather than exact.</param>
/// <param name="CloseCall">A measurement of theirs sat within a whisker of a threshold.</param>
public sealed record ListPersonView(
    string Key,
    string? VRChatUserId,
    string? DiscordUserId,
    string? Name,
    bool InGroup,
    bool InDiscord,
    bool Linked,
    bool FromPolledData,
    bool CloseCall);

/// <summary>Who is in a list right now, a page at a time.</summary>
/// <param name="Count">How many people are in it.</param>
/// <param name="Considered">How many people were looked at.</param>
/// <param name="CloseCalls">How many of the people in it sat within a whisker of a threshold.</param>
/// <param name="FromPolledData">Any rule here was answered from polled presence reports.</param>
/// <param name="Unanswerable">Why Modbot cannot answer. Everything else is empty when this is set.</param>
/// <param name="Stopped">More people than Modbot looks at in one go.</param>
/// <param name="CountedAt">When this answer was worked out. A list is asked again every time.</param>
public sealed record ListPeopleView(
    int Count,
    int Considered,
    int CloseCalls,
    bool FromPolledData,
    string? Unanswerable,
    bool Stopped,
    DateTimeOffset CountedAt,
    IReadOnlyList<ListPersonView> People,
    int Page,
    int PageSize);

/// <summary>What the list builder offers: the rule kinds, the trust ranks and the roles.</summary>
/// <remarks>
/// The same names as the giveaway builder's, so one rule builder reads either. There is no
/// weighting here, and no "in a list" rule: a list cannot use another list.
/// </remarks>
public sealed record ListBuilderView(
    IReadOnlyList<string> RuleKinds,
    IReadOnlyList<string> Weightings,
    IReadOnlyList<string> TrustRanks,
    IReadOnlyList<ListRoleView> GroupRoles,
    IReadOnlyList<ListRoleView> DiscordRoles,
    int ModerationFactRetentionDays,
    int PresenceFactRetentionDays);

/// <summary>A role a rule can name, or a list a rule can name.</summary>
public sealed record ListRoleView(string Id, string Name);
