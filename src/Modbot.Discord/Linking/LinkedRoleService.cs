using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.Linking;

/// <summary>
/// Runs <see cref="LinkedRoles"/> once a minute while the bot has a ready session, and at once when
/// a link is saved or ended or a member joins.
/// </summary>
/// <remarks>
/// Its own loop, like the moderation log and the instance cards, so a server where the bot has lost
/// Manage Roles does not hold up either of them.
/// </remarks>
public sealed class LinkedRoleService : BackgroundService
{
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly DiscordBotStatus _status;
    private readonly DiscordLinkSignal _signal;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public LinkedRoleService(
        IServiceScopeFactory scopes,
        DiscordBotService bot,
        DiscordBotStatus status,
        DiscordLinkSignal signal,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _bot = bot;
        _status = status;
        _signal = signal;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_bot.ReadyGateway is { } gateway)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var roles = scope.ServiceProvider.GetRequiredService<LinkedRoles>();
                    var pass = await roles.RunAsync(gateway, stoppingToken).ConfigureAwait(false);

                    if (pass.Given + pass.Removed > 0)
                        _log.Information("Gave {Given} and took away {Removed} linked member roles", pass.Given, pass.Removed);

                    if (pass.Problem is { } problem)
                        _status.Problem($"Could not change a linked member's role: {problem}", _clock.UtcNow);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.Warning(e, "The linked member roles pass failed; trying again shortly");
                }
            }

            try
            {
                await _signal.WaitAsync(PassInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
