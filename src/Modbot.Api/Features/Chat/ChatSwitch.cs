using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Chat;

/// <summary>
/// Whether Chat answers: AI is on and Chat is on (Settings → AI). One rule, read by the Chat page,
/// by sending, and by the signed-in person's own info, so the page list and the page agree: the
/// sidebar used to offer Chat to anybody with the permission, and with AI chat off the page it
/// led to only said "Chat is off." (review 2026-09-29).
/// </summary>
public static class ChatSwitch
{
    /// <summary>Whether Chat answers under these settings. Null settings, as on a fresh deployment, is off.</summary>
    public static bool On([NotNullWhen(true)] Core.Data.Entities.Settings? settings) => settings is { AiEnabled: true, AiChatEnabled: true };

    /// <summary>Whether Chat answers right now.</summary>
    public static async Task<bool> IsOnAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return On(await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct));
    }
}
