using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Chat;

/// <summary>Which of Chat's parts answer right now, for the signed-in person's own info.</summary>
/// <param name="ChatOn">The Chat page answers (<see cref="ChatSwitch.On"/>).</param>
/// <param name="BriefsOn">The popups offer an AI brief (<see cref="ChatSwitch.BriefsOn"/>).</param>
public sealed record AiSwitches(bool ChatOn, bool BriefsOn);

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

    /// <summary>
    /// Whether the popups offer an AI brief (AI chat design §14): Chat answers and briefs are on. A
    /// brief is a Chat call, so with Chat off there is none either.
    /// </summary>
    public static bool BriefsOn([NotNullWhen(true)] Core.Data.Entities.Settings? settings) => On(settings) && settings.AiBriefsEnabled;

    /// <summary>Both switches right now, in one read.</summary>
    public static async Task<AiSwitches> ReadAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return new AiSwitches(On(settings), BriefsOn(settings));
    }
}
