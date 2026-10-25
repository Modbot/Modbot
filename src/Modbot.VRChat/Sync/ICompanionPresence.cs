namespace Modbot.VRChat.Sync;

/// <summary>
/// Whether a moderator's companion is in one of the group's instances right now.
/// </summary>
/// <remarks>
/// Defined here and implemented by the Api project, which is the only place that knows where each
/// paired device is standing. The audit-log loop asks it, so a ban or kick made in front of a
/// companion is read soon enough for the clip request that follows it to still find the footage.
/// </remarks>
public interface ICompanionPresence
{
    /// <summary>True while at least one paired companion is believed to be in an instance.</summary>
    bool AnyInInstance();
}
