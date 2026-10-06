using Modbot.Companion.Overlay;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Shared.Names;

namespace Modbot.Overlay.Views;

/// <summary>Which people a list shows, by how they stand with the group.</summary>
public enum Who
{
    All,
    Flagged,
    Members,
    Staff,
    NotInGroup,
}

/// <summary>How long ago something happened: joining, on the Instance list; the row, on the Audit Log.</summary>
public enum TimeWindow
{
    Any,
    FiveMinutes,
    FifteenMinutes,
    Hour,

    /// <summary>More than an hour ago, or before Modbot saw them arrive.</summary>
    Earlier,
}

/// <summary>What order the Instance list is in.</summary>
public enum RosterOrder
{
    /// <summary>Flagged, then staff, then members, then everybody else; by name inside each.</summary>
    Standing,

    /// <summary>Whoever arrived last first.</summary>
    Newest,

    Name,
}

/// <summary>One of the controls in a list's filter row.</summary>
public enum FilterPart
{
    Who,
    Rank,

    /// <summary>"Joined" on the Instance list, "When" on the Audit Log.</summary>
    Time,
    Name,

    /// <summary>The Instance list only.</summary>
    Sort,

    /// <summary>The Audit Log only.</summary>
    Kind,
}

/// <summary>
/// The trust ranks a list is cut down to, as bits, so two picks compare equal by value. Empty is
/// every rank.
/// </summary>
public readonly record struct RankPick(int Bits)
{
    /// <summary>The bit for a person whose rank the server has not said.</summary>
    private const int NotKnownBit = 1 << 15;

    /// <summary>The ranks offered, lowest first, and null for "Not known".</summary>
    /// <remarks>
    /// Legend and VRChat Team are left off: both are all but gone, and a strip of nine would wrap
    /// onto a second line of the panel. A person holding one is shown while no rank is picked.
    /// </remarks>
    public static IReadOnlyList<TrustRank?> Offered { get; } =
    [
        TrustRank.Visitor,
        TrustRank.NewUser,
        TrustRank.User,
        TrustRank.KnownUser,
        TrustRank.TrustedUser,
        TrustRank.Nuisance,
        null,
    ];

    public bool IsEmpty => Bits == 0;

    public int Count => System.Numerics.BitOperations.PopCount((uint)Bits);

    public bool Has(TrustRank? rank) => (Bits & Bit(rank)) != 0;

    public RankPick Toggle(TrustRank? rank) => new(Bits ^ Bit(rank));

    /// <summary>Whether a person of this rank is shown: always, while nothing is picked.</summary>
    public bool Lets(TrustRank? rank) => IsEmpty || Has(rank);

    /// <summary>The picked ranks in the order they are offered.</summary>
    public IEnumerable<TrustRank?> Picked
    {
        get
        {
            var bits = Bits;
            return Offered.Where(rank => (bits & Bit(rank)) != 0);
        }
    }

    private static int Bit(TrustRank? rank) => rank is { } known ? 1 << (int)known : NotKnownBit;
}

/// <summary>The kinds of Audit Log row a list is cut down to, as bits. Empty is every kind.</summary>
public readonly record struct KindPick(int Bits)
{
    /// <summary>The kinds offered, in the order the strip shows them.</summary>
    public static IReadOnlyList<string> Offered { get; } =
    [
        LiveEventKinds.PersonJoined,
        LiveEventKinds.PersonLeft,
        LiveEventKinds.PersonHere,
        LiveEventKinds.FlaggedJoin,
        LiveEventKinds.WatchStopped,
    ];

    public bool IsEmpty => Bits == 0;

    public int Count => System.Numerics.BitOperations.PopCount((uint)Bits);

    public bool Has(string kind) => Index(kind) is { } i && (Bits & (1 << i)) != 0;

    public KindPick Toggle(string kind) => Index(kind) is { } i ? new(Bits ^ (1 << i)) : this;

    /// <summary>
    /// Whether a row of this kind is shown. A kind Modbot has no word for is shown only while
    /// nothing is picked, because no pick can name it.
    /// </summary>
    public bool Lets(string kind) => IsEmpty || Has(kind);

    public IEnumerable<string> Picked
    {
        get
        {
            var bits = Bits;
            return Offered.Where((_, i) => (bits & (1 << i)) != 0);
        }
    }

    private static int? Index(string kind)
    {
        for (var i = 0; i < Offered.Count; i++)
        {
            if (string.Equals(Offered[i], kind, StringComparison.Ordinal))
                return i;
        }

        return null;
    }
}

/// <summary>
/// What one list on the panel is cut down to, and which of its filters has its choices open.
/// </summary>
/// <remarks>
/// <para>One of these per list: the Instance list and the Audit Log each keep their own, so neither
/// looks empty for a reason set on the other. The Instance list does not use
/// <see cref="Kinds"/>, and the Audit Log does not use <see cref="Order"/>.</para>
/// <para>A value, so the screen it sits on still compares by what it would draw. Kept by the drive
/// loop, and gone when the moderator moves to another instance.</para>
/// </remarks>
/// <param name="Open">The filter whose choices are showing under the row, or null.</param>
public sealed record ListFilters(
    Who Who = Who.All,
    RankPick Ranks = default,
    TimeWindow Time = TimeWindow.Any,
    string? Name = null,
    RosterOrder Order = RosterOrder.Standing,
    KindPick Kinds = default,
    FilterPart? Open = null)
{
    /// <summary>Nothing picked and nothing open: the whole list, as it always was.</summary>
    public static ListFilters None { get; } = new();

    /// <summary>The longest name a search keeps.</summary>
    public const int LongestName = 32;

    /// <summary>Whether anything picked can hide a row. The order hides nothing.</summary>
    public bool Hides => Who != Who.All || !Ranks.IsEmpty || Time != TimeWindow.Any || Name is { Length: > 0 } || !Kinds.IsEmpty;

    /// <summary>Whether anything at all is picked, which is when Clear is offered.</summary>
    public bool AnyPicked => Hides || Order != RosterOrder.Standing;

    /// <summary>A search as it is kept: trimmed, cut to <see cref="LongestName"/>, and null for nothing.</summary>
    public static string? CleanName(string? typed)
    {
        var trimmed = typed?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= LongestName ? trimmed : trimmed[..LongestName];
    }
}

/// <summary>
/// The filtering and ordering both lists share, apart from the view so the drive loop can count
/// what the view will show.
/// </summary>
public static class ListFiltering
{
    /// <summary>
    /// Everybody the Instance list may show: those here, then those who left in the last minute.
    /// Somebody on both is shown once, as present. Sorting and filtering then treat a row of
    /// somebody who left exactly as it was treated while they were here, which is what keeps the
    /// order from jumping when they go.
    /// </summary>
    public static IReadOnlyList<RosterMember> Everyone(IReadOnlyList<RosterMember> present, IReadOnlyList<RecentLeaver> left)
    {
        if (left.Count == 0)
            return present;

        var here = present.Select(member => member.SubjectId).ToHashSet(StringComparer.Ordinal);
        return [.. present, .. left.Select(leaver => leaver.Member).Where(member => !here.Contains(member.SubjectId))];
    }

    /// <summary>
    /// The Instance list as the filters leave it, in the order it is drawn.
    /// </summary>
    /// <param name="arrivals">
    /// When each person got here, from this PC's own copy of VRChat's log: a time, or null for
    /// somebody already here when Modbot saw the moderator arrive. A person missing from it is
    /// somebody the log has not mentioned, and is left out by any time filter.
    /// </param>
    /// <param name="now">The drive loop's clock, which the time filters measure against.</param>
    public static IReadOnlyList<RosterMember> Roster(
        IReadOnlyList<RosterMember> members,
        ListFilters filters,
        IReadOnlyDictionary<string, DateTimeOffset?> arrivals,
        DateTimeOffset now)
    {
        var term = Term(filters.Name);

        var shown = members.Where(member =>
            Lets(filters.Who, member.Standing)
            && filters.Ranks.Lets(member.TrustRank)
            && LetsArrival(filters.Time, arrivals.TryGetValue(member.SubjectId, out var at), at, now)
            && Matches(member.DisplayName, term));

        // Flagged first, then staff, then everybody else: the overlay's job is to put the row
        // that matters where the eye lands. Within a band the order is by the name in plain
        // letters, so 𝕬𝖑𝖊𝖝 sits with the As.
        return filters.Order switch
        {
            RosterOrder.Newest => shown
                .OrderBy(member => arrivals.TryGetValue(member.SubjectId, out var at) && at is { } known ? -known.UtcTicks : long.MaxValue)
                .ThenBy(SortName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RosterOrder.Name => shown
                .OrderBy(SortName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => shown
                .OrderBy(Priority)
                .ThenBy(SortName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    /// <summary>The Audit Log as the filters leave it, newest first as it came.</summary>
    public static IReadOnlyList<LiveEvent> Events(IReadOnlyList<LiveEvent> events, ListFilters filters, DateTimeOffset now)
    {
        var term = Term(filters.Name);

        return events.Where(@event =>
            filters.Kinds.Lets(@event.Kind)
            && (filters.Who is Who.All || (@event.Person is { } person && Lets(filters.Who, person.Standing)))
            && (filters.Ranks.IsEmpty || (@event.Person is { } ranked && filters.Ranks.Has(RankOf(ranked))))
            && LetsTime(filters.Time, now - @event.At)
            && (term is null || (@event.Person is { } named && Matches(named.DisplayName, term))))
            .ToList();
    }

    /// <summary>A person's rank off a live event, or null when the server did not say it.</summary>
    public static TrustRank? RankOf(LivePerson person)
        => person.TrustRank is { Length: > 0 } word ? TrustRanks.Parse(word) : null;

    /// <summary>How long somebody has been here, in the fewest words that still say it.</summary>
    /// <param name="arrived">When they got here, or null for somebody already here when the moderator arrived.</param>
    public static string JoinedWords(DateTimeOffset? arrived, DateTimeOffset now)
    {
        if (arrived is not { } at)
            return "already here";

        var here = now - at;
        return here < TimeSpan.FromMinutes(1) ? "<1m" : TimeWords.Length(here);
    }

    /// <summary>The name in plain letters, else the name, else the id: what a row sorts by.</summary>
    public static string SortName(RosterMember member)
    {
        if (member.DisplayName is null)
            return member.SubjectId;

        var plain = NameNormalizer.Readable(member.DisplayName);
        return plain.Length == 0 ? member.DisplayName : plain;
    }

    private static int Priority(RosterMember member) => member.Standing switch
    {
        RosterStanding.Flagged => 0,
        RosterStanding.Staff => 1,
        RosterStanding.Member => 2,
        _ => 3,
    };

    private static bool Lets(Who who, RosterStanding standing) => who switch
    {
        Who.Flagged => standing is RosterStanding.Flagged,
        Who.Members => standing is RosterStanding.Member,
        Who.Staff => standing is RosterStanding.Staff,
        Who.NotInGroup => standing is RosterStanding.Ordinary,
        _ => true,
    };

    private static bool LetsArrival(TimeWindow window, bool seen, DateTimeOffset? at, DateTimeOffset now)
    {
        if (window is TimeWindow.Any)
            return true;

        // Nothing is known about somebody the log never mentioned, so no window claims them.
        if (!seen)
            return false;

        // Already here when the moderator arrived: that is earlier than anything Modbot saw.
        return at is { } known ? LetsTime(window, now - known) : window is TimeWindow.Earlier;
    }

    private static bool LetsTime(TimeWindow window, TimeSpan ago) => window switch
    {
        TimeWindow.FiveMinutes => ago <= TimeSpan.FromMinutes(5),
        TimeWindow.FifteenMinutes => ago <= TimeSpan.FromMinutes(15),
        TimeWindow.Hour => ago <= TimeSpan.FromHours(1),
        TimeWindow.Earlier => ago > TimeSpan.FromHours(1),
        _ => true,
    };

    /// <summary>The search as the names are compared: plain letters, lower case. Null for none.</summary>
    private static string? Term(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var term = NameNormalizer.Searchable(name);
        return term.Length == 0 ? name.Trim() : term;
    }

    /// <summary>
    /// Whether a display name holds the search, in plain letters or as written, so typing
    /// <c>alex</c> finds 𝕬𝖑𝖊𝖝 and typing an emoji finds the emoji.
    /// </summary>
    private static bool Matches(string? displayName, string? term)
    {
        if (term is null)
            return true;

        if (displayName is null)
            return false;

        return NameNormalizer.Searchable(displayName).Contains(term, StringComparison.Ordinal)
            || displayName.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
