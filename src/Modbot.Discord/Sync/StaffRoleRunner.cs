using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Discord;
using Modbot.Discord.Bot;

namespace Modbot.Discord.Sync;

/// <summary>The settings screen's Apply button: one staff role pass now, past the brake.</summary>
/// <remarks>A singleton over the hosted bot, like <see cref="DiscordSyncRunner"/>.</remarks>
public sealed class StaffRoleRunner : IStaffRoleRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;

    public StaffRoleRunner(IServiceScopeFactory scopes, DiscordBotService bot)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);

        _scopes = scopes;
        _bot = bot;
    }

    public async Task<StaffRolePass> ApplyAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<StaffRoleSync>();

        return await sync.RunAsync(_bot.ReadyGateway, _bot.MembersRead, pastBrake: true, ct).ConfigureAwait(false);
    }
}
