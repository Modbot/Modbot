using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Core.Updates;

/// <summary>
/// Asks what the newest release is, shortly after start and every few hours after that.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This runs whatever <c>MODBOT_CLOUD_DISABLED</c> says.</strong> That variable turns off
/// the Cloud features — reporting, app logs, term lists, the public instances listing. Being told
/// that a newer Modbot exists is not one of them: nothing about the deployment is sent, and an
/// operator who turned Cloud off did not thereby ask to stop hearing about security fixes. The
/// switch that does turn this off is the operator's own, on the settings row.
/// </para>
/// <para>
/// Every failure is debug, never a warning. Same reasoning as <c>ServerReportingService</c>: an
/// operator whose logs fill with errors because somebody else's service is down would reasonably
/// conclude their install is broken. The settings screen is where a failed check is shown, because
/// that is where somebody has gone to look.
/// </para>
/// </remarks>
public sealed class UpdateCheckService(IServiceScopeFactory scopes, ILogger log) : BackgroundService
{
    /// <summary>
    /// Often enough that a server running for weeks hears about a release the same day, rarely
    /// enough to be nothing at the other end.
    /// </summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Startup is not the moment to make a network call, and nothing here is urgent. Sooner than
    /// the report's two minutes, though: the answer belongs on a screen an operator may open in
    /// the first minute after a deploy.
    /// </summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);

    private readonly ILogger _log = log.ForContext(LogArea.Name, LogArea.Http);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = FirstDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            wait = await TryCheckAsync(stoppingToken).ConfigureAwait(false) ? CheckEvery : RetryAfter;
        }
    }

    private async Task<bool> TryCheckAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var checker = scope.ServiceProvider.GetRequiredService<UpdateChecker>();

            var status = await checker.CheckAsync(ct).ConfigureAwait(false);

            if (!status.On)
            {
                _log.Debug("Checking for updates is turned off in settings");

                // Nothing was asked and nothing failed. Come back at the ordinary time in case
                // somebody turns it back on.
                return true;
            }

            if (status.Problem is { } problem)
            {
                _log.Debug("Could not check for a newer Modbot: {Problem}", problem);
                return false;
            }

            if (status.NewerAvailable)
            {
                _log.Information(
                    "Modbot {Version} is available; this server is running {Running}. Modbot does not update itself",
                    status.Newest,
                    status.Running);
            }
            else
            {
                _log.Debug("Checked for updates: {Running} is the newest", status.Running);
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Debug, deliberately. See the class remarks.
            _log.Debug(ex, "Checking for a newer Modbot failed; Modbot is unaffected");
            return false;
        }
    }
}
