using Modbot.Core.Moderation;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>
/// Records what the sync asked VRChat to do, and never opens a socket.
/// </summary>
public sealed class FakeVRChatActions : IVRChatModerationActions
{
    /// <summary>Every group action asked for, in order: what, who, and the role where there is one.</summary>
    public List<(string What, string UserId, string? RoleId)> Actions { get; } = [];

    /// <summary>Set to make every action fail with this sentence.</summary>
    public string? Error { get; set; }

    public Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Do("ban", userId, null);

    public Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Do("remove", userId, null);

    public Task<VRChatActionOutcome> UnbanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => Do("unban", userId, null);

    public Task<VRChatActionOutcome> GiveGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
        => Do("role-given", userId, roleId);

    public Task<VRChatActionOutcome> TakeGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
        => Do("role-taken", userId, roleId);

    private Task<VRChatActionOutcome> Do(string what, string userId, string? roleId)
    {
        if (Error is { } error)
            return Task.FromResult(VRChatActionOutcome.Failed(error));

        Actions.Add((what, userId, roleId));
        return Task.FromResult(VRChatActionOutcome.Ok);
    }
}
