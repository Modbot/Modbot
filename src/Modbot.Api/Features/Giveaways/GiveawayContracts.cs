using System.Text.Json.Nodes;

namespace Modbot.Api.Features.Giveaways;

/// <summary>A giveaway as a person fills it in (giveaways design §3).</summary>
/// <param name="OpensAt">When entries open, as an instant: <c>2026-09-20T19:00:00Z</c>.</param>
/// <param name="ClosesAt">When entries close.</param>
/// <param name="DrawAt">When Modbot draws by itself, or null to draw by hand.</param>
/// <param name="EntryWay"><c>automatic</c> or <c>react</c>.</param>
/// <param name="Emoji">What people react with, for <c>react</c>.</param>
/// <param name="Rules">The rule tree. See the rule kinds in the giveaways docs.</param>
/// <param name="Exclusions">Who is kept out, and why.</param>
/// <param name="Weighting"><c>uniform</c>, <c>instanceHours</c>, <c>voiceHours</c>, <c>messages</c> or <c>daysSeen</c>.</param>
/// <param name="WeightCap">The most weight one person may hold, or null for no cap.</param>
/// <param name="Draft">Save without opening it or posting anything.</param>
public sealed record GiveawayRequest(
    string Name,
    string? Prize,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    DateTimeOffset? DrawAt,
    int WinnerCount,
    string? EntryWay,
    string? Emoji,
    JsonNode? Rules,
    JsonNode? Exclusions,
    string? Weighting,
    long? WeightCap,
    bool PostToChannel,
    string? ChannelId,
    bool Draft);

/// <summary>A rule tree on its own, to see how many people it matches before saving anything.</summary>
public sealed record GiveawayPreviewRequest(
    JsonNode? Rules,
    JsonNode? Exclusions,
    string? Weighting,
    long? WeightCap,
    string? EntryWay);

/// <summary>Where the giveaway's Discord post is, and how that went.</summary>
/// <param name="State"><c>waiting</c>, <c>published</c>, <c>failed</c> or <c>removed</c>.</param>
public sealed record GiveawayPostView(
    string State,
    string? ChannelId,
    string? Error,
    DateTimeOffset? ErrorAt,
    DateTimeOffset UpdatedAt);

/// <summary>One person in a draw's frozen entrant list, or in a preview.</summary>
/// <param name="Weight">A whole number. Zero for anybody who could not win.</param>
/// <param name="Measured">The number the weight was counted from, before rounding and the cap.</param>
/// <param name="KeptOut">Empty when they were in the draw, else why they were not.</param>
/// <param name="Because">The rule they failed, in plain words.</param>
/// <param name="FromPolledData">Their figures came from polled presence reports, so they are close rather than exact.</param>
/// <param name="CloseCall">A measurement of theirs sat within a whisker of a threshold.</param>
/// <param name="Purged">They asked to be erased. Their place and weight stay; their name does not.</param>
public sealed record GiveawayEntrantView(
    int Position,
    string Key,
    string? VRChatUserId,
    string? DiscordUserId,
    string? Name,
    long Weight,
    decimal Measured,
    string KeptOut,
    string KeptOutLabel,
    string? Because,
    bool FromPolledData,
    bool CloseCall,
    int? WinnerRank,
    bool Purged);

/// <summary>One draw, with everything needed to work it out again.</summary>
/// <param name="Seed">The seed, in the open once the draw is made.</param>
/// <param name="SeedPromise">The hash published before it: SHA-256 of <paramref name="Seed"/>.</param>
/// <param name="SeedKept">Whether the seed matches the promise. Checked here so nobody has to.</param>
/// <param name="RuleLines">The rules as they stood, in plain words.</param>
public sealed record GiveawayDrawView(
    string Id,
    int Number,
    DateTimeOffset DrawnAt,
    string? DrawnBy,
    string Seed,
    string SeedPromise,
    bool SeedKept,
    int WinnerCount,
    IReadOnlyList<string> RuleLines,
    IReadOnlyList<string> Exclusions,
    string Weighting,
    long? WeightCap,
    int EntrantCount,
    int InDrawCount,
    long TotalWeight,
    bool FromPolledData,
    int CloseCalls,
    IReadOnlyList<GiveawayEntrantView> Winners);

/// <param name="RuleLines">The rules in plain words, one line each.</param>
/// <param name="SeedPromise">The promise standing for the next draw.</param>
/// <param name="EntryCount">How many people have reacted and not withdrawn.</param>
public sealed record GiveawayView(
    Guid Id,
    string Name,
    string Prize,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    DateTimeOffset? DrawAt,
    int WinnerCount,
    string EntryWay,
    string Emoji,
    JsonNode? Rules,
    IReadOnlyList<string> RuleLines,
    JsonNode? Exclusions,
    IReadOnlyList<string> ExclusionLines,
    string Weighting,
    long? WeightCap,
    bool PostToChannel,
    string? ChannelId,
    string State,
    string SeedPromise,
    int DrawCount,
    int EntryCount,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? ClosedAt,
    GiveawayPostView? Post,
    IReadOnlyList<GiveawayDrawView> Draws);

/// <param name="CanRun">Whether the person asking may create, edit and draw.</param>
public sealed record GiveawayListView(
    IReadOnlyList<GiveawayView> Giveaways,
    bool CanRun,
    DateTimeOffset Now);

/// <summary>What a rule tree matches right now.</summary>
/// <param name="Total">How many people were looked at.</param>
/// <param name="InDraw">How many of them would be in the hat.</param>
/// <param name="CloseCalls">How many sat within a whisker of a threshold on an approximate rule.</param>
/// <param name="FromPolledData">Any figure here came from polled presence reports.</param>
/// <param name="Unanswerable">Why Modbot cannot answer. Everything else is empty when this is set.</param>
public sealed record GiveawayPreviewView(
    int Total,
    int InDraw,
    long TotalWeight,
    int CloseCalls,
    bool FromPolledData,
    string? Unanswerable,
    bool Stopped,
    IReadOnlyList<GiveawayEntrantView> People);

/// <summary>One page of a draw's frozen entrant list.</summary>
public sealed record GiveawayEntrantsView(
    IReadOnlyList<GiveawayEntrantView> People,
    int Total,
    int Page,
    int PageSize);

/// <summary>A role the rule builder can pick, on either side.</summary>
public sealed record GiveawayRoleView(string Id, string Name);

/// <summary>What the rule builder offers: the rule kinds, the weightings, and the roles.</summary>
public sealed record GiveawayBuilderView(
    IReadOnlyList<string> RuleKinds,
    IReadOnlyList<string> Weightings,
    IReadOnlyList<string> TrustRanks,
    IReadOnlyList<GiveawayRoleView> GroupRoles,
    IReadOnlyList<GiveawayRoleView> DiscordRoles,
    int ModerationFactRetentionDays,
    int PresenceFactRetentionDays);
