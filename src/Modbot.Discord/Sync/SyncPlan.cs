namespace Modbot.Discord.Sync;

/// <summary>
/// One change a sync would make, or has made.
/// </summary>
/// <remarks>
/// The same shape whether it was applied or only worked out, which is what makes a dry run
/// trustworthy: the list an operator reads before switching a sync on is produced by the code that
/// will do the work, not by a second description of it (M5 §7).
/// </remarks>
/// <param name="What">One of <see cref="Modbot.Core.Data.Entities.CopyKinds"/>.</param>
/// <param name="Platform">Which platform would be changed: <c>vrchat</c> or <c>discord</c>.</param>
/// <param name="Name">The person as a moderator would recognise them, when a name is known.</param>
/// <param name="RoleName">The role's name on the platform being changed, for a role change.</param>
/// <param name="Why">One plain sentence saying what made this change necessary.</param>
public sealed record PlannedChange(
    string What,
    string Platform,
    string? VRChatUserId,
    string? DiscordUserId,
    string? Name,
    string? RoleName,
    string Why);

/// <summary>What one role sync pass did, or would do.</summary>
/// <param name="Given">Roles given to match the deciding side.</param>
/// <param name="Taken">Roles taken away to match the deciding side.</param>
/// <param name="Disagreed">Differences on pairs where nobody decides, recorded and left alone.</param>
/// <param name="Left">Changes this pass did not get to. The next pass carries on.</param>
/// <param name="Problem">The last change a platform refused, as a sentence, or null.</param>
public sealed record RoleSyncPass(
    int Given,
    int Taken,
    int Disagreed,
    int Left,
    string? Problem,
    IReadOnlyList<PlannedChange> Changes);

/// <summary>What one ban sync pass did.</summary>
/// <param name="Copied">Bans, unbans and removals copied to the other platform.</param>
/// <param name="Dropped">Events recognised as Modbot's own copies coming back, and left alone.</param>
/// <param name="NotLinked">Events about somebody with no link to the other platform.</param>
/// <param name="Left">Facts this pass did not get to. The next pass carries on.</param>
public sealed record BanSyncPass(
    int Copied,
    int Dropped,
    int NotLinked,
    int Left,
    string? Problem,
    IReadOnlyList<PlannedChange> Changes);

/// <summary>
/// What a first run would do, on both halves at once.
/// </summary>
/// <param name="Total">Every change found, even if <see cref="Changes"/> was cut short.</param>
/// <param name="Changes">The changes themselves, up to the screen's limit.</param>
/// <param name="Problem">Why the answer is incomplete — an unreadable ban list, a missing permission.</param>
public sealed record SyncPreview(
    int Total,
    IReadOnlyList<PlannedChange> Changes,
    string? Problem);

/// <summary>The platform a change lands on, as the plan and the API spell it.</summary>
public static class SyncPlatforms
{
    public const string VRChat = "vrchat";
    public const string Discord = "discord";
}
