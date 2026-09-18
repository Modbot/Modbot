namespace Modbot.Core.Moderation;

/// <summary>Whether VRChat did what was asked, and if not, why.</summary>
public sealed record VRChatActionOutcome(bool Done, string? Error)
{
    public static VRChatActionOutcome Ok { get; } = new(true, null);

    public static VRChatActionOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// What Modbot may do to a person in the managed VRChat group without going through the buttons a
/// moderator presses: ban them, remove them, lift a ban, and move a group role.
/// </summary>
/// <remarks>
/// <para>
/// In Core so the AutoMod engine in <c>Modbot.Moderation</c> and the role and ban sync in
/// <c>Modbot.Discord</c> can ask without either of them depending on the VRChat project. The real
/// one goes through the gate like every other VRChat call; a process without the gate answers a
/// failure at once, and the flag or the copy record shows that nothing happened.
/// </para>
/// <para>
/// The first two are AutoMod design §5; the last three arrived with role and ban sync (M5 §3, §4).
/// </para>
/// </remarks>
public interface IVRChatModerationActions
{
    Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default);

    Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default);

    /// <summary>Lifts a group ban.</summary>
    Task<VRChatActionOutcome> UnbanFromGroupAsync(string userId, string reason, CancellationToken ct = default);

    /// <summary>Gives a member one of the group's roles.</summary>
    Task<VRChatActionOutcome> GiveGroupRoleAsync(string userId, string roleId, CancellationToken ct = default);

    /// <summary>Takes one of the group's roles away from a member.</summary>
    Task<VRChatActionOutcome> TakeGroupRoleAsync(string userId, string roleId, CancellationToken ct = default);
}

/// <summary>A process with no VRChat gate. Every action fails and says so.</summary>
public sealed class NoVRChatModerationActions : IVRChatModerationActions
{
    private const string NoGate = "This deployment is not set up to act in VRChat.";

    public Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Task.FromResult(VRChatActionOutcome.Failed(NoGate));

    public Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Task.FromResult(VRChatActionOutcome.Failed(NoGate));

    public Task<VRChatActionOutcome> UnbanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Task.FromResult(VRChatActionOutcome.Failed(NoGate));

    public Task<VRChatActionOutcome> GiveGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
        => Task.FromResult(VRChatActionOutcome.Failed(NoGate));

    public Task<VRChatActionOutcome> TakeGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
        => Task.FromResult(VRChatActionOutcome.Failed(NoGate));
}
