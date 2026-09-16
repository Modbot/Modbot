using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Configuration;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Core.Cloud;

/// <summary>
/// Sends the group's public rooms to Modbot Cloud: every five minutes, and whenever a room opens
/// or closes.
/// </summary>
/// <remarks>
/// <para>
/// Five minutes because the report is also what keeps the group on modbot.co — Cloud drops a
/// server's rooms once the reports stop — and because the nudge already covers the case anybody
/// would notice. A Modbot that is switched off, or that has the setting turned off, falls off the
/// page on its own within the aging-out window.
/// </para>
/// <para>
/// The loop is not started at all when this server may not talk to Cloud. There is nothing for it
/// to do in that case, and a background loop that wakes every five minutes to decide it has nothing
/// to do is a line in a profiler nobody can explain.
/// </para>
/// </remarks>
public sealed class PublicRoomsService(
    IServiceScopeFactory scopes,
    ModbotCloudAddress cloud,
    PublicRoomsNudge nudge,
    ILogger? log = null) : BackgroundService
{
    /// <summary>How long between reports when nothing has happened.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait after a failure before trying again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (cloud.Disabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await SendSafelyAsync(stoppingToken).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
                return;

            var wait = outcome == PublicRoomsOutcome.Failed ? RetryInterval : Interval;
            await nudge.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<PublicRoomsOutcome> SendSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<PublicRoomsSender>();
            return await sender.SendAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PublicRoomsOutcome.Off;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "The public rooms report failed; it will try again");
            return PublicRoomsOutcome.Failed;
        }
    }
}
