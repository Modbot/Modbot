using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Modbot.Core.Logging.Store;

/// <summary>
/// Sends Modbot's log to Modbot Cloud, a batch at a time, in the background.
/// </summary>
/// <remarks>
/// <para>
/// It never blocks anything: this is its own task, and every failure is a longer wait rather than a
/// thrown exception. A deployment that cannot reach Cloud works exactly as well as one that can.
/// </para>
/// <para>
/// The backoff doubles from thirty seconds to ten minutes and honours a <c>Retry-After</c> when
/// Cloud sends one. That is Cloud's own limit, not VRChat's cold stop (foundation 4.3.1), and the
/// two must not be confused: waiting longer here costs nothing but freshness.
/// </para>
/// <para>
/// A pass that sent a full batch comes straight back for the next one, so a deployment catching up
/// after being offline catches up at the speed of the network rather than one batch a minute.
/// </para>
/// </remarks>
public sealed class CloudLogShipService : BackgroundService
{
    /// <summary>How long after starting the first pass runs. Long enough for the log table to exist.</summary>
    public static readonly TimeSpan FirstRun = TimeSpan.FromSeconds(30);

    /// <summary>The wait after a pass with nothing to send.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(60);

    /// <summary>The wait after a pass that sent a full batch: there is more waiting.</summary>
    public static readonly TimeSpan MoreToSend = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);

    private TimeSpan _backoff = FirstBackoff;

    public CloudLogShipService(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = FirstRun;

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

            wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var shipper = scope.ServiceProvider.GetRequiredService<CloudLogShipper>();

            var result = await shipper.RunOnceAsync(ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case ShipOutcome.Off:
                    // Nothing to do and nothing to say. Checked again in a minute, because the
                    // switch is a setting somebody can turn back on without restarting Modbot.
                    _backoff = FirstBackoff;
                    return Quiet;

                case ShipOutcome.Sent:
                    _backoff = FirstBackoff;
                    return result.Lines >= CloudLogShipper.BatchSize ? MoreToSend : Quiet;

                case ShipOutcome.Failed:
                    var next = result.RetryAfter is { } asked && asked > TimeSpan.Zero
                        ? Cap(asked)
                        : _backoff;

                    _backoff = Cap(_backoff * 2);
                    return next;

                default:
                    _backoff = FirstBackoff;
                    return Quiet;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Quiet;
        }
        catch (Exception e)
        {
            // Warning, not Error: a deployment that cannot reach Cloud is working perfectly well.
            _log.Warning(e, "Sending logs to Modbot Cloud failed");

            var next = _backoff;
            _backoff = Cap(_backoff * 2);

            return next;
        }
    }

    private static TimeSpan Cap(TimeSpan wait) => wait > LongestBackoff ? LongestBackoff : wait;
}
