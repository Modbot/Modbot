using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.Sync;

/// <summary>
/// Runs the role and ban sync passes, the staff role pass, and the roles from lists pass, once a
/// minute while the bot has a ready session.
/// </summary>
/// <remarks>
/// <para>
/// Its own loop, like the moderation log, the instance cards and the linked-member roles, so that a
/// server where the bot has lost a permission holds none of them up. Both halves run in the same
/// pass because they read the same settings and both cap their own work; a pass where nothing is
/// switched on costs one settings read.
/// </para>
/// <para>
/// Neither half catches up a backlog on its own. Ban sync starts from the moment a direction was
/// switched on, and the bans that were already different are copied only when an operator presses
/// the button for it, having seen the list first (M5 §7).
/// </para>
/// </remarks>
public sealed class DiscordSyncService : BackgroundService
{
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly DiscordBotStatus _status;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public DiscordSyncService(
        IServiceScopeFactory scopes,
        DiscordBotService bot,
        DiscordBotStatus status,
        IModbotClock clock,
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

                    var bans = scope.ServiceProvider.GetRequiredService<BanSync>();
                    var pass = await bans.RunAsync(gateway, stoppingToken).ConfigureAwait(false);

                    if (pass.Copied > 0)
                        _log.Information("Copied {Copied} bans or unbans to the other platform", pass.Copied);

                    if (pass.Problem is { } banProblem)
                        _status.Problem($"Could not copy a ban: {banProblem}", _clock.UtcNow);

                    var roles = scope.ServiceProvider.GetRequiredService<RoleSync>();
                    var rolePass = await roles.RunAsync(gateway, apply: true, stoppingToken).ConfigureAwait(false);

                    if (rolePass.Given + rolePass.Taken > 0)
                        _log.Information("Gave {Given} and took away {Taken} paired roles", rolePass.Given, rolePass.Taken);

                    if (rolePass.Problem is { } roleProblem)
                        _status.Problem($"Could not change a paired role: {roleProblem}", _clock.UtcNow);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.Warning(e, "The role and ban sync pass failed; trying again shortly");
                }

                // Its own try and its own scope, so a failure in either half never stops the other.
                // Its own switch too: while it is off the pass reads one settings row and stops.
                try
                {
                    using var scope = _scopes.CreateScope();

                    var lists = scope.ServiceProvider.GetRequiredService<ListRoleSync>();
                    var listPass = await lists.RunAsync(gateway, stoppingToken).ConfigureAwait(false);

                    if (listPass.Given + listPass.Taken > 0)
                        _log.Information("Gave {Given} and took away {Taken} Discord roles from lists", listPass.Given, listPass.Taken);

                    if (listPass.Problem is { } listProblem)
                        _status.Problem($"Roles from lists: {listProblem}", _clock.UtcNow);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.Warning(e, "The roles from lists pass failed; trying again shortly");
                }
            }

            // The staff role pass runs with the bot down as well: it gives and takes nothing then,
            // and after a while says that member updates are missing (staff roles design §6.1).
            try
            {
                using var scope = _scopes.CreateScope();

                var staff = scope.ServiceProvider.GetRequiredService<StaffRoleSync>();
                var staffPass = await staff.RunAsync(
                        _bot.ReadyGateway, _bot.MemberUpdatesCurrent, pastBrake: false, stoppingToken, startedAt: _bot.StartedAt)
                    .ConfigureAwait(false);

                if (staffPass.Given + staffPass.Taken > 0)
                    _log.Information("Gave {Given} and took away {Taken} staff roles from Discord roles", staffPass.Given, staffPass.Taken);

                if (staffPass.Problem is { } staffProblem)
                    _status.Problem($"Staff roles from Discord: {staffProblem}", _clock.UtcNow);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.Warning(e, "The staff role pass failed; trying again shortly");
            }

            try
            {
                await Task.Delay(PassInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
