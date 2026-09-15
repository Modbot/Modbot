using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// Runs <see cref="ModerationLogPoster"/> on a timer, only while the bot has a ready session.
/// </summary>
/// <remarks>
/// Separate from the connection loop so a slow post never delays noticing a changed token, and
/// so the poster stays a plain class a test can call. Each pass gets its own scope, so its
/// <c>ModbotContext</c> is short-lived like a request's.
/// </remarks>
public sealed class ModerationLogService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly DiscordBotStatus _status;
    private readonly IModbotClock _clock;
    private readonly ModerationLogOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public ModerationLogService(
        IServiceScopeFactory scopes,
        DiscordBotService bot,
        DiscordBotStatus status,
        IModbotClock clock,
        ModerationLogOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _bot = bot;
        _status = status;
        _clock = clock;
        _options = options ?? new ModerationLogOptions();
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = _options.PollInterval;

            if (_bot.ReadyGateway is { } gateway)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var poster = scope.ServiceProvider.GetRequiredService<ModerationLogPoster>();
                    // A refused channel waits on its own -- its retry time is on its row -- so one
                    // broken channel does not slow the others down.
                    await poster.RunOnceAsync(gateway, _delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.Error(e, "The Discord event posting pass failed; trying again shortly");
                    _status.Problem($"Posting events to Discord failed: {e.Message}", _clock.UtcNow);
                    wait = _options.RetryAfterFailure;
                }
            }

            try
            {
                await _delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
