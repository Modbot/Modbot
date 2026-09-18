namespace Modbot.Core.Discord;

/// <summary>The platform a change lands on, as the plan and the API spell it.</summary>
public static class SyncPlatforms
{
    public const string VRChat = "vrchat";
    public const string Discord = "discord";
}

/// <summary>
/// One change a sync would make, or has made.
/// </summary>
/// <remarks>
/// The same shape whether it was applied or only worked out, which is what makes a dry run
/// trustworthy: the list an operator reads before switching a sync on is produced by the code that
/// will do the work, not by a second description of it (M5 §7).
/// </remarks>
/// <param name="What">One of <c>CopyKinds</c>, or <c>disagree</c> for a pair nobody decides.</param>
/// <param name="Platform">Which platform would be changed, from <see cref="SyncPlatforms"/>.</param>
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
/// <param name="Found">Every difference the pairs cover, whether or not this pass acted on it.</param>
/// <param name="Given">Roles given to match the deciding side.</param>
/// <param name="Taken">Roles taken away to match the deciding side.</param>
/// <param name="Disagreed">Differences on pairs where nobody decides, recorded and left alone.</param>
/// <param name="Left">Changes this pass did not get to. The next pass carries on.</param>
/// <param name="Problem">The last change a platform refused, as a sentence, or null.</param>
public sealed record RoleSyncPass(
    int Found,
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
/// What a first run would do, or has done, across both halves.
/// </summary>
/// <param name="Total">Every change found, even if <see cref="Changes"/> was cut short.</param>
/// <param name="Changes">The changes themselves, up to the screen's limit.</param>
/// <param name="Problem">Why the answer is incomplete — an unreadable ban list, a missing permission.</param>
public sealed record SyncPreview(
    int Total,
    IReadOnlyList<PlannedChange> Changes,
    string? Problem);

/// <summary>
/// Working out what role and ban sync would do, and doing it on demand.
/// </summary>
/// <remarks>
/// In Core so the settings endpoints can offer the dry run without the API project depending on
/// the bot, the same arrangement the AI moderation actions already use. A process with no bot
/// answers that there is none, rather than pretending there is nothing to do.
/// </remarks>
public interface IDiscordSyncRunner
{
    /// <summary>
    /// Everything the two syncs would change right now, changing nothing.
    /// </summary>
    /// <remarks>
    /// This is the blast radius M5 §7 asks to be shown before a sync is switched on. Role
    /// differences come from comparing both sides; ban differences come from comparing the two
    /// ban lists, which costs a read of Discord's.
    /// </remarks>
    Task<SyncPreview> PreviewAsync(CancellationToken ct = default);

    /// <summary>
    /// Copies the roles and bans that are already different — the backlog a first run has to deal
    /// with, which the minute-by-minute passes deliberately never touch.
    /// </summary>
    Task<SyncPreview> CatchUpAsync(CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. There is nothing to work out and nothing to run.</summary>
public sealed class NoDiscordSyncRunner : IDiscordSyncRunner
{
    private const string NoBot = "The Discord bot is not running.";

    public Task<SyncPreview> PreviewAsync(CancellationToken ct = default)
        => Task.FromResult(new SyncPreview(0, [], NoBot));

    public Task<SyncPreview> CatchUpAsync(CancellationToken ct = default)
        => Task.FromResult(new SyncPreview(0, [], NoBot));
}
