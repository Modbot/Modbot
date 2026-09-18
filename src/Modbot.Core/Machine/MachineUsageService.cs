using Microsoft.Extensions.Hosting;
using Serilog;

namespace Modbot.Core.Machine;

/// <summary>
/// Takes a reading every <see cref="MachineUsageSampler.Every"/>, so the settings screen has a
/// window to draw rather than one number taken when somebody opened it.
/// </summary>
/// <remarks>
/// <para>
/// It samples from the moment the host starts, without the usual settling delay other background
/// services take: the first reading is only a baseline, and starting late would mean the screen is
/// blank for whoever opens it right after a deploy — which is exactly when somebody is watching a
/// new build settle.
/// </para>
/// <para>
/// A reading that fails is never allowed to stop the loop or the host. It is said once, at debug,
/// because a host where the counters cannot be read will fail every ten seconds forever and an
/// operator does not need to be told 8,640 times a day that a diagnostic is unavailable.
/// </para>
/// </remarks>
public sealed class MachineUsageService(MachineUsageSampler sampler, ILogger log) : BackgroundService
{
    private bool _saidSo;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Sample();

        using var timer = new PeriodicTimer(MachineUsageSampler.Every);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                Sample();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void Sample()
    {
        try
        {
            sampler.Take(MachineReader.Read());
        }
        catch (Exception ex)
        {
            if (_saidSo)
                return;

            _saidSo = true;
            log.Debug(ex, "Could not read how hard this machine is working; Modbot is unaffected");
        }
    }
}
