using Microsoft.Extensions.Hosting;

namespace Modbot.Cloud.Features.Updates;

/// <summary>Fetches the release news every few minutes, so no request ever waits on GitHub.</summary>
/// <remarks>
/// The first fetch is immediate rather than delayed: this is the one thing a Modbot that has just
/// started may ask for straight away, and a Cloud that has been up for five minutes with nothing to
/// say would answer 503 to all of them.
/// </remarks>
public sealed class UpdateRefreshService(LatestReleases releases, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Failures are the class's own business: it keeps what it had and logs at debug.
                await releases.RefreshAsync(stoppingToken).ConfigureAwait(false);

                await Task.Delay(LatestReleases.RefreshEvery, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
