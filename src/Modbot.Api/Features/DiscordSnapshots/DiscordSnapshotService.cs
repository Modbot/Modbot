using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Api.Features.DiscordSnapshots;

/// <summary>
/// Runs <see cref="DiscordSnapshotPass"/> every ten minutes. A pass with the schedule off reads the
/// settings row and nothing more.
/// </summary>
public sealed class DiscordSnapshotService : BackgroundService
{
    /// <summary>How long between two passes. Also how long a snapshot that could not be taken waits before it is tried again.</summary>
    public static readonly TimeSpan PassEvery = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public DiscordSnapshotService(
        IServiceScopeFactory scopes,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DiscordSnapshotPass>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The server snapshot pass failed; it will run again");
            }

            try
            {
                await _delay(PassEvery, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
