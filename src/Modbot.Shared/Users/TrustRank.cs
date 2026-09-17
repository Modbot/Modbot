using System.Text.Json.Serialization;

namespace Modbot.Core.Users;

/// <summary>
/// A VRChat trust rank, as the nameplate shows it.
/// </summary>
/// <remarks>
/// <para>
/// VRChat says which rank a person holds with one tag on the user object, and it names the tags
/// one rank <em>below</em> what they mean -- <c>system_trust_trusted</c> is Known User, not
/// Trusted User -- because the ranks were renamed in 2018 and the tags were not. The members here
/// are named after the rank a moderator sees, never after the tag. The tag table and the order
/// are in <c>.agent/research/2026-09-16-vrchat-trust-ranks.md</c>.
/// </para>
/// <para>
/// The values are in order so that <c>&lt;</c> means "lower rank than". <see cref="Nuisance"/>
/// and <see cref="VRChatTeam"/> sit above the ladder because they override it: a troll still
/// carries a ladder tag, and a staff account does too, but the badge shows one word.
/// </para>
/// <para>
/// Serialised by name on the wire, so <c>"KnownUser"</c> and never a number.
/// </para>
/// <para><strong>Persisted as smallint. Never renumber a member.</strong></para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TrustRank>))]
public enum TrustRank : short
{
    /// <summary>No trust tag at all. Where every account starts.</summary>
    Visitor = 0,

    /// <summary><c>system_trust_basic</c>. Blue.</summary>
    NewUser = 1,

    /// <summary><c>system_trust_known</c>. Green.</summary>
    User = 2,

    /// <summary><c>system_trust_trusted</c>. Orange.</summary>
    KnownUser = 3,

    /// <summary><c>system_trust_veteran</c>. Purple.</summary>
    TrustedUser = 4,

    /// <summary><c>system_trust_legend</c>. Gold. Retired in 2018; kept because the tag may still be on old accounts.</summary>
    Legend = 5,

    /// <summary><c>system_troll</c> or <c>system_probable_troll</c>. Overrides the ladder.</summary>
    Nuisance = 6,

    /// <summary><c>admin_moderator</c>. VRChat staff. Overrides everything, Nuisance included.</summary>
    VRChatTeam = 7,
}

/// <summary>Reads a trust rank off a tag list, and names and colours each rank.</summary>
public static class TrustRanks
{
    /// <summary>The tag that means each ladder rank, lowest first.</summary>
    /// <remarks>
    /// The order is the precedence: when a list carries more than one, the highest wins. VRChat
    /// sends only the rank held, not the ranks below it, so this is a safety net rather than the
    /// common case.
    /// </remarks>
    private static readonly (string Tag, TrustRank Rank)[] Ladder =
    [
        ("system_trust_basic", TrustRank.NewUser),
        ("system_trust_known", TrustRank.User),
        ("system_trust_trusted", TrustRank.KnownUser),
        ("system_trust_veteran", TrustRank.TrustedUser),
        ("system_trust_legend", TrustRank.Legend),
    ];

    /// <summary>
    /// The rank a tag list says. Never throws: a null list, a null tag or a tag Modbot has never
    /// heard of is simply not a rank.
    /// </summary>
    /// <remarks>
    /// Highest wins. <c>admin_moderator</c> beats everything; either nuisance tag beats every
    /// ladder rank; otherwise the highest ladder tag present. <c>system_trust_intermediate</c> and
    /// <c>system_trust_advanced</c>, the steps VRChat removed in 2022, are not ranks and are
    /// ignored -- an account that still carries one carries a real rank tag beside it.
    /// </remarks>
    public static TrustRank FromTags(IEnumerable<string?>? tags)
    {
        if (tags is null)
            return TrustRank.Visitor;

        var rank = TrustRank.Visitor;

        foreach (var tag in tags)
        {
            if (tag is null)
                continue;

            var found = tag switch
            {
                "admin_moderator" => TrustRank.VRChatTeam,
                "system_troll" or "system_probable_troll" => TrustRank.Nuisance,
                _ => LadderRank(tag),
            };

            if (found > rank)
                rank = found;
        }

        return rank;
    }

    /// <summary>The words VRChat uses for the rank, as a nameplate shows them.</summary>
    public static string Name(TrustRank rank) => rank switch
    {
        TrustRank.Visitor => "Visitor",
        TrustRank.NewUser => "New User",
        TrustRank.User => "User",
        TrustRank.KnownUser => "Known User",
        TrustRank.TrustedUser => "Trusted User",
        TrustRank.Legend => "Legend",
        TrustRank.Nuisance => "Nuisance",
        TrustRank.VRChatTeam => "VRChat Team",
        _ => "Visitor",
    };

    /// <summary>
    /// The colour VRChat paints the rank, as <c>#RRGGBB</c>. The research note says where each
    /// value comes from.
    /// </summary>
    public static string Colour(TrustRank rank) => rank switch
    {
        TrustRank.Visitor => "#CCCCCC",
        TrustRank.NewUser => "#1778FF",
        TrustRank.User => "#2BCF5C",
        TrustRank.KnownUser => "#FF7B42",
        TrustRank.TrustedUser => "#8143E6",
        TrustRank.Legend => "#FFD000",
        TrustRank.Nuisance => "#782F2F",
        TrustRank.VRChatTeam => "#FF2626",
        _ => "#CCCCCC",
    };

    /// <summary>Parses a stored or transmitted rank name; unknown text is <see cref="TrustRank.Visitor"/>.</summary>
    public static TrustRank Parse(string? name) =>
        name is not null && Enum.TryParse<TrustRank>(name, ignoreCase: true, out var rank) && Enum.IsDefined(rank)
            ? rank
            : TrustRank.Visitor;

    private static TrustRank LadderRank(string tag)
    {
        foreach (var (ladderTag, rank) in Ladder)
        {
            if (string.Equals(tag, ladderTag, StringComparison.Ordinal))
                return rank;
        }

        return TrustRank.Visitor;
    }
}
