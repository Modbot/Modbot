using System.Text.Json.Nodes;

namespace Modbot.Core.Giveaways;

/// <summary>What an entrant's weight is counted from (giveaways design §4.3).</summary>
/// <remarks>
/// Every one but <see cref="Uniform"/> is a number the rules also expose, so a weight is never
/// counted from something a person could not have asked about.
/// </remarks>
public static class GiveawayWeights
{
    /// <summary>Everybody the same. The default.</summary>
    public const string Uniform = "uniform";

    /// <summary>Hours in our instances.</summary>
    public const string InstanceHours = "instanceHours";

    /// <summary>Hours in Discord voice.</summary>
    public const string VoiceHours = "voiceHours";

    /// <summary>Discord messages sent.</summary>
    public const string Messages = "messages";

    /// <summary>Days they were seen in one of our instances.</summary>
    public const string DaysSeen = "daysSeen";

    public static readonly IReadOnlyList<string> All = [Uniform, InstanceHours, VoiceHours, Messages, DaysSeen];

    public static bool IsKnown(string kind) => All.Contains(kind, StringComparer.Ordinal);

    /// <summary>Weights counted from polled presence reports rather than exactly-timed facts.</summary>
    public static bool IsApproximate(string kind) => kind is InstanceHours or DaysSeen;

    public static string Label(string kind) => kind switch
    {
        InstanceHours => "Hours in our instances",
        VoiceHours => "Hours in Discord voice",
        Messages => "Discord messages",
        DaysSeen => "Days seen",
        _ => "Everybody the same",
    };
}

/// <summary>
/// How a person's number becomes a whole-number weight.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Whole numbers, on purpose.</strong> A draw has to be reproducible by somebody else from
/// the snapshot and the seed alone (giveaways design §5), and the moment weights are fractions
/// that reproduction depends on how whoever is checking rounds. Whole numbers and integer
/// arithmetic give one answer on every machine and in every language.
/// </para>
/// <para>
/// The floor of one is what keeps a qualifying entrant an entrant. Somebody who passed every rule
/// but happens to have nought hours recorded has entered, and a weight of zero would be a silent
/// exclusion of exactly the kind §4.2 says must be a recorded parameter instead.
/// </para>
/// </remarks>
public static class GiveawayWeight
{
    /// <summary>The largest cap anybody may set. Above this a cap is not capping anything.</summary>
    public const long MaxCap = 1_000_000;

    /// <summary>
    /// The weight a measured number becomes: rounded to a whole number, never below one, never
    /// above <paramref name="cap"/>.
    /// </summary>
    public static long Of(decimal measured, long? cap)
    {
        var rounded = measured <= 0 ? 1 : (long)Math.Round(measured, MidpointRounding.AwayFromZero);

        if (rounded < 1)
            rounded = 1;

        if (cap is { } limit && rounded > limit)
            rounded = limit;

        return rounded;
    }
}

/// <summary>
/// Who is kept out of a draw, and why — recorded on the giveaway and copied onto every draw
/// (giveaways design §5.2).
/// </summary>
/// <remarks>
/// Exclusions are parameters, never a quiet filter. An excluded person is in the snapshot with
/// their reason beside them, so "why didn't I win" has an answer that does not require trusting
/// anybody.
/// </remarks>
public sealed record GiveawayExclusions
{
    /// <summary>How many people may be named one at a time.</summary>
    public const int MaxPeople = 200;

    /// <summary>Anybody with a Modbot account. The people running the giveaway.</summary>
    public bool Staff { get; init; }

    /// <summary>Anybody who has already won a giveaway on this Modbot.</summary>
    public bool PastWinners { get; init; }

    /// <summary>Anybody banned from the group right now.</summary>
    public bool BannedMembers { get; init; }

    /// <summary>People named one at a time, as VRChat or Discord ids. Opaque text, never parsed.</summary>
    public IReadOnlyList<string> People { get; init; } = [];

    public static GiveawayExclusions None { get; } = new();

    public bool Any => Staff || PastWinners || BannedMembers || People.Count > 0;

    /// <summary>Plain words, one per exclusion, for the card and the page.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();

        if (Staff)
            lines.Add("staff");

        if (PastWinners)
            lines.Add("people who have won before");

        if (BannedMembers)
            lines.Add("banned members");

        if (People.Count > 0)
            lines.Add(People.Count == 1 ? "1 person named" : $"{People.Count} people named");

        return lines;
    }

    public static GiveawayExclusions Read(JsonNode? node, out string? error)
    {
        error = null;

        if (node is not JsonObject body)
            return None;

        var people = new List<string>();

        if (body["people"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                var id = entry?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(id))
                    continue;

                if (id.Length > 128)
                {
                    error = "That id is too long.";
                    return None;
                }

                if (!people.Contains(id, StringComparer.Ordinal))
                    people.Add(id);
            }

            if (people.Count > MaxPeople)
            {
                error = $"At most {MaxPeople} people can be named.";
                return None;
            }
        }

        return new GiveawayExclusions
        {
            Staff = body["staff"]?.GetValue<bool>() ?? false,
            PastWinners = body["pastWinners"]?.GetValue<bool>() ?? false,
            BannedMembers = body["bannedMembers"]?.GetValue<bool>() ?? false,
            People = people,
        };
    }

    public static GiveawayExclusions ReadStored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return None;

        try
        {
            return Read(JsonNode.Parse(json), out var error) is var read && error is null ? read : None;
        }
        catch (System.Text.Json.JsonException)
        {
            return None;
        }
    }

    public JsonObject Write() => new()
    {
        ["staff"] = Staff,
        ["pastWinners"] = PastWinners,
        ["bannedMembers"] = BannedMembers,
        ["people"] = new JsonArray([.. People.Select(p => (JsonNode)JsonValue.Create(p))]),
    };

    public string Store() => Write().ToJsonString();
}

/// <summary>Why somebody in the snapshot could not win.</summary>
public static class GiveawayKeptOut
{
    /// <summary>They are in the draw.</summary>
    public const string No = "";

    /// <summary>A rule they do not pass. For a react entry, one they passed when they reacted.</summary>
    public const string Rules = "rules";

    public const string Staff = "staff";
    public const string PastWinner = "pastWinner";
    public const string Banned = "banned";

    /// <summary>Named one at a time on the giveaway.</summary>
    public const string Named = "named";

    /// <summary>They withdrew by taking their reaction off.</summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>A rule needed VRChat data and Modbot has no VRChat account for them.</summary>
    public const string NoVRChatAccount = "noVRChatAccount";

    public static string Label(string reason) => reason switch
    {
        Rules => "Does not pass the rules",
        Staff => "Staff",
        PastWinner => "Won before",
        Banned => "Banned",
        Named => "Kept out by name",
        Withdrawn => "Withdrew",
        NoVRChatAccount => "No VRChat account",
        _ => "In the draw",
    };
}
