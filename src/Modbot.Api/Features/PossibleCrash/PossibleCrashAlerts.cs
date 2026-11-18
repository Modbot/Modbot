using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.PossibleCrash;

/// <summary>
/// Raises the possible crash alert when many people leave one instance within a few seconds
/// (problem user alerts design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same hub, the same card family, the same Discord post as the other flagged
/// alerts.</strong> The moderators standing in that instance are told, the reporter included: the
/// reporter's own client cannot know what the other companions' logs said. A failed Discord post is
/// logged and never stops the headset card, which has already gone out.
/// </para>
/// <para>
/// <strong>Every batch goes through the watch,</strong> leaves or not, because the arrivals it
/// remembers are what the next batch with leaves in it names.
/// </para>
/// </remarks>
public static class PossibleCrashAlerts
{
    public static async Task RaiseAsync(
        IReadOnlyList<FactRecord> written,
        IReadOnlyList<FactWriteResult> results,
        ICompanionDeviceStore devices,
        AlertHub alerts,
        PossibleCrashWatch watch,
        IDiscordChannelPoster? discord,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        var fresh = written.Where((_, index) => !results[index].WasDeduplicated).ToList();

        var rules = FlagRuleSettings.Read(
            await database.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.FlagRules).FirstOrDefaultAsync(ct));

        if (!rules.CrashAlert)
            return;

        var found = watch.Observe(fresh, rules.CrashLeaves, rules.CrashSeconds, clock.UtcNow);

        if (found.Count == 0)
            return;

        var paired = await devices.ListDevicesAsync(ct);
        var recipients = paired.Where(d => !d.IsRevoked).Select(d => d.Id).ToList();

        foreach (var crash in found)
        {
            alerts.Raise(
                AlertHub.ForPossibleCrash(clock, crash.InstanceId, crash.WorldId, crash.Left, rules.CrashSeconds, crash.People),
                Guid.Empty,
                recipients,
                clock.UtcNow);

            if (discord is null)
                continue;

            try
            {
                await FlaggedDiscordPost.PostAsync(
                    discord, rules, FlaggedDiscordPost.CrashText(crash.Left, rules.CrashSeconds, crash.People.Select(p => p.DisplayName ?? p.SubjectId).ToList()), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The headset card has already gone out.
                Serilog.Log.Warning(e, "Could not post a possible crash in Discord");
            }
        }
    }
}
