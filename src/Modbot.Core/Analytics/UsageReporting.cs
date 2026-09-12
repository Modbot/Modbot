using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Core.Analytics;

/// <summary>
/// Everything the usage reporter needs to know, supplied by whatever owns configuration.
/// </summary>
/// <remarks>
/// An interface rather than a direct <c>Settings</c> dependency so <c>Modbot.Core</c> does not have
/// to know where configuration lives. The real implementation reads the settings singleton once the
/// onboarding wizard exists.
/// </remarks>
public interface IUsageConfiguration
{
    /// <summary>Opt-out, default on, answered during onboarding (central services spec 5.1).</summary>
    bool AnalyticsEnabled { get; }

    /// <summary>Random UUID this deployment assigned itself on first boot. Never derived from anything.</summary>
    string InstanceId { get; }

    /// <summary>This deployment's own public URL, as the operator entered it.</summary>
    string? InstanceUrl { get; }

    /// <summary>Where to report. Configurable so an operator can point it elsewhere or nowhere.</summary>
    string HubUrl { get; }

    /// <summary>Builds the current report. Returns null when there is nothing meaningful to send yet.</summary>
    Task<UsageSnapshot?> CaptureAsync(CancellationToken ct);
}

/// <summary>
/// The complete analytics payload. Central services spec section 5.2.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is absent matters more than what is present.</strong> There is no group id, no group
/// name, no member identity, no moderation data, no fact, no profile text, no credential and no
/// VRChat instance id — not filtered out on the way, simply nowhere to put.
/// </para>
/// <para>
/// "We don't send X" is a promise somebody has to keep on every future change. "There is no field
/// for X" is checked by the compiler.
/// </para>
/// </remarks>
public sealed record UsageSnapshot
{
    public required string Version { get; init; }

    /// <summary>Bucketed member count — <c>&lt;1k</c>, <c>1k-10k</c>, <c>10k-50k</c>, <c>50k+</c>.</summary>
    /// <remarks>
    /// Never exact. An exact member count plus a publicly visible VRChat group size is an
    /// identifier, which would defeat the point of the instance id being random.
    /// </remarks>
    public string? ScaleBucket { get; init; }

    public int? PairedClients { get; init; }
    public bool? DiscordConnected { get; init; }

    /// <summary>Which lists are imported — the ids, never their contents.</summary>
    public IReadOnlyList<string>? TermListsImported { get; init; }

    /// <summary>
    /// Rate-limit cold stops since the last report. The most valuable field here: §4.3's limiter is
    /// built on estimates about an undocumented system, and a spike across many deployments after a
    /// VRChat change is how the project discovers its numbers are wrong. No single operator can see
    /// that pattern.
    /// </summary>
    public int? RateLimitColdStops { get; init; }

    public int? WafBlocks { get; init; }

    /// <summary>Buckets a member count. Public so the caller never invents its own boundaries.</summary>
    public static string Bucket(int members) => members switch
    {
        < 1_000 => "<1k",
        < 10_000 => "1k-10k",
        < 50_000 => "10k-50k",
        _ => "50k+",
    };
}

/// <summary>
/// Registers this deployment with Modbot Hub and reports usage periodically — but only when the
/// operator enabled analytics.
/// </summary>
/// <remarks>
/// <para>
/// Every failure here is <em>debug</em>, never a warning or an error. Modbot is self-hosted software
/// that happens to talk to an optional service; a operator whose logs fill with errors because
/// somebody else's server is down would reasonably conclude their install is broken.
/// </para>
/// <para>
/// Nothing in Modbot waits on this. It runs in the background, and a Hub that is unreachable,
/// slow or permanently gone changes nothing about how the deployment behaves.
/// </para>
/// </remarks>
public sealed class UsageReportingService : BackgroundService
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    private readonly IUsageConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _log;
    private bool _registered;

    public UsageReportingService(IUsageConfiguration config, IHttpClientFactory httpClientFactory, ILogger log)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _log = log.ForContext(LogArea.Name, LogArea.Http);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup is not the moment to make a network call. Nothing here is urgent, and a Hub that
        // is slow must not be part of how long Modbot takes to become useful.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = await TryReportAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(sent ? ReportInterval : RetryDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryReportAsync(CancellationToken ct)
    {
        // Checked every cycle rather than once, so turning analytics off in settings takes effect
        // without a restart.
        if (!_config.AnalyticsEnabled)
        {
            _registered = false;
            return true;
        }

        try
        {
            var snapshot = await _config.CaptureAsync(ct).ConfigureAwait(false);
            if (snapshot is null)
                return false;

            var http = _httpClientFactory.CreateClient(nameof(UsageReportingService));
            http.BaseAddress = new Uri(_config.HubUrl);
            http.Timeout = TimeSpan.FromSeconds(20);

            if (!_registered)
            {
                var registration = await http.PostAsJsonAsync("/api/instances/register", new
                {
                    instanceId = _config.InstanceId,
                    instanceUrl = _config.InstanceUrl,
                    version = snapshot.Version,
                }, ct).ConfigureAwait(false);

                if (!registration.IsSuccessStatusCode)
                {
                    _log.Debug("Hub registration returned {Status}; will retry", (int)registration.StatusCode);
                    return false;
                }

                _registered = true;
                _log.Debug("Registered with Modbot Hub");
            }

            var report = await http.PostAsJsonAsync(
                $"/api/instances/{Uri.EscapeDataString(_config.InstanceId)}/usage", snapshot, ct)
                .ConfigureAwait(false);

            if (report.IsSuccessStatusCode)
                return true;

            // A 404 means the Hub forgot us -- re-register on the next cycle rather than reporting
            // into a void forever.
            if (report.StatusCode == System.Net.HttpStatusCode.NotFound)
                _registered = false;

            _log.Debug("Hub usage report returned {Status}", (int)report.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Debug, deliberately. See the class remarks.
            _log.Debug(ex, "Usage reporting failed; Modbot is unaffected");
            return false;
        }
    }
}
