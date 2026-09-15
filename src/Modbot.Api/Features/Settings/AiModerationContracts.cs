namespace Modbot.Api.Features.Settings;

/// <summary>How often a rule has flagged, and how many of those a moderator dismissed (M8 §4.4).</summary>
public sealed record RuleStats(int Flags, int Dismissed);

public sealed record TermListView(
    Guid Id,
    string Name,
    string Source,
    bool Enabled,
    IReadOnlyList<string> Targets,
    bool DeleteMessage,
    int? TimeoutMinutes,
    string? SetToActBy,
    DateTimeOffset? SetToActAt,
    int TermCount,
    int ExcludedCount,
    string? HubId,
    string? HubVersion,
    DateTimeOffset? HubFetchedAt,
    string? HubAvailableVersion,
    Modbot.AI.Moderation.HubListChanges? HubAvailableChanges,
    string? HubError,
    RuleStats Stats);

/// <param name="Label">The term as a moderator reads it: the words, the pattern, or the combination.</param>
/// <param name="Excluded">Switched off on this deployment.</param>
public sealed record TermView(
    string Id,
    string Kind,
    string? Text,
    string? Pattern,
    string Label,
    string? Category,
    string? Note,
    bool Excluded);

public sealed record TermListDetail(TermListView List, IReadOnlyList<TermView> Terms);

public sealed record TopicView(
    Guid Id,
    string Name,
    string Instructions,
    string Sensitivity,
    bool Enabled,
    IReadOnlyList<string> Targets,
    bool DeleteMessage,
    int? TimeoutMinutes,
    string? SetToActBy,
    DateTimeOffset? SetToActAt,
    RuleStats Stats);

/// <param name="AiReady">Whether AI base settings are on and complete, so AI topics can run at all.</param>
public sealed record AiModerationResponse(
    bool Enabled,
    int DailyAiCallLimit,
    int AiCallsToday,
    bool AiReady,
    IReadOnlyList<TermListView> Lists,
    IReadOnlyList<TopicView> Topics);

public sealed record AiModerationUpdate(bool Enabled, int DailyAiCallLimit);

/// <param name="Id">An existing term's id, to keep it; null for a new term.</param>
/// <param name="Kind"><c>word</c>, <c>contains</c> or <c>regex</c>.</param>
/// <param name="Text">The word or phrase, or the pattern for <c>regex</c>.</param>
public sealed record TermInput(string? Id, string Kind, string Text);

/// <param name="Name">Ignored for a Hub list.</param>
/// <param name="Terms">Ignored for a Hub list. Null keeps the terms as they are.</param>
/// <param name="ExcludedTerms">Hub lists only: the ids of terms to switch off. Null keeps them as they are.</param>
public sealed record TermListInput(
    string? Name,
    bool Enabled,
    IReadOnlyList<string>? Targets,
    bool DeleteMessage,
    int? TimeoutMinutes,
    IReadOnlyList<TermInput>? Terms,
    IReadOnlyList<string>? ExcludedTerms);

public sealed record HubSubscribe(string HubId);

public sealed record HubListView(
    string Id,
    string Name,
    string? Description,
    string? Version,
    int RuleCount,
    IReadOnlyList<string> SuitableFor,
    bool Subscribed);

public sealed record HubIndexResponse(IReadOnlyList<HubListView> Lists, string? Error);

public sealed record TopicInput(
    string? Name,
    string? Instructions,
    string? Sensitivity,
    bool Enabled,
    IReadOnlyList<string>? Targets,
    bool DeleteMessage,
    int? TimeoutMinutes);

public sealed record TryRequest(string? Text, string? Target, bool IncludeAi);

public sealed record TryMatchView(
    string RuleKind,
    Guid RuleId,
    string RuleName,
    bool RuleEnabled,
    string Term,
    string Matched,
    string? Reason,
    bool DeleteMessage,
    int? TimeoutMinutes);

public sealed record TryResponse(
    IReadOnlyList<TryMatchView> Matches,
    bool WouldDeleteMessage,
    int? WouldTimeOutMinutes,
    string? AiSkipped);
