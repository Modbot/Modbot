using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Moderation;

/// <summary>The daily AI call limit for AutoMod (AI moderation design §4.2).</summary>
public static class AiCallAllowance
{
    /// <summary>
    /// Takes one call from today's allowance, or answers false when there is none left.
    /// </summary>
    /// <remarks>
    /// One statement that checks and counts, so two checks running at once cannot both take the
    /// last call. A new UTC day starts the count again at one.
    /// </remarks>
    public static async Task<bool> TryUseAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var updated = await db.Settings
            .Where(s => s.Id == 1
                        && s.AiModerationDailyCallLimit > 0
                        && (s.AiModerationCallsDay != today || s.AiModerationCallsUsed < s.AiModerationDailyCallLimit))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.AiModerationCallsUsed, s => s.AiModerationCallsDay == today ? s.AiModerationCallsUsed + 1 : 1)
                .SetProperty(s => s.AiModerationCallsDay, today), ct)
            .ConfigureAwait(false);

        return updated == 1;
    }

    /// <summary>How many calls today has used.</summary>
    public static int UsedToday(Settings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AiModerationCallsDay == DateOnly.FromDateTime(now.UtcDateTime) ? settings.AiModerationCallsUsed : 0;
    }

    public const string Reached = "The daily AI call limit is reached.";
}
