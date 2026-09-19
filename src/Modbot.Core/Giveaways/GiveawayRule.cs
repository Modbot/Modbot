using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Core.Users;

namespace Modbot.Core.Giveaways;

/// <summary>The words a rule's <see cref="GiveawayRule.Kind"/> may hold.</summary>
/// <remarks>
/// <para>
/// Three of them combine other rules; the rest ask one question about one person. A rule that is
/// not in this list is refused when it is saved, so nothing unknown ever reaches the checker
/// (giveaways design §2).
/// </para>
/// <para>
/// There is no "does not hold" flag on a single rule. <see cref="NoneOf"/> around it says the same
/// thing with one idea instead of two, and it reads the same way in the card: "none of: holds the
/// Regulars role".
/// </para>
/// </remarks>
public static class GiveawayRuleKinds
{
    // ── Combining ────────────────────────────────────────────────────────────────────────

    /// <summary>Every rule inside must pass.</summary>
    public const string AllOf = "allOf";

    /// <summary>At least one rule inside must pass.</summary>
    public const string AnyOf = "anyOf";

    /// <summary>No rule inside may pass.</summary>
    public const string NoneOf = "noneOf";

    // ── Asking ───────────────────────────────────────────────────────────────────────────

    /// <summary>In the Discord server for at least <c>Amount</c> days, from the stored join date.</summary>
    public const string DiscordMemberDays = "discordMemberDays";

    /// <summary>In the VRChat group for at least <c>Amount</c> days, from the join date VRChat states.</summary>
    public const string GroupMemberDays = "groupMemberDays";

    /// <summary>In the VRChat group right now.</summary>
    public const string InGroup = "inGroup";

    /// <summary>At least <c>Amount</c> hours in our instances, all of them added up.</summary>
    public const string InstanceHours = "instanceHours";

    /// <summary>At least <c>Amount</c> hours in one single instance — a different question from the total.</summary>
    public const string OneInstanceHours = "oneInstanceHours";

    /// <summary>At least <c>Amount</c> hours in Discord voice.</summary>
    public const string VoiceHours = "voiceHours";

    /// <summary>At least <c>Amount</c> Discord messages sent.</summary>
    public const string Messages = "messages";

    /// <summary>Has a Discord account and a VRChat account linked to each other.</summary>
    public const string LinkedAccounts = "linkedAccounts";

    /// <summary>Holds the VRChat group role <c>Id</c>.</summary>
    public const string GroupRole = "groupRole";

    /// <summary>Holds the Discord role <c>Id</c>.</summary>
    public const string DiscordRole = "discordRole";

    /// <summary>Seen in one of our instances in the last <c>Amount</c> days.</summary>
    public const string SeenWithinDays = "seenWithinDays";

    /// <summary>No bans, kicks or flags.</summary>
    public const string NoTrouble = "noTrouble";

    /// <summary>The VRChat account is at least <c>Amount</c> days old, from the join date Modbot stores.</summary>
    public const string VRChatAccountDays = "vrchatAccountDays";

    /// <summary>
    /// The VRChat trust rank is at least the one named in <c>Id</c> -- <c>"TrustedUser"</c>.
    /// </summary>
    /// <remarks>
    /// The rank is a name and not a number because a rule tree is JSON that people read, and
    /// <c>{"id":"TrustedUser"}</c> says what it means where <c>{"amount":4}</c> does not.
    /// Nuisance and VRChat Team sit above the ladder because they override it, so "at least"
    /// never reaches them (auto-invites design §3.1).
    /// </remarks>
    public const string TrustRankAtLeast = "trustRankAtLeast";

    /// <summary>
    /// Modbot has seen them as 18+ verified at least once.
    /// </summary>
    /// <remarks>
    /// The sticky flag, not VRChat's current status word: VRChat lets somebody hide the
    /// verification again, and a rule that flickered with it would answer differently on two days
    /// for a thing that did not change (user profile sync design §4).
    /// </remarks>
    public const string Age18Plus = "age18Plus";

    public static readonly IReadOnlyList<string> Combining = [AllOf, AnyOf, NoneOf];

    /// <summary>Every kind that asks a question, in the order the builder lists them.</summary>
    public static readonly IReadOnlyList<string> Asking =
    [
        DiscordMemberDays,
        GroupMemberDays,
        InGroup,
        InstanceHours,
        OneInstanceHours,
        VoiceHours,
        Messages,
        SeenWithinDays,
        LinkedAccounts,
        GroupRole,
        DiscordRole,
        NoTrouble,
        VRChatAccountDays,
        TrustRankAtLeast,
        Age18Plus,
    ];

    public static bool IsCombining(string kind) => Combining.Contains(kind, StringComparer.Ordinal);

    public static bool IsKnown(string kind) => IsCombining(kind) || Asking.Contains(kind, StringComparer.Ordinal);

    /// <summary>Kinds that take a number of hours, days or messages.</summary>
    public static bool TakesAmount(string kind) => kind is DiscordMemberDays or GroupMemberDays
        or InstanceHours or OneInstanceHours or VoiceHours or Messages or SeenWithinDays or VRChatAccountDays;

    /// <summary>Kinds that may be narrowed to the last so many days.</summary>
    public static bool TakesWindow(string kind) => kind is InstanceHours or OneInstanceHours
        or VoiceHours or Messages or NoTrouble;

    /// <summary>Kinds that name a role.</summary>
    public static bool TakesId(string kind) => kind is GroupRole or DiscordRole;

    /// <summary>Kinds that name a trust rank.</summary>
    /// <remarks>
    /// Apart from <see cref="TakesId"/> although both use the same field: a role is picked from
    /// the group's own list and a rank from a fixed ladder, and the builder draws two different
    /// controls for them.
    /// </remarks>
    public static bool TakesRank(string kind) => kind is TrustRankAtLeast;
}

/// <summary>
/// One rule, or a group of rules combined with all of, any of or none of.
/// </summary>
/// <remarks>
/// <para>
/// One record for both, because a tree of two record types is twice the JSON to read and twice the
/// code to walk, and every combining rule is a rule that happens to hold other rules.
/// </para>
/// <para>
/// <strong>This model is deliberately not about giveaways.</strong> It asks who a person is and
/// what they have done, and nothing about prizes or draws. M7 §2's segment builder wants the same
/// questions asked of the same data; when it is built it should take this tree and this checker
/// rather than grow a second answer to "how many hours has this person spent here" that can
/// disagree with the first (giveaways design §2.6).
/// </para>
/// </remarks>
public sealed record GiveawayRule
{
    /// <summary>How deep rules may nest. One level of nesting is asked for; three is room to spare.</summary>
    public const int MaxDepth = 3;

    /// <summary>How many rules one group may hold.</summary>
    public const int MaxRules = 20;

    /// <summary>How many rules a whole tree may hold, however they are arranged.</summary>
    public const int MaxTotalRules = 60;

    /// <summary>One of <see cref="GiveawayRuleKinds"/>.</summary>
    public string Kind { get; init; } = GiveawayRuleKinds.AllOf;

    /// <summary>The rules inside, for a combining kind. Empty for a question.</summary>
    public IReadOnlyList<GiveawayRule> Rules { get; init; } = [];

    /// <summary>How many hours, days or messages. Null for a kind that takes no number.</summary>
    public decimal? Amount { get; init; }

    /// <summary>Only count the last so many days. Null means all of recorded history.</summary>
    public int? WithinDays { get; init; }

    /// <summary>
    /// A role id, for the role kinds, or a trust rank name for <see cref="GiveawayRuleKinds.TrustRankAtLeast"/>.
    /// A role id is opaque text, never parsed (foundation §3.1.1); a rank name is one of
    /// <see cref="Modbot.Core.Users.TrustRank"/>'s members and is checked when the rule is read.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>A rule that lets everybody through: all of nothing.</summary>
    public static GiveawayRule Everyone { get; } = new();

    /// <summary>True when this rule lets everybody through, so the page can say "Everyone".</summary>
    public bool LetsEveryoneIn =>
        Kind == GiveawayRuleKinds.AllOf && Rules.Count == 0;
}

/// <summary>Reads a rule tree out of JSON, and writes one back, refusing anything it does not know.</summary>
public static class GiveawayRules
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>
    /// Reads a stored or submitted rule tree. Returns null and sets <paramref name="error"/> when
    /// anything is wrong, in the words a person should see.
    /// </summary>
    public static GiveawayRule? Read(JsonNode? node, out string? error)
    {
        error = null;

        if (node is null)
            return GiveawayRule.Everyone;

        var total = 0;
        var rule = Read(node, depth: 1, ref total, ref error);
        return error is null ? rule : null;
    }

    /// <summary>Reads a rule tree from stored text. Unreadable text is treated as "everyone".</summary>
    public static GiveawayRule ReadStored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return GiveawayRule.Everyone;

        try
        {
            var rule = Read(JsonNode.Parse(json), out var error);
            return error is null && rule is not null ? rule : GiveawayRule.Everyone;
        }
        catch (JsonException)
        {
            return GiveawayRule.Everyone;
        }
    }

    /// <summary>The tree as JSON, for the column and for the API.</summary>
    public static JsonObject Write(GiveawayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var node = new JsonObject { ["kind"] = rule.Kind };

        if (GiveawayRuleKinds.IsCombining(rule.Kind))
        {
            var rules = new JsonArray();
            foreach (var inner in rule.Rules)
                rules.Add(Write(inner));

            node["rules"] = rules;
            return node;
        }

        if (rule.Amount is { } amount)
            node["amount"] = amount;

        if (rule.WithinDays is { } days)
            node["withinDays"] = days;

        if (rule.Id is { } id)
            node["id"] = id;

        return node;
    }

    /// <summary>The tree as stored text.</summary>
    public static string Store(GiveawayRule rule) => Write(rule).ToJsonString(Compact);

    /// <summary>
    /// The rule in plain words, one line, for the Discord card and the page.
    /// </summary>
    /// <remarks>
    /// Names for roles are passed in rather than looked up, because this runs where there is no
    /// database — inside the card builder — and a role id in a card nobody can read is worse than
    /// the role's own name being one sync behind.
    /// </remarks>
    public static string Describe(GiveawayRule rule, IReadOnlyDictionary<string, string>? roleNames = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.LetsEveryoneIn)
            return "Everyone";

        return Line(rule, roleNames);
    }

    /// <summary>The rule as a list of lines, one per rule inside the outermost group.</summary>
    public static IReadOnlyList<string> DescribeLines(
        GiveawayRule rule, IReadOnlyDictionary<string, string>? roleNames = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.LetsEveryoneIn)
            return ["Everyone"];

        return rule.Kind == GiveawayRuleKinds.AllOf
            ? [.. rule.Rules.Select(r => Line(r, roleNames))]
            : [Line(rule, roleNames)];
    }

    private static string Line(GiveawayRule rule, IReadOnlyDictionary<string, string>? roleNames)
    {
        if (GiveawayRuleKinds.IsCombining(rule.Kind))
        {
            var word = rule.Kind switch
            {
                GiveawayRuleKinds.AnyOf => "any of",
                GiveawayRuleKinds.NoneOf => "none of",
                _ => "all of",
            };

            return rule.Rules.Count == 0
                ? rule.Kind == GiveawayRuleKinds.NoneOf ? "Everyone" : "Everyone"
                : $"{word}: {string.Join("; ", rule.Rules.Select(r => Line(r, roleNames)))}";
        }

        var within = rule.WithinDays is { } days ? $" in the last {Plain(days)} days" : string.Empty;
        var amount = rule.Amount ?? 0;

        return rule.Kind switch
        {
            GiveawayRuleKinds.DiscordMemberDays => $"in Discord for {Plain(amount)} days or more",
            GiveawayRuleKinds.GroupMemberDays => $"in the group for {Plain(amount)} days or more",
            GiveawayRuleKinds.InGroup => "in the group now",
            GiveawayRuleKinds.InstanceHours => $"{Plain(amount)} hours or more in our instances{within}",
            GiveawayRuleKinds.OneInstanceHours => $"{Plain(amount)} hours or more in one single instance{within}",
            GiveawayRuleKinds.VoiceHours => $"{Plain(amount)} hours or more in Discord voice{within}",
            GiveawayRuleKinds.Messages => $"{Plain(amount)} Discord messages or more{within}",
            GiveawayRuleKinds.SeenWithinDays => $"seen in the last {Plain(amount)} days",
            GiveawayRuleKinds.LinkedAccounts => "Discord and VRChat accounts linked",
            GiveawayRuleKinds.GroupRole => $"holds the group role {RoleName(rule.Id, roleNames)}",
            GiveawayRuleKinds.DiscordRole => $"holds the Discord role {RoleName(rule.Id, roleNames)}",
            GiveawayRuleKinds.NoTrouble => $"no bans, kicks or flags{within}",
            GiveawayRuleKinds.VRChatAccountDays => $"VRChat account {Plain(amount)} days old or more",
            GiveawayRuleKinds.TrustRankAtLeast => $"trust rank {RankName(rule.Id)} or better",
            GiveawayRuleKinds.Age18Plus => "18+ verified",
            _ => rule.Kind,
        };
    }

    /// <summary>The rank a rule names, in the words a nameplate shows.</summary>
    private static string RankName(string? id)
        => id is null ? "(none picked)" : TrustRanks.Name(TrustRanks.Parse(id));

    private static string RoleName(string? id, IReadOnlyDictionary<string, string>? roleNames)
    {
        if (id is null)
            return "(none picked)";

        return roleNames is not null && roleNames.TryGetValue(id, out var name) && name.Length > 0 ? name : id;
    }

    /// <summary>A number without trailing zeros: <c>10</c>, not <c>10.00</c>.</summary>
    public static string Plain(decimal value)
    {
        var rounded = Math.Round(value, 2);
        return rounded == Math.Truncate(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static GiveawayRule? Read(JsonNode? node, int depth, ref int total, ref string? error)
    {
        if (node is not JsonObject body)
        {
            error = "A rule must be an object.";
            return null;
        }

        var kind = body["kind"]?.GetValue<string>()?.Trim() ?? string.Empty;

        if (!GiveawayRuleKinds.IsKnown(kind))
        {
            error = kind.Length == 0 ? "A rule needs a kind." : $"'{kind}' is not a rule Modbot knows.";
            return null;
        }

        if (++total > GiveawayRule.MaxTotalRules)
        {
            error = $"A giveaway can have at most {GiveawayRule.MaxTotalRules} rules.";
            return null;
        }

        if (GiveawayRuleKinds.IsCombining(kind))
        {
            if (depth > GiveawayRule.MaxDepth)
            {
                error = $"Rules can be grouped at most {GiveawayRule.MaxDepth} deep.";
                return null;
            }

            var inner = new List<GiveawayRule>();

            if (body["rules"] is JsonArray array)
            {
                if (array.Count > GiveawayRule.MaxRules)
                {
                    error = $"A group can hold at most {GiveawayRule.MaxRules} rules.";
                    return null;
                }

                foreach (var child in array)
                {
                    var read = Read(child, depth + 1, ref total, ref error);
                    if (error is not null)
                        return null;

                    if (read is not null)
                        inner.Add(read);
                }
            }

            return new GiveawayRule { Kind = kind, Rules = inner };
        }

        decimal? amount = null;
        if (GiveawayRuleKinds.TakesAmount(kind))
        {
            amount = Number(body["amount"]);

            if (amount is null)
            {
                error = $"'{kind}' needs a number.";
                return null;
            }

            if (amount < 0 || amount > 1_000_000)
            {
                error = "A rule's number must be between 0 and 1,000,000.";
                return null;
            }
        }

        int? withinDays = null;
        if (body["withinDays"] is { } window && window.GetValueKind() is not JsonValueKind.Null)
        {
            if (!GiveawayRuleKinds.TakesWindow(kind))
            {
                error = $"'{kind}' is not counted over a window.";
                return null;
            }

            var days = Number(window);
            if (days is null || days < 1 || days > 3650)
            {
                error = "A window must be between 1 and 3,650 days.";
                return null;
            }

            withinDays = (int)days.Value;
        }

        string? id = null;
        if (GiveawayRuleKinds.TakesRank(kind))
        {
            id = body["id"]?.GetValue<string>()?.Trim();

            // Parsed strictly rather than through TrustRanks.Parse, which answers Visitor for
            // anything it does not know: a rank nobody meant would quietly become "everybody".
            if (string.IsNullOrEmpty(id)
                || !Enum.TryParse<TrustRank>(id, ignoreCase: true, out var rank)
                || !Enum.IsDefined(rank))
            {
                error = $"'{kind}' needs a trust rank.";
                return null;
            }

            if (!TrustRanks.OnTheLadder(rank))
            {
                error = $"“{TrustRanks.Name(rank)}” is not a rank a rule can ask for.";
                return null;
            }

            id = rank.ToString();
        }
        else if (GiveawayRuleKinds.TakesId(kind))
        {
            id = body["id"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrEmpty(id))
            {
                error = $"'{kind}' needs a role.";
                return null;
            }

            if (id.Length > 128)
            {
                error = "That role id is too long.";
                return null;
            }
        }

        return new GiveawayRule { Kind = kind, Amount = amount, WithinDays = withinDays, Id = id };
    }

    private static decimal? Number(JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.Number => node.GetValue<decimal>(),
                JsonValueKind.String => decimal.TryParse(
                    node.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null,
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
