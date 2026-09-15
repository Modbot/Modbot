using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Moderation;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.AI.Moderation;

/// <summary>
/// Fetching a subscribed Hub list again, and applying a newer version when the operator says so
/// (AI moderation design §9, foundation §4.2.7).
/// </summary>
public sealed class HubTermListUpdates
{
    private readonly HubTermLists _hub;
    private readonly IModbotClock _clock;

    public HubTermListUpdates(HubTermLists hub, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(clock);

        _hub = hub;
        _clock = clock;
    }

    /// <summary>
    /// Fetches the list and, when it differs from what is in use, stores it as available. Nothing
    /// that matches changes here. The caller saves.
    /// </summary>
    public async Task RefreshAsync(ModerationTermList list, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (list.Source != TermListSource.Cloud || list.HubId is null)
            return;

        var fetched = await _hub.FetchAsync(list.HubId, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        if (fetched.Error is not null)
        {
            list.HubError = fetched.Error.Length <= 500 ? fetched.Error : fetched.Error[..500];
            return;
        }

        list.HubError = null;
        list.HubFetchedAt = now;

        var changes = HubTermLists.Compare(StoredTerm.ParseList(list.Terms), fetched.Terms);

        if (!changes.Any && fetched.Version == list.HubVersion)
        {
            list.HubAvailableVersion = null;
            list.HubAvailableTerms = null;
            list.HubAvailableChanges = null;
            return;
        }

        list.HubAvailableVersion = fetched.Version ?? "unknown";
        list.HubAvailableTerms = StoredTerm.Serialize(fetched.Terms);
        list.HubAvailableChanges = JsonSerializer.Serialize(changes, StoredTerm.Json);
    }

    /// <summary>Puts the available version into use. Switched-off terms stay off by id. The caller saves.</summary>
    public bool Apply(ModerationTermList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (list.HubAvailableTerms is null)
            return false;

        list.Terms = list.HubAvailableTerms;
        list.HubVersion = list.HubAvailableVersion;
        list.HubAvailableVersion = null;
        list.HubAvailableTerms = null;
        list.HubAvailableChanges = null;
        list.UpdatedAt = _clock.UtcNow;
        return true;
    }
}

/// <summary>Checks every subscribed Hub list for a newer version every six hours (design §9).</summary>
public sealed class HubTermListRefreshService : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Moderation);

    public HubTermListRefreshService(IServiceScopeFactory scopes)
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

            wait = Every;

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
                var updates = scope.ServiceProvider.GetRequiredService<HubTermListUpdates>();

                var lists = await db.ModerationTermLists
                    .Where(l => l.Source == TermListSource.Cloud)
                    .ToListAsync(stoppingToken).ConfigureAwait(false);

                foreach (var list in lists)
                    await updates.RefreshAsync(list, stoppingToken).ConfigureAwait(false);

                await db.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _log.Warning(e, "Checking Modbot Hub term lists for updates failed");
            }
        }
    }
}

/// <summary>
/// Checks VRChat profile text as the profile sync records it (design §8).
/// </summary>
/// <remarks>
/// Reads the profile facts after a stored position rather than being called from the profile
/// sync, so an AI call that takes ten seconds does not hold up fetching the next profile. Only the
/// fields a change fact names are checked, so an avatar change costs no AI call.
/// </remarks>
public sealed class ProfileModerationPass
{
    public const int BatchSize = 200;

    private readonly ModbotContext _db;
    private readonly IModerationChecker _checker;

    public ProfileModerationPass(ModbotContext db, IModerationChecker checker)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(checker);

        _db = db;
        _checker = checker;
    }

    /// <summary>One batch. Returns how many facts were read; zero means caught up or switched off.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.AiModerationEnabled)
            return 0;

        var cursor = settings.AiModerationProfileFactsReadThrough;

        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.Id > cursor
                        && (e.Type == FactType.UserProfileFirstSeen || e.Type == FactType.UserProfileChanged)
                        && e.SubjectPlatform == FactPlatform.VRChat)
            .OrderBy(e => e.Id)
            .Take(BatchSize)
            .Select(e => new { e.Id, e.Type, e.SubjectId, e.Data })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
            return 0;

        // One check per person per batch, over every field any of their facts named.
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!wanted.TryGetValue(row.SubjectId, out var fields))
                wanted[row.SubjectId] = fields = new HashSet<string>(StringComparer.Ordinal);

            if (row.Type == FactType.UserProfileFirstSeen)
            {
                fields.UnionWith(["displayName", "bio", "statusDescription", "pronouns"]);
                continue;
            }

            try
            {
                if (JsonNode.Parse(row.Data)?["changed"] is JsonObject changed)
                    fields.UnionWith(changed.Select(p => p.Key));
            }
            catch (JsonException)
            {
                // A change fact whose data cannot be read names no fields.
            }
        }

        var ids = wanted.Keys.ToList();
        var profiles = await _db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .Select(u => new { u.UserId, u.DisplayName, u.Bio, u.StatusDescription, u.Pronouns })
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var profile in profiles)
        {
            var fields = wanted[profile.UserId];

            await _checker.CheckProfileAsync(
                new ProfileToCheck(
                    profile.UserId,
                    fields.Contains("displayName") ? profile.DisplayName : null,
                    fields.Contains("bio") ? profile.Bio : null,
                    fields.Contains("statusDescription") ? profile.StatusDescription : null,
                    fields.Contains("pronouns") ? profile.Pronouns : null),
                ct).ConfigureAwait(false);
        }

        settings.AiModerationProfileFactsReadThrough = rows[^1].Id;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return rows.Count;
    }
}

/// <summary>Runs <see cref="ProfileModerationPass"/> once a minute, and straight away again while there is more.</summary>
public sealed class ProfileModerationService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Moderation);

    public ProfileModerationService(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                int read;
                do
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    read = await scope.ServiceProvider.GetRequiredService<ProfileModerationPass>()
                        .RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                while (read == ProfileModerationPass.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _log.Warning(e, "Checking VRChat profiles against moderation rules failed");
            }
        }
    }
}
