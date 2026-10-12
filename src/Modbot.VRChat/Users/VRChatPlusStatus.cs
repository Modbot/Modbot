using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.VRChat.Users;

/// <summary>
/// Keeps whether the VRChat account has VRChat+ (<see cref="Settings.VRChatHasPlus"/>) and, when it
/// is known to have none, turns Upload VRChat pictures off by itself.
/// </summary>
/// <remarks>
/// VRChat takes a picture upload only from an account with VRChat+, so a switch left on for an
/// account that lost it would only send uploads VRChat refuses. The one place both ways of learning
/// the answer (signing in, and Check again in Settings) go through, so the rule is the same for both.
/// Not known (null) changes nothing: <see cref="VRChatPlus.FromTags"/> never says false on a missing list.
/// </remarks>
public static class VRChatPlusStatus
{
    /// <summary>The name the audit entry gives the switch, the one Settings uses when a person changes it.</summary>
    public const string PictureUploadsSetting = "vrchatPictureUploads";

    /// <summary>
    /// Stores <paramref name="hasPlus"/> and saves the settings row. When it is false and Upload
    /// VRChat pictures is on, the switch goes off in the same save, and the change is written to the
    /// audit log with no person behind it and <c>automatic</c> set, as the post list updater marks
    /// its own edits.
    /// </summary>
    /// <param name="services">Scoped services of the same scope as <paramref name="db"/>.</param>
    /// <returns>True when the switch was turned off.</returns>
    public static async Task<bool> SaveAsync(
        IServiceProvider services, ModbotContext db, Settings settings, bool? hasPlus, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);

        settings.VRChatHasPlus = hasPlus;

        var turnOff = hasPlus == false && settings.VRChatPictureUploads;
        if (turnOff)
            settings.VRChatPictureUploads = false;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (!turnOff)
            return false;

        var clock = services.GetRequiredService<IModbotClock>();
        var now = clock.UtcNow;

        await services.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(now, ct).ConfigureAwait(false);
        await services.GetRequiredService<IFactWriter>().WriteAsync(
            new FactRecord
            {
                Type = FactType.SettingsChanged,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = "settings",
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["setting"] = PictureUploadsSetting,
                    ["before"] = true,
                    ["after"] = false,
                    ["automatic"] = true,
                    ["reason"] = "VRChat+ is off for the account",
                },
            },
            ct).ConfigureAwait(false);

        return true;
    }
}
