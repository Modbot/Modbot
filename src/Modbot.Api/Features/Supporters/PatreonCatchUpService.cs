using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Supporters;
using Serilog;

namespace Modbot.Api.Features.Supporters;

/// <summary>
/// Runs <see cref="PatreonCatchUp"/> every half minute (Patreon design, step 2).
/// </summary>
/// <remarks>
/// Its own loop, apart from the Discord bot's, so a patron's Discord account is looked up whether or not the bot
/// is connected, and a limit from Patreon stops nothing else. A run with Patreon off, or its keys not saved,
/// reads the settings row and nothing more.
/// </remarks>
public sealed class PatreonCatchUpService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public PatreonCatchUpService(
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
                var result = await scope.ServiceProvider.GetRequiredService<PatreonCatchUp>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);

                if (result.CaughtUp || result.Looked > 0)
                {
                    _log.Information(
                        "Patreon: read {Members} patrons and looked up {Looked}; {Taken} supporters changed",
                        result.Members, result.Looked, result.Taken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The Patreon catch-up failed; it will run again");
            }

            try
            {
                await _delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
