using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Discord;
using Modbot.Discord.Bot;

namespace Modbot.Discord.Sync;

/// <summary>
/// Answers "what would the sync do" and "do it now" for the settings screen, through the bot's live
/// session.
/// </summary>
/// <remarks>
/// A singleton over the hosted bot, like the AI moderation actions, because the caller is an HTTP
/// request and the two syncs are scoped services that need a scope of their own.
/// </remarks>
public sealed class DiscordSyncRunner : IDiscordSyncRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;

    public DiscordSyncRunner(IServiceScopeFactory scopes, DiscordBotService bot)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);

        _scopes = scopes;
        _bot = bot;
    }

    public Task<SyncPreview> PreviewAsync(CancellationToken ct = default) => BothAsync(apply: false, ct);

    public Task<SyncPreview> CatchUpAsync(CancellationToken ct = default) => BothAsync(apply: true, ct);

    private async Task<SyncPreview> BothAsync(bool apply, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var gateway = _bot.ReadyGateway;

        var roles = scope.ServiceProvider.GetRequiredService<RoleSync>();
        var bans = scope.ServiceProvider.GetRequiredService<BanSync>();

        // Roles first: a person about to be banned is better off not being given a role on the way
        // out, and the role pass is the one whose result an operator reads most closely.
        var rolePass = await roles.RunAsync(gateway, apply, ct).ConfigureAwait(false);
        var banPass = await bans.CatchUpAsync(gateway, apply, ct).ConfigureAwait(false);

        var changes = new List<PlannedChange>(rolePass.Changes);
        changes.AddRange(banPass.Changes);

        var total = rolePass.Found + banPass.Total;

        return new SyncPreview(total, changes, rolePass.Problem ?? banPass.Problem);
    }
}
