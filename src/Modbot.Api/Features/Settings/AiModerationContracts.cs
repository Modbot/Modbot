namespace Modbot.Api.Features.Settings;

/// <summary>
/// How often a rule has flagged, how many of those a moderator dismissed and how many were
/// confirmed (M8 §4.4, AI moderation design §19).
/// </summary>
/// <param name="ByLanguage">
/// The same numbers for each language the rule has flagged. M8 §4.4 says false positives cluster
/// in text that is not English, so one figure for the whole rule hides the problem it warns about.
/// </param>
public sealed record RuleStats(
    int Flags,
    int Dismissed,
    int Confirmed = 0,
    IReadOnlyList<RuleLanguageStats>? ByLanguage = null);

/// <param name="Language">The ISO 639-3 code, or null when the language could not be told.</param>
public sealed record RuleLanguageStats(string? Language, string Label, int Flags, int Dismissed, int Confirmed);

/// <summary>Where a rule runs and who it never acts on (AI moderation design §13.3).</summary>
/// <param name="ChannelMode"><c>all</c>, <c>only</c> or <c>except</c>.</param>
public sealed record RuleScope(
    string ChannelMode,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> ExemptRoles,
    bool ExemptRolesSkipFlag);

/// <summary>A running trial: what the rule would have done, and how much of it was dismissed (design §13.1).</summary>
public sealed record RuleTrial(
    DateTimeOffset StartedAt,
    int Days,
    DateTimeOffset EndsAt,
    int Flags,
    int WouldDelete,
    int WouldTimeOut,
    int Dismissed);

/// <summary>A rule that stopped itself (design §13.2).</summary>
public sealed record RulePause(DateTimeOffset At, string? Reason);

/// <summary>The rule's test set and its newest run (design §12).</summary>
/// <param name="Passes">A run exists for the rule as it stands now, with nothing wrongly flagged.</param>
public sealed record RuleTestSummary(
    int Samples,
    DateTimeOffset? LastRunAt,
    string? LastRunModel,
    int? Caught,
    int? ShouldFlag,
    int? WronglyFlagged,
    bool Passes);

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
    RuleStats Stats,
    int Version,
    bool Acting,
    RuleScope Scope,
    RuleTrial? Trial,
    RulePause? Paused,
    RuleTestSummary Tests,
    int ContextMessages = 0,
    bool CheckPictures = false,
    bool OpenReviewForEachFlag = false);

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
    RuleStats Stats,
    int Version,
    bool Acting,
    RuleScope Scope,
    RuleTrial? Trial,
    RulePause? Paused,
    RuleTestSummary Tests,
    int ContextMessages = 0,
    bool CheckPictures = false,
    bool OpenReviewForEachFlag = false);

/// <param name="AiReady">Whether AI base settings are on and complete, so AI topics can run at all.</param>
/// <param name="PicturesAvailable">
/// Whether the model in use reads pictures, as the model list last said (AI moderation design §17).
/// False makes the "Check pictures" box unavailable rather than letting it fail at run time.
/// </param>
/// <param name="ContextChoices">The amounts of context a rule may be set to.</param>
public sealed record AiModerationResponse(
    bool Enabled,
    int DailyAiCallLimit,
    int AiCallsToday,
    bool AiReady,
    IReadOnlyList<TermListView> Lists,
    IReadOnlyList<TopicView> Topics,
    bool PicturesAvailable = false,
    IReadOnlyList<int>? ContextChoices = null);

public sealed record AiModerationUpdate(bool Enabled, int DailyAiCallLimit);

/// <param name="Id">An existing term's id, to keep it; null for a new term.</param>
/// <param name="Kind"><c>word</c>, <c>contains</c> or <c>regex</c>.</param>
/// <param name="Text">The word or phrase, or the pattern for <c>regex</c>.</param>
public sealed record TermInput(string? Id, string Kind, string Text);

/// <param name="Name">Ignored for a Hub list.</param>
/// <param name="Terms">Ignored for a Hub list. Null keeps the terms as they are.</param>
/// <param name="ExcludedTerms">Hub lists only: the ids of terms to switch off. Null keeps them as they are.</param>
/// <param name="Scope">Null keeps the scope as it is.</param>
/// <param name="TrialDays">How long the trial should run when this switches the rule to acting. Null means 7.</param>
/// <param name="ActWithoutTest">
/// Switch the rule to acting without a passing test run. Recorded as a fact naming the operator
/// (AI moderation design §12.4).
/// </param>
public sealed record TermListInput(
    string? Name,
    bool Enabled,
    IReadOnlyList<string>? Targets,
    bool DeleteMessage,
    int? TimeoutMinutes,
    IReadOnlyList<TermInput>? Terms,
    IReadOnlyList<string>? ExcludedTerms,
    RuleScope? Scope = null,
    int? TrialDays = null,
    bool ActWithoutTest = false,
    int? ContextMessages = null,
    bool CheckPictures = false,
    bool OpenReviewForEachFlag = false);

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
    int? TimeoutMinutes,
    RuleScope? Scope = null,
    int? TrialDays = null,
    bool ActWithoutTest = false,
    int? ContextMessages = null,
    bool CheckPictures = false,
    bool OpenReviewForEachFlag = false);

// ── Test sets (AI moderation design §12) ────────────────────────────────────────────────────

public sealed record TestSampleView(Guid Id, string Text, bool ShouldFlag, string? Note, string Target, bool Seeded);

public sealed record TestSampleInput(string? Text, bool ShouldFlag, string? Note, string? Target);

/// <param name="Flagged">Whether the rule flagged it on the run.</param>
/// <param name="Matched">The words that matched, or the model's quote.</param>
/// <param name="Reason">The Hub note for a term, or the model's sentence for a topic.</param>
public sealed record TestRunSampleView(
    Guid SampleId,
    string Text,
    bool ShouldFlag,
    string? Note,
    string Target,
    bool Flagged,
    string? Term,
    string? Matched,
    string? Reason);

public sealed record TestRunView(
    Guid Id,
    DateTimeOffset RanAt,
    string? Model,
    int RuleVersion,
    int Samples,
    int ShouldFlagCount,
    int Caught,
    int Missed,
    int ShouldNotFlagCount,
    int WronglyFlagged,
    string? AiSkipped,
    string? RanBy,
    IReadOnlyList<TestRunSampleView> Results);

public sealed record RuleTestsResponse(
    string RuleKind,
    Guid RuleId,
    string RuleName,
    int RuleVersion,
    bool Acting,
    IReadOnlyList<TestSampleView> Samples,
    IReadOnlyList<TestRunView> Runs);

/// <summary>One version of a rule's text (AI moderation design §14).</summary>
public sealed record RuleVersionView(
    int Version,
    DateTimeOffset ChangedAt,
    string? ChangedBy,
    string Name,
    string Text);

public sealed record RuleVersionList(string RuleKind, Guid RuleId, IReadOnlyList<RuleVersionView> Versions);

/// <param name="Days">How long the trial should run. Null keeps what the rule has.</param>
public sealed record TrialInput(int? Days);

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

/// <param name="CallId">The AI call behind it, in the call log. Null when no AI call was made.</param>
public sealed record TryResponse(
    IReadOnlyList<TryMatchView> Matches,
    bool WouldDeleteMessage,
    int? WouldTimeOutMinutes,
    string? AiSkipped,
    Guid? CallId = null);
