using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Facts;

/// <summary>
/// The pairs of facts that one decision leaves behind, and which of each pair is the main one.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.3.2. Every pair here is a case where a person made <em>one</em> decision and Modbot ends
/// up holding two facts about it. Counting both would say a moderator acted twice and would push a
/// person towards repeat-offender status at half the number of decisions the operator set.
/// </para>
/// <para>
/// <strong>The main fact is the one that survives counting, and it is always the heavier one.</strong>
/// A ban and the instance kick that threw the person out of the instance they were standing in are
/// not equally important: the ban is the decision and the kick is how it took effect. Where the
/// pair is Modbot's own record beside the upstream system's, the upstream fact is the main one --
/// it is what every existing count already counts, so nothing a deployment already measured moves.
/// </para>
/// <para>
/// Mains and followers are deliberately disjoint sets: no type in this table is both. That keeps a
/// decision two facts deep at most and rules out a chain where a follower drags a third fact in
/// behind it.
/// </para>
/// </remarks>
public static class LinkedActions
{
    /// <summary>
    /// How far apart two facts of one decision may be.
    /// </summary>
    /// <remarks>
    /// VRChat writes the ban and its instance kick from one request, so live data has them inside
    /// a second; Modbot's own record of a press and VRChat's record of the result are a round trip
    /// apart. Ten seconds covers both with room to spare and is two orders of magnitude short of
    /// the case that must never collapse -- a ban, an unban, and a second ban ten minutes later,
    /// which is two decisions and has to count as two.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <param name="Main">The fact that counts.</param>
    /// <param name="Follower">The fact that is part of the same decision and must not count again.</param>
    public readonly record struct Pair(string Main, string Follower);

    /// <summary>
    /// Every pair Modbot has evidence for.
    /// </summary>
    /// <remarks>
    /// Found by reading what each producer writes, not by guessing:
    /// <list type="bullet">
    /// <item>VRChat's group audit log writes <c>group.instance.kick</c> beside a ban or a removal
    /// when the person was in one of the group's instances at the time. This is the case the
    /// maintainer reported.</item>
    /// <item>A moderation action taken in Modbot writes Modbot's own fact -- which carries who
    /// decided it, because VRChat's log can only say "Modbot" -- and VRChat's audit log then
    /// reports the same action from the other side.</item>
    /// <item>AutoMod's group actions do the same thing, with a rule in place of a person.</item>
    /// <item>Discord raises <c>guildMemberRemove</c> alongside <c>guildBanAdd</c>, so a ban and a
    /// kick each arrive with a leave behind them.</item>
    /// <item>An AutoMod timeout and Discord's own record of that timeout are one decision.</item>
    /// </list>
    /// </remarks>
    public static readonly Pair[] Pairs =
    [
        new(FactType.MemberBanned, FactType.GroupInstanceKick),
        new(FactType.MemberKicked, FactType.GroupInstanceKick),

        new(FactType.MemberBanned, FactType.ActionBan),
        new(FactType.MemberKicked, FactType.ActionKick),
        new(FactType.MemberUnbanned, FactType.ActionUnban),

        new(FactType.MemberBanned, FactType.AutoModGroupBan),
        new(FactType.MemberKicked, FactType.AutoModGroupRemove),

        new(FactType.DiscordMemberBanned, FactType.DiscordMemberLeft),
        new(FactType.DiscordMemberKicked, FactType.DiscordMemberLeft),

        new(FactType.DiscordMemberTimedOut, FactType.AutoModTimeout),
    ];

    private static readonly Dictionary<string, string[]> FollowersOfMain = Pairs
        .GroupBy(p => p.Main, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(p => p.Follower).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    private static readonly Dictionary<string, string[]> MainsOfFollower = Pairs
        .GroupBy(p => p.Follower, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(p => p.Main).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    /// <summary>The follower types a fact of this type can pull in, or empty when it leads nothing.</summary>
    public static string[] FollowersOf(string type)
        => FollowersOfMain.TryGetValue(type, out var found) ? found : [];

    /// <summary>The types a fact of this type can belong to, or empty when it is never a follower.</summary>
    public static string[] MainsOf(string type)
        => MainsOfFollower.TryGetValue(type, out var found) ? found : [];

    public static bool IsMain(string type) => FollowersOfMain.ContainsKey(type);

    public static bool IsFollower(string type) => MainsOfFollower.ContainsKey(type);

    /// <summary>
    /// Whether these two facts could be the same decision, on everything except their types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same person, close enough in time, and no disagreement about who did it. Actors are only
    /// compared <em>within a platform</em>: VRChat attributes everything Modbot does to Modbot's
    /// own account, so the VRChat fact names the service account while Modbot's fact names the
    /// moderator, and those are the same decision seen from two sides rather than two people.
    /// </para>
    /// <para>
    /// <strong>A fact whose time is a window never links.</strong> Spec 5.3: when all that is
    /// known is that something happened between two polls, "at the same moment as" is not a
    /// question the data can answer, and guessing it would quietly merge decisions that were
    /// minutes apart.
    /// </para>
    /// </remarks>
    public static bool CouldBeOneDecision(ModbotEvent main, ModbotEvent follower)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(follower);

        if (main.OccurredBefore is not null || follower.OccurredBefore is not null)
            return false;

        if (main.SubjectPlatform != follower.SubjectPlatform
            || !string.Equals(main.SubjectId, follower.SubjectId, StringComparison.Ordinal))
            return false;

        if ((main.OccurredAt - follower.OccurredAt).Duration() > Window)
            return false;

        return !ActorsDisagree(main, follower);
    }

    private static bool ActorsDisagree(ModbotEvent a, ModbotEvent b)
        => a.ActorId is not null
           && b.ActorId is not null
           && a.ActorPlatform == b.ActorPlatform
           && !string.Equals(a.ActorId, b.ActorId, StringComparison.Ordinal);
}
