using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Core.Cloud;

/// <summary>What one attempt to send the public instances report did.</summary>
public enum PublicInstancesOutcome
{
    /// <summary>Nothing was sent, and nothing should have been.</summary>
    Off,

    /// <summary>The report reached Cloud.</summary>
    Sent,

    /// <summary>Cloud could not be reached, or refused. It will be tried again.</summary>
    Failed,
}

/// <summary>
/// Sends the public instances report to Modbot Cloud, and remembers this server's place there.
/// </summary>
/// <remarks>
/// <para>
/// One request replaces the whole list, so an instance that closed disappears from modbot.co on the
/// next send whether or not this server noticed it close. The report is sent again on a schedule
/// for the same reason: Cloud drops a server's instances when the reports stop, so a Modbot that is
/// switched off takes its instances off the page by itself.
/// </para>
/// <para>
/// The first send makes up an id and a secret and keeps them. Cloud takes the first report under
/// an id as the one that claims it, so from then on only this server can change its own instances.
/// The secret is encrypted in the settings row like every other secret (spec 8.3).
/// </para>
/// </remarks>
public sealed class PublicInstancesSender
{
    /// <summary>Where the report goes, under the Cloud address.</summary>
    public const string Path = "/api/v1/public-instances";

    private readonly ModbotContext _db;
    private readonly PublicInstancesReportBuilder _builder;
    private readonly ModbotCloudAddress _cloud;
    private readonly ISecretProtector _protector;
    private readonly IModbotClock _clock;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public PublicInstancesSender(
        ModbotContext db,
        PublicInstancesReportBuilder builder,
        ModbotCloudAddress cloud,
        ISecretProtector protector,
        IModbotClock clock,
        HttpClient http,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(http);

        _db = db;
        _builder = builder;
        _cloud = cloud;
        _protector = protector;
        _clock = clock;
        _http = http;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<PublicInstancesOutcome> SendAsync(CancellationToken ct = default)
    {
        var report = await _builder.BuildAsync(ct).ConfigureAwait(false);

        if (report is null)
            return PublicInstancesOutcome.Off;

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        var serverId = settings.PublicInstancesServerId;
        var secret = _protector.Unprotect(settings.PublicInstancesSecretEncrypted);

        if (serverId is null || string.IsNullOrEmpty(secret))
        {
            serverId = Guid.NewGuid();
            secret = NewSecret();

            settings.PublicInstancesServerId = serverId;
            settings.PublicInstancesSecretEncrypted = _protector.Protect(secret);

            // Saved before the request, not after: an id that reached Cloud and was then forgotten
            // here would be claimed for good, and this server could never report again under it.
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_cloud.Endpoint, Path))
        {
            Content = JsonContent.Create(report),
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serverId:D}.{secret}");

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A refusal is logged once at Information, not as an error: modbot.co listing this
                // group is a nicety, and a Cloud that is down or has forgotten this server must not
                // fill an operator's log with red.
                _log.Information(
                    "Modbot Cloud did not take the public instances report: {Status}",
                    (int)response.StatusCode);

                // Cloud says it does not know this id. The row here is the only copy of the secret,
                // so nothing can be recovered — start again with a new id on the next send.
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    settings.PublicInstancesServerId = null;
                    settings.PublicInstancesSecretEncrypted = null;
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                return PublicInstancesOutcome.Failed;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.Information("Could not reach Modbot Cloud to report the group's public instances: {Reason}", ex.Message);
            return PublicInstancesOutcome.Failed;
        }

        settings.PublicInstancesReportedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return PublicInstancesOutcome.Sent;
    }

    /// <summary>
    /// Tells Cloud to drop this server's instances, when the setting has just been turned off.
    /// </summary>
    /// <remarks>
    /// Without this, turning the setting off would leave the instances on modbot.co until Cloud aged
    /// them out. The id is forgotten either way, so a failure here costs at most one aging-out.
    /// </remarks>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cloud.Disabled)
            return;

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.PublicInstancesServerId is not { } serverId)
            return;

        var secret = _protector.Unprotect(settings.PublicInstancesSecretEncrypted);

        settings.PublicInstancesServerId = null;
        settings.PublicInstancesSecretEncrypted = null;
        settings.PublicInstancesReportedAt = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(secret))
            return;

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_cloud.Endpoint, Path));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serverId:D}.{secret}");

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                _log.Information("Modbot Cloud did not drop this server's instances: {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.Information("Could not reach Modbot Cloud to drop this server's instances: {Reason}", ex.Message);
        }
    }

    /// <summary>32 random bytes as base64url, the same shape a companion's install secret has.</summary>
    private static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
