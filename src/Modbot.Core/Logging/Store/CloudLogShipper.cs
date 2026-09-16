using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Microsoft.Extensions.Http;
using Modbot.Core.Time;

namespace Modbot.Core.Logging.Store;

/// <summary>How one pass of the shipper ended.</summary>
public enum ShipOutcome
{
    /// <summary>Sending is off, here or by <c>MODBOT_CLOUD_DISABLED</c>.</summary>
    Off,

    /// <summary>Nothing new to send.</summary>
    Nothing,

    /// <summary>A batch reached Cloud.</summary>
    Sent,

    /// <summary>Cloud refused or could not be reached. Try again later.</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Lines">Lines sent, when a batch went.</param>
/// <param name="Dropped">Lines skipped this pass, counted rather than silently forgotten.</param>
/// <param name="RetryAfter">What Cloud asked for, when it asked.</param>
/// <param name="Error">What went wrong, in Cloud's words where it gave any.</param>
public sealed record ShipResult(
    ShipOutcome Outcome,
    int Lines = 0,
    long Dropped = 0,
    TimeSpan? RetryAfter = null,
    string? Error = null);

/// <summary>
/// Sends Modbot's own log lines to Modbot Cloud.
/// </summary>
/// <remarks>
/// <para>
/// On by default and turned off in <strong>Settings</strong>, <strong>Host &amp; Database</strong>,
/// <strong>Logs</strong>, and off entirely when <c>MODBOT_CLOUD_DISABLED</c> is set — that variable
/// wins over the switch, always (central services spec 1.1).
/// </para>
/// <para>
/// Why it exists: a deployment the project cannot reach is a deployment nobody can help with. The
/// operator writes "it stopped syncing yesterday" and the one thing that would answer it is in a
/// container that has since been redeployed. Cloud holds a copy so the answer survives, and so the
/// operator can read it from somewhere their own Modbot is not.
/// </para>
///
/// <para><strong>What it sends.</strong></para>
/// <para>
/// The same rows the deployment keeps itself, unchanged: Information and above, outbound API traffic
/// left out, secret-looking properties already replaced by <see cref="LogSecrets"/> before they were
/// ever stored. The maintainer chose unchanged over warnings-only and over anonymised, and the
/// privacy policy says so plainly.
/// </para>
///
/// <para><strong>The place-marker, and what is dropped.</strong></para>
/// <para>
/// There is no second queue. The shipper reads <c>modbot_log</c> from the row id it last sent,
/// which survives restarts for free and cannot lose a line the store kept. If Cloud is unreachable
/// long enough for the marker to fall <see cref="MostRowsBehind"/> rows behind, the marker jumps
/// forward and the skipped lines are counted into <c>Settings.CloudLogDropped</c> — the alternative
/// is a deployment that comes back after a fortnight and spends hours sending a fortnight of log
/// lines nobody will read. What is dropped is the <em>oldest</em>, because the newest are the ones
/// somebody is asking about.
/// </para>
/// <para>
/// A batch Cloud refuses outright (<c>400</c> or <c>413</c>) is skipped rather than retried forever:
/// re-sending it would never work and would block every line behind it.
/// </para>
///
/// <para><strong>Identity.</strong></para>
/// <para>
/// <strong>The credential this server already has:</strong> the id and secret it registered with in
/// Cloud's server registry, which <c>ServerReporter</c> establishes and keeps in the same settings
/// row. Not a second registration of its own — Cloud would then hold two ids for one deployment with
/// no way to join them, and that join is what lets the account which claimed the server read its own
/// logs in Cloud.
/// </para>
/// <para>
/// So a server that has not registered yet sends nothing and says so; registering is
/// <c>ServerReporter</c>'s job and happens within a few minutes of starting. A <c>401</c> is left
/// alone for the same reason: <c>ServerReporter</c> owns that credential and re-registers when Cloud
/// forgets it, and two things clearing the same secret would fight.
/// </para>
/// </remarks>
public sealed class CloudLogShipper
{
    public const string HttpClientName = "modbot-cloud-logs";

    /// <summary>Lines in one batch. Well under Cloud's ceiling of a thousand.</summary>
    public const int BatchSize = 500;

    /// <summary>
    /// How far behind the place-marker may fall before the oldest unsent lines are given up on.
    /// About a fortnight of a quiet deployment, or a day of a very noisy one.
    /// </summary>
    public const int MostRowsBehind = 50_000;

    /// <summary>
    /// How many lines a deployment sending for the first time starts from. Turning this on should
    /// show Cloud the recent past, not six months of it.
    /// </summary>
    public const int FirstRunLines = 1_000;

    /// <summary>What the Health page says while this server has not registered with Cloud yet.</summary>
    public const string NotRegisteredYet = "This server has not registered with Modbot Cloud yet.";

    private readonly ModbotContext _db;
    private readonly IHttpClientFactory _http;
    private readonly ISecretProtector _protector;
    private readonly IModbotClock _clock;
    private readonly ModbotCloudAddress _cloud;

    public CloudLogShipper(
        ModbotContext db,
        IHttpClientFactory http,
        ISecretProtector protector,
        IModbotClock clock,
        ModbotCloudAddress cloud)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cloud);

        _db = db;
        _http = http;
        _protector = protector;
        _clock = clock;
        _cloud = cloud;
    }

    public async Task<ShipResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (_cloud.Disabled)
            return new ShipResult(ShipOutcome.Off);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!settings.ShipLogsToCloud)
            return new ShipResult(ShipOutcome.Off);

        // The registry's credential, made by ServerReporter. Nothing to do until it exists.
        if (Bearer(settings) is not { } bearer)
        {
            await NoteFailureAsync(settings, NotRegisteredYet, ct).ConfigureAwait(false);
            return new ShipResult(ShipOutcome.Failed, Error: NotRegisteredYet);
        }

        var client = _http.CreateClient(HttpClientName);

        var newest = await _db.Logs.AsNoTracking().MaxAsync(l => (long?)l.Id, ct).ConfigureAwait(false) ?? 0;

        if (newest == 0)
            return new ShipResult(ShipOutcome.Nothing);

        var (cursor, skipped) = StartFrom(settings, newest);

        var lines = await _db.Logs.AsNoTracking()
            .Where(l => l.Id > cursor)
            .OrderBy(l => l.Id)
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (lines.Count == 0)
        {
            if (skipped > 0)
                await AdvanceAsync(settings, cursor, skipped, sent: false, ct).ConfigureAwait(false);

            return new ShipResult(ShipOutcome.Nothing, Dropped: skipped);
        }

        var body = Batch(lines, _clock.UtcNow);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_cloud.Endpoint, "/api/v1/logs"))
        {
            Content = body,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await NoteFailureAsync(settings, Short(e.Message), ct).ConfigureAwait(false);
            return new ShipResult(ShipOutcome.Failed, Dropped: skipped, Error: Short(e.Message));
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                await AdvanceAsync(settings, lines[^1].Id, skipped, sent: true, ct).ConfigureAwait(false);
                return new ShipResult(ShipOutcome.Sent, lines.Count, skipped);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Cloud does not know this server any more. ServerReporter owns that credential and
                // registers again on its own next pass; clearing it here as well would mean two
                // things racing to replace one secret.
                await NoteFailureAsync(settings, "Modbot Cloud did not recognise this server.", ct)
                    .ConfigureAwait(false);

                return new ShipResult(ShipOutcome.Failed, Dropped: skipped, Error: "Not recognised.");
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
            {
                // Permanent for this batch. Re-sending it would never work and would block every
                // line behind it, so it is given up on and counted.
                await AdvanceAsync(settings, lines[^1].Id, skipped + lines.Count, sent: false, ct)
                    .ConfigureAwait(false);

                await NoteFailureAsync(settings, $"Modbot Cloud refused a batch ({(int)response.StatusCode}).", ct)
                    .ConfigureAwait(false);

                return new ShipResult(
                    ShipOutcome.Failed, Dropped: skipped + lines.Count, Error: "A batch was refused.");
            }

            var retryAfter = response.Headers.RetryAfter?.Delta
                             ?? (response.Headers.RetryAfter?.Date is { } date ? date - _clock.UtcNow : null);

            var problem = $"Modbot Cloud answered {(int)response.StatusCode}.";
            await NoteFailureAsync(settings, problem, ct).ConfigureAwait(false);

            return new ShipResult(ShipOutcome.Failed, Dropped: skipped, RetryAfter: retryAfter, Error: problem);
        }
    }

    /// <summary>
    /// Where this pass starts reading, and how many lines it gave up on to get there.
    /// </summary>
    private static (long Cursor, long Skipped) StartFrom(Settings settings, long newest)
    {
        var cursor = settings.CloudLogSentThroughId;

        // Never sent anything: start near the top rather than at the beginning of the table, so
        // turning this on does not send six months of log lines nobody asked for.
        if (cursor == 0 && settings.CloudLogSentAt is null)
            return (Math.Max(0, newest - FirstRunLines), 0);

        var behind = newest - cursor;

        if (behind <= MostRowsBehind)
            return (cursor, 0);

        var jumped = newest - MostRowsBehind;
        return (jumped, jumped - cursor);
    }

    /// <summary><c>serverId.secret</c>, or null while this server has not registered.</summary>
    private string? Bearer(Settings settings)
    {
        if (settings.CloudServerId is not { Length: > 0 } id || settings.CloudServerSecretEncrypted is not { } stored)
            return null;

        var secret = _protector.Unprotect(stored);
        return string.IsNullOrEmpty(secret) ? null : $"{id}.{secret}";
    }

    private async Task AdvanceAsync(Settings settings, long cursor, long dropped, bool sent, CancellationToken ct)
    {
        settings.CloudLogSentThroughId = cursor;
        settings.CloudLogDropped += dropped;

        if (sent)
        {
            settings.CloudLogSentAt = _clock.UtcNow;
            settings.CloudLogError = null;
            settings.CloudLogErrorAt = null;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task NoteFailureAsync(Settings settings, string problem, CancellationToken ct)
    {
        settings.CloudLogError = Short(problem);
        settings.CloudLogErrorAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The batch, as gzipped JSON.</summary>
    private static HttpContent Batch(IReadOnlyList<LogEntry> lines, DateTimeOffset sentAt)
    {
        var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var json = new Utf8JsonWriter(gzip))
        {
            json.WriteStartObject();
            json.WriteString("sentAt", sentAt);
            json.WriteString("serverVersion", ModbotVersion.Release);
            json.WriteStartArray("lines");

            foreach (var line in lines)
            {
                json.WriteStartObject();
                json.WriteString("at", line.At);
                json.WriteString("level", line.Level);
                json.WriteString("message", line.Message);
                WriteOptional(json, "template", line.Template);
                WriteOptional(json, "source", line.Source);
                WriteOptional(json, "area", line.Area);
                WriteOptional(json, "exception", line.Exception);

                // Written raw, so the property document arrives as an object rather than as a
                // string holding one. It came out of a jsonb column, so it is valid JSON.
                json.WritePropertyName("properties");
                json.WriteRawValue(line.Properties, skipInputValidation: true);

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
        }

        buffer.Position = 0;

        var content = new StreamContent(buffer);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        return content;
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
            json.WriteString(name, value);
    }

    /// <summary>A problem short enough for a settings column and a card on the Health page.</summary>
    private static string Short(string message) =>
        message.Length <= 200 ? message : message[..200];

}

/// <summary>The value the Health page shows for log shipping.</summary>
/// <param name="On">Sending is on and allowed.</param>
/// <param name="Allowed">False when <c>MODBOT_CLOUD_DISABLED</c> is set.</param>
/// <param name="Registered">This server has registered with Cloud, so it has something to send as.</param>
/// <param name="LastSentAt">When the last batch reached Cloud.</param>
/// <param name="Waiting">Lines stored but not yet sent.</param>
/// <param name="Dropped">Lines given up on because Cloud was unreachable for long, or refused them.</param>
/// <param name="LastError">Why the last attempt failed. Null once one succeeds.</param>
public sealed record CloudLogStatus(
    bool On,
    bool Allowed,
    bool Registered,
    DateTimeOffset? LastSentAt,
    long Waiting,
    long Dropped,
    string? LastError,
    DateTimeOffset? LastErrorAt)
{
    public static async Task<CloudLogStatus> ReadAsync(
        ModbotContext db, ModbotCloudAddress cloud, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(cloud);

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new
            {
                s.ShipLogsToCloud,
                s.CloudServerId,
                s.CloudLogSentAt,
                s.CloudLogSentThroughId,
                s.CloudLogDropped,
                s.CloudLogError,
                s.CloudLogErrorAt,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var newest = await db.Logs.AsNoTracking().MaxAsync(l => (long?)l.Id, ct).ConfigureAwait(false) ?? 0;
        var cursor = settings?.CloudLogSentThroughId ?? 0;

        return new CloudLogStatus(
            (settings?.ShipLogsToCloud ?? true) && !cloud.Disabled,
            !cloud.Disabled,
            settings?.CloudServerId is not null,
            settings?.CloudLogSentAt,
            Math.Max(0, newest - cursor),
            settings?.CloudLogDropped ?? 0,
            settings?.CloudLogError,
            settings?.CloudLogErrorAt);
    }
}
