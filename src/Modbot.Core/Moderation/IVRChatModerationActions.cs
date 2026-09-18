namespace Modbot.Core.Moderation;

/// <summary>Whether VRChat did what was asked, and if not, why.</summary>
public sealed record VRChatActionOutcome(bool Done, string? Error)
{
    public static VRChatActionOutcome Ok { get; } = new(true, null);

    public static VRChatActionOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// The two things an AutoMod rule may do in the managed VRChat group (AutoMod design §5): ban a
/// person from it, and remove them from it.
/// </summary>
/// <remarks>
/// In Core so the engine in <c>Modbot.Moderation</c> can ask without depending on the VRChat
/// project. The real one goes through the gate like every other VRChat call; a process without the
/// gate answers a failure at once, and the flag records that nothing happened.
/// </remarks>
public interface IVRChatModerationActions
{
    Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default);

    Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default);
}

/// <summary>A process with no VRChat gate. Every action fails and says so.</summary>
public sealed class NoVRChatModerationActions : IVRChatModerationActions
{
    private const string NoGate = "This deployment is not set up to act in VRChat.";

    public Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Task.FromResult(VRChatActionOutcome.Failed(NoGate));

    public Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Task.FromResult(VRChatActionOutcome.Failed(NoGate));
}
