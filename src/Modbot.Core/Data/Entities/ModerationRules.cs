namespace Modbot.Core.Data.Entities;

/// <summary>Where a term list came from.</summary>
public static class TermListSource
{
    /// <summary>Written by the operator on this deployment.</summary>
    public const string Local = "local";

    /// <summary>Subscribed from Modbot Hub. Read-only here, apart from switching terms off.</summary>
    public const string Cloud = "cloud";
}

/// <summary>The two kinds of rule, as stored on a flag.</summary>
public static class ModerationRuleKind
{
    public const string TermList = "termList";

    public const string Topic = "topic";

    public static bool IsKind(string? kind)
        => string.Equals(kind, TermList, StringComparison.Ordinal) || string.Equals(kind, Topic, StringComparison.Ordinal);
}

/// <summary>
/// What a rule and a version row share, so the engine and the endpoints can hold either kind of
/// rule without knowing which one it is.
/// </summary>
public interface IModerationRule
{
    Guid Id { get; }

    string Name { get; }

    bool Enabled { get; }

    /// <summary>A <c>ModerationTargets</c> value.</summary>
    int Targets { get; }

    bool DeleteMessage { get; }

    int? TimeoutMinutes { get; }

    /// <summary>Ban the person from the managed VRChat group (AutoMod design §5). Profile targets only.</summary>
    bool GroupBan { get; }

    /// <summary>Remove the person from the managed VRChat group (AutoMod design §5). Profile targets only.</summary>
    bool GroupRemove { get; }

    /// <summary>The rule's text version (AI moderation design §14).</summary>
    int Version { get; }

    Guid? ActSetByUserId { get; }

    string? ActSetByUsername { get; }

    DateTimeOffset? ActSetAt { get; }

    DateTimeOffset? TrialStartedAt { get; }

    int TrialDays { get; }

    DateTimeOffset? TrialEndedAt { get; }

    string? TrialEndedByUsername { get; }

    DateTimeOffset? PausedAt { get; }

    string? PausedReason { get; }

    /// <summary><see cref="ChannelScope"/>.</summary>
    string ChannelMode { get; }

    /// <summary>Channel ids, as a JSON array.</summary>
    string Channels { get; }

    /// <summary>Discord role ids whose members are never acted on, as a JSON array.</summary>
    string ExemptRoles { get; }

    /// <summary>Exempt members are not flagged either.</summary>
    bool ExemptRolesSkipFlag { get; }

    /// <summary>
    /// How many messages before the checked one go to the model as context
    /// (AI moderation design §16). One of <see cref="ContextMessageCounts"/>.
    /// </summary>
    int ContextMessages { get; }

    /// <summary>The rule also checks pictures (AI moderation design §17).</summary>
    bool CheckPictures { get; }

    /// <summary>Every flag this rule raises opens a review (AI moderation design §19).</summary>
    bool OpenReviewForEachFlag { get; }

    DateTimeOffset UpdatedAt { get; }
}

/// <summary>How much of the conversation a rule sends with a message (AI moderation design §16).</summary>
public static class ContextMessageCounts
{
    /// <summary>What a rule starts on: the five messages before the one being checked.</summary>
    public const int Default = 5;

    /// <summary>The counts the settings page offers. Zero is "none".</summary>
    public static IReadOnlyList<int> All { get; } = [0, 3, 5, 10];

    public static bool IsCount(int count) => All.Contains(count);
}

/// <summary>Which Discord channels a rule runs in (AI moderation design §13.3).</summary>
public static class ChannelScope
{
    /// <summary>Every channel.</summary>
    public const string All = "all";

    /// <summary>Only the channels listed.</summary>
    public const string Only = "only";

    /// <summary>Every channel but the ones listed.</summary>
    public const string Except = "except";

    public static bool IsMode(string? mode)
        => mode is All or Only or Except;
}

/// <summary>A term list (AI moderation design §2).</summary>
public class ModerationTermList : IModerationRule
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="TermListSource.Local"/> or <see cref="TermListSource.Cloud"/>.</summary>
    public string Source { get; set; } = TermListSource.Local;

    public bool Enabled { get; set; }

    /// <summary>A <c>ModerationTargets</c> value.</summary>
    public int Targets { get; set; }

    /// <summary>
    /// The terms, as a JSON array. A list is read and written whole: it is edited as one form, and
    /// matching loads every term of every enabled list anyway.
    /// </summary>
    public string Terms { get; set; } = "[]";

    /// <summary>Ids of terms switched off, as a JSON array. Survives a Hub update, because Hub ids are stable.</summary>
    public string ExcludedTerms { get; set; } = "[]";

    public bool DeleteMessage { get; set; }

    public int? TimeoutMinutes { get; set; }

    public bool GroupBan { get; set; }

    public bool GroupRemove { get; set; }

    /// <summary>Who set this list to act, and when. Null while it only flags.</summary>
    public Guid? ActSetByUserId { get; set; }

    public string? ActSetByUsername { get; set; }

    public DateTimeOffset? ActSetAt { get; set; }

    /// <summary>The rule's text version (AI moderation design §14). Starts at 1.</summary>
    public int Version { get; set; } = 1;

    // ── Trial, pause and scope (AI moderation design §13) ───────────────────────────────────

    public DateTimeOffset? TrialStartedAt { get; set; }

    public int TrialDays { get; set; } = 7;

    public DateTimeOffset? TrialEndedAt { get; set; }

    public Guid? TrialEndedByUserId { get; set; }

    public string? TrialEndedByUsername { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    public string? PausedReason { get; set; }

    public string ChannelMode { get; set; } = ChannelScope.All;

    public string Channels { get; set; } = "[]";

    public string ExemptRoles { get; set; } = "[]";

    public bool ExemptRolesSkipFlag { get; set; }

    /// <summary>How many messages before the checked one go to the model (AI moderation design §16).</summary>
    public int ContextMessages { get; set; } = ContextMessageCounts.Default;

    /// <summary>The rule also checks pictures (AI moderation design §17).</summary>
    public bool CheckPictures { get; set; }

    /// <summary>Every flag this rule raises opens a review (AI moderation design §19).</summary>
    public bool OpenReviewForEachFlag { get; set; }

    // ── Cloud lists only ────────────────────────────────────────────────────────────────────

    /// <summary>The Hub's id for the list, e.g. <c>modbot_harassment_terms</c>.</summary>
    public string? HubId { get; set; }

    public string? HubVersion { get; set; }

    public DateTimeOffset? HubFetchedAt { get; set; }

    /// <summary>A newer version, fetched and waiting for the operator to apply it (foundation §4.2.7).</summary>
    public string? HubAvailableVersion { get; set; }

    /// <summary>The waiting version's terms, already converted, as a JSON array.</summary>
    public string? HubAvailableTerms { get; set; }

    /// <summary>How many terms the waiting version adds, removes and changes, as JSON.</summary>
    public string? HubAvailableChanges { get; set; }

    /// <summary>Why the last fetch failed, or null when it worked.</summary>
    public string? HubError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An AI topic: a name and what to catch, in the operator's words (AI moderation design §2).</summary>
public class ModerationTopic : IModerationRule
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;

    /// <summary><c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    public string Sensitivity { get; set; } = "medium";

    public bool Enabled { get; set; }

    public int Targets { get; set; }

    public bool DeleteMessage { get; set; }

    public int? TimeoutMinutes { get; set; }

    public bool GroupBan { get; set; }

    public bool GroupRemove { get; set; }

    public Guid? ActSetByUserId { get; set; }

    public string? ActSetByUsername { get; set; }

    public DateTimeOffset? ActSetAt { get; set; }

    /// <summary>The rule's text version (AI moderation design §14). Starts at 1.</summary>
    public int Version { get; set; } = 1;

    // ── Trial, pause and scope (AI moderation design §13) ───────────────────────────────────

    public DateTimeOffset? TrialStartedAt { get; set; }

    public int TrialDays { get; set; } = 7;

    public DateTimeOffset? TrialEndedAt { get; set; }

    public Guid? TrialEndedByUserId { get; set; }

    public string? TrialEndedByUsername { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    public string? PausedReason { get; set; }

    public string ChannelMode { get; set; } = ChannelScope.All;

    public string Channels { get; set; } = "[]";

    public string ExemptRoles { get; set; } = "[]";

    public bool ExemptRolesSkipFlag { get; set; }

    /// <summary>How many messages before the checked one go to the model (AI moderation design §16).</summary>
    public int ContextMessages { get; set; } = ContextMessageCounts.Default;

    /// <summary>The rule also checks pictures (AI moderation design §17).</summary>
    public bool CheckPictures { get; set; }

    /// <summary>Every flag this rule raises opens a review (AI moderation design §19).</summary>
    public bool OpenReviewForEachFlag { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What the AI may say about a flag, and what it may propose (AutoMod design §6.3).</summary>
public static class FlagOpinions
{
    public const string Keep = "keep";

    public const string Dismiss = "dismiss";

    public static bool IsOpinion(string? value) => value is Keep or Dismiss;

    /// <summary>The actions the model may propose. Nothing else it names is stored.</summary>
    public const string NoAction = "none";

    public const string DeleteMessage = "delete_message";

    public const string TimeOut = "timeout";

    public const string GroupBan = "group_ban";

    public const string GroupRemove = "group_remove";

    public static bool IsAction(string? value) => value is NoAction or DeleteMessage or TimeOut or GroupBan or GroupRemove;
}

/// <summary><para><strong>Persisted as smallint. Never renumber a member.</strong></para></summary>
public enum ModerationFlagState : short
{
    Open = 1,
    Dismissed = 2,

    /// <summary>A moderator closed the flag's review as right (AI moderation design §19).</summary>
    Confirmed = 3,
}

/// <summary>
/// One flag: a rule matched something a person wrote (AI moderation design §5).
/// </summary>
/// <remarks>
/// The row is the working copy moderators page through and dismiss; the facts written alongside it
/// are the record. Dismissal is the one change ever made to a row, and it writes its own fact.
/// </remarks>
public class ModerationFlag
{
    public Guid Id { get; set; }

    public DateTimeOffset FlaggedAt { get; set; }

    /// <summary><see cref="ModerationRuleKind"/>.</summary>
    public string RuleKind { get; set; } = string.Empty;

    public Guid RuleId { get; set; }

    /// <summary>The rule's name when it flagged. The rule may have been renamed or deleted since.</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>The rule version that flagged (AI moderation design §14), so the rule text as it was then can be shown.</summary>
    public int RuleVersion { get; set; }

    /// <summary>Which term in the list. Empty for a topic, so the dismissal lookup has one shape.</summary>
    public string TermKey { get; set; } = string.Empty;

    public string Term { get; set; } = string.Empty;

    /// <summary>One target name, e.g. <c>discordMessage</c>.</summary>
    public string Target { get; set; } = string.Empty;

    public FactPlatform SubjectPlatform { get; set; }

    public string SubjectId { get; set; } = string.Empty;

    public string? SubjectName { get; set; }

    public string? ChannelId { get; set; }

    public string? MessageId { get; set; }

    public string Matched { get; set; } = string.Empty;

    public string? Reason { get; set; }

    /// <summary>
    /// The language of the text that was checked, as an ISO 639-3 code, or null when the detector
    /// could not tell (AI moderation design §18). M8 §4.4: false positives cluster in text that is
    /// not English, and a rule's dismissal rate only shows that if the language is on the flag.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// The ids of the messages sent to the model as context, oldest first, as a JSON array
    /// (AI moderation design §16). Empty for a term list, a profile check and a rule with no
    /// context. Kept so a moderator can see what the model saw.
    /// </summary>
    public string ContextMessageIds { get; set; } = "[]";

    /// <summary>
    /// Which picture matched, in plain words — "Attachment cat.png", "Discord avatar", "VRChat
    /// profile picture" (AI moderation design §17). Null when the text matched.
    /// </summary>
    public string? Picture { get; set; }

    /// <summary>Where that picture was, so a moderator can open it. Null when the text matched.</summary>
    public string? PictureUrl { get; set; }

    /// <summary>The review opened for this flag, or null when none was (AI moderation design §19).</summary>
    public Guid? ReviewId { get; set; }

    public bool MessageDeleted { get; set; }

    public int? TimedOutMinutes { get; set; }

    /// <summary>The person was banned from the managed VRChat group by this rule (AutoMod design §5).</summary>
    public bool GroupBanned { get; set; }

    /// <summary>The person was removed from the managed VRChat group by this rule (AutoMod design §5).</summary>
    public bool GroupRemoved { get; set; }

    /// <summary>The rule was in its trial, so nothing was done (AI moderation design §13.1).</summary>
    public bool Trial { get; set; }

    /// <summary>What the rule would have done, while it is in its trial or paused.</summary>
    public bool WouldDeleteMessage { get; set; }

    public int? WouldTimeOutMinutes { get; set; }

    public bool WouldGroupBan { get; set; }

    public bool WouldGroupRemove { get; set; }

    // ── The AI's opinion, when a moderator asked for one (AutoMod design §6.3) ──────────────

    /// <summary><see cref="FlagOpinions.Keep"/> or <see cref="FlagOpinions.Dismiss"/>. Null until somebody asks.</summary>
    public string? AiOpinion { get; set; }

    /// <summary>The model's sentence for its opinion.</summary>
    public string? AiOpinionReason { get; set; }

    /// <summary>
    /// What the model proposed a moderator might do, one of <see cref="FlagOpinions"/>'s actions.
    /// Advice only: nothing here is ever carried out by Modbot. Null when the proposal tool is off.
    /// </summary>
    public string? AiProposedAction { get; set; }

    public DateTimeOffset? AiOpinionAt { get; set; }

    /// <summary>The call that gave the opinion, in the call log.</summary>
    public Guid? AiOpinionCallId { get; set; }

    /// <summary>
    /// The AI call that produced this flag, in <see cref="AiCall"/>. Null for a term list, which
    /// makes no call.
    /// </summary>
    public Guid? CallId { get; set; }

    public ModerationFlagState State { get; set; } = ModerationFlagState.Open;

    public DateTimeOffset? DismissedAt { get; set; }

    public Guid? DismissedByUserId { get; set; }

    public string? DismissedByUsername { get; set; }

    /// <summary>When a moderator closed the flag's review as right (AI moderation design §19).</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    public Guid? ConfirmedByUserId { get; set; }

    public string? ConfirmedByUsername { get; set; }
}
