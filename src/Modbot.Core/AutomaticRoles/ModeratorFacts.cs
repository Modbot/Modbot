namespace Modbot.Core.AutomaticRoles;

/// <summary>
/// Everything an automatic role rule needs to know about one Modbot account, gathered once so the
/// rule itself (<see cref="AutomaticRoleRule"/>) needs no database and no clock.
/// </summary>
/// <remarks>
/// <para>
/// The counts are kept as the moments they happened, not as totals, because each rule picks its
/// own number of days: one rule may look back 7 days and another 30, over the same facts.
/// <see cref="Now"/> is the moment the facts were gathered; the rule counts back from it.
/// </para>
/// <para>
/// Every list reaches back at least as far as the longest look-back of any rule it was gathered
/// for, and at least 24 hours.
/// </para>
/// </remarks>
/// <param name="UserId">The Modbot account.</param>
/// <param name="Now">When the facts were gathered. The rule counts back from here.</param>
/// <param name="IsModerator">The account may kick, ban or warn, or pair a companion: the Modbot moderator team.</param>
/// <param name="CompanionRunning">One of their paired, unrevoked companions has its live connection open now.</param>
/// <param name="ClipsReady">One of their paired companions last reported Clips on, the 10-minute setting and sending clips for their actions.</param>
/// <param name="InGroupInstance">One of their paired companions is in an instance of the managed group.</param>
/// <param name="PassedTrial">They have a staff trial that was closed.</param>
/// <param name="DiscordRoleIds">The Discord roles they hold, empty when they have no counted Discord account.</param>
/// <param name="VRChatRoleIds">The VRChat group roles they hold, empty when they have no linked VRChat account.</param>
/// <param name="BanTimes">When each ban they made was made.</param>
/// <param name="OverturnedDecidedAt">For each of their bans that was overturned on appeal, when the appeal was decided.</param>
/// <param name="MissedClipTimes">For each clip they missed, when the action was decided.</param>
public sealed record ModeratorFacts(
    Guid UserId,
    DateTimeOffset Now,
    bool IsModerator,
    bool CompanionRunning,
    bool ClipsReady,
    bool InGroupInstance,
    bool PassedTrial,
    IReadOnlyCollection<string> DiscordRoleIds,
    IReadOnlyCollection<string> VRChatRoleIds,
    IReadOnlyList<DateTimeOffset> BanTimes,
    IReadOnlyList<DateTimeOffset> OverturnedDecidedAt,
    IReadOnlyList<DateTimeOffset> MissedClipTimes);
