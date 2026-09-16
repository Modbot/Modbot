using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Configuration;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Core.Cloud;

/// <summary>
/// Registers this server with Modbot Cloud and reports on a schedule (central services spec 5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every failure here is debug, never a warning or an error.</strong> Modbot is self-hosted
/// software that happens to talk to an optional service; an operator whose logs fill with errors
/// because somebody else's server is down would reasonably conclude their install is broken. The
/// Health page is where a failure is shown, because that is where somebody has gone to look.
/// </para>
/// <para>
/// Nothing in Modbot waits on this. A Cloud that is unreachable, slow or permanently gone changes
/// nothing about how the deployment behaves.
/// </para>
/// </remarks>
public sealed class ServerReportingService(
    IServiceScopeFactory scopes,
    ModbotCloudAddress cloud,
    ILogger log)
    : BackgroundService
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromHours(6);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    /// <summary>
    /// Startup is not the moment to make a network call. Nothing here is urgent, and a Cloud that is
    /// slow must not be part of how long Modbot takes to become useful.
    /// </summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    private readonly ILogger _log = log.ForContext(LogArea.Name, LogArea.Http);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Checked once: MODBOT_CLOUD_DISABLED is an environment variable, so it cannot change while
        // the process runs.
        if (cloud.Disabled)
        {
            _log.Debug("MODBOT_CLOUD_DISABLED is set, so nothing is reported to Modbot Cloud");
            return;
        }

        await Task.Delay(FirstDelay, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = await TryReportAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(sent ? ReportInterval : RetryDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryReportAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var reporter = scope.ServiceProvider.GetRequiredService<ServerReporter>();

            var result = await reporter.ReportAsync(ct).ConfigureAwait(false);

            if (result.Ok)
                _log.Debug("Reported to Modbot Cloud");
            else
                _log.Debug("Could not report to Modbot Cloud: {Problem}", result.Problem);

            return result.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Debug, deliberately. See the class remarks.
            _log.Debug(ex, "Reporting to Modbot Cloud failed; Modbot is unaffected");
            return false;
        }
    }
}
