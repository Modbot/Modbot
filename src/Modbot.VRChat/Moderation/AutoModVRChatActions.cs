using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Moderation;

namespace Modbot.VRChat.Moderation;

/// <summary>
/// Bans a person from the managed group, or removes them from it, for an AutoMod rule set to act
/// (AutoMod design §5).
/// </summary>
/// <remarks>
/// <para>
/// Through <see cref="GroupModeration"/> and so through the gate, like the buttons a moderator
/// presses (M4 §4): the same endpoint class, the same rule that nothing is recorded as done unless
/// VRChat accepted it, and no retry on a 429 (spec 4.3.1). What it does not share with the buttons
/// is the confirmation key and the case file: a rule has no dialog to press twice, and the flag and
/// the action fact are its record.
/// </para>
/// <para>
/// Modbot will not act on the account it signs in as, for the reason the button refuses to: taking
/// that account out of the group takes away the access every sync depends on.
/// </para>
/// </remarks>
public sealed class AutoModVRChatActions : IVRChatModerationActions
{
    private readonly ModbotContext _db;
    private readonly GroupModeration _vrchat;

    public AutoModVRChatActions(ModbotContext db, GroupModeration vrchat)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(vrchat);

        _db = db;
        _vrchat = vrchat;
    }

    public Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => ActAsync(userId, (group, ct2) => Outcome(_vrchat.BanAsync(group, userId, ct2)), ct);

    public Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        => ActAsync(userId, (group, ct2) => Outcome(_vrchat.KickAsync(group, userId, ct2)), ct);

    private async Task<VRChatActionOutcome> ActAsync(
        string userId, Func<string, CancellationToken, Task<VRChatActionOutcome>> act, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return VRChatActionOutcome.Failed("There is no VRChat id to act on.");

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.ManagedGroupId, s.VRChatSessionUserId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
            return VRChatActionOutcome.Failed("No VRChat group is set up yet, so there is nothing to act in.");

        // Ordinal: the id is opaque text and is compared as text (foundation §3.1.1).
        if (settings.VRChatSessionUserId is { Length: > 0 } self && string.Equals(self, userId, StringComparison.Ordinal))
            return VRChatActionOutcome.Failed("That is the account Modbot signs in as. Modbot will not act on itself.");

        return await act(groupId, ct).ConfigureAwait(false);
    }

    private static async Task<VRChatActionOutcome> Outcome<T>(Task<VRChatResult<T>> call)
    {
        var result = await call.ConfigureAwait(false);

        return result.Success
            ? VRChatActionOutcome.Ok
            : VRChatActionOutcome.Failed(result.ErrorMessage ?? "VRChat did not say why.");
    }
}
