using System.Net.Http.Headers;
using System.Text.Json;
using Modbot.Client.Ingest;
using Modbot.Core.Time;

namespace Modbot.Client.Time;

/// <summary>Asks one server what time it thinks it is, and where the event backup goes.</summary>
public interface IServerTimeProbe
{
    Task<ServerTimeAnswer?> MeasureAsync(ServerPairing pairing, CancellationToken cancellationToken);
}

/// <summary>
/// <c>GET /api/v{n}/client/time</c>, timed at both ends.
/// </summary>
/// <remarks>
/// <para><strong>What this sends:</strong> a GET with a bearer token and no body. It reports
/// nothing about the machine, the user, or VRChat — it exists only so timestamps from several
/// moderators' PCs can be compared with each other.</para>
/// <para><strong>What it reads back</strong>, beside the time: which Modbot Cloud this server's
/// clients back their logs up to, or that its operator turned that off, and the server's own id.
/// The client obeys the first and passes the second on to Cloud; nothing else in the answer is
/// acted on.</para>
/// <para>How often: at pairing, on reconnect, and every few hours. Not per batch.</para>
/// </remarks>
public sealed class HttpServerTimeProbe : IServerTimeProbe
{
    private readonly HttpClient _http;
    private readonly IModbotClock _clock;

    public HttpServerTimeProbe(HttpClient http, IModbotClock clock)
    {
        _http = http;
        _clock = clock;
    }

    public async Task<ServerTimeAnswer?> MeasureAsync(ServerPairing pairing, CancellationToken cancellationToken)
    {
        // t0 and t3 are read either side of the request, so the round trip is measured rather than
        // assumed. A failed probe returns null: the previous estimate keeps standing and ages,
        // which the confidence value already accounts for.
        var sentAt = _clock.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.TimeEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var receivedAt = _clock.UtcNow;

            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("serverTime", out var serverTime)
                || !serverTime.TryGetDateTimeOffset(out var reported))
            {
                return null;
            }

            return new ServerTimeAnswer(new ClockSample(sentAt, reported, receivedAt), ReadCloud(document.RootElement));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>cloud</c> object and <c>instanceId</c>. A server too old to send them has no
    /// preference; an endpoint that is not an absolute address is kept as given-but-unusable by
    /// marking the answer disabled, so a typo never sends anywhere unintended.
    /// </summary>
    public static ServerCloudAnswer ReadCloud(JsonElement root)
    {
        var instanceId = root.TryGetProperty("instanceId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

        if (!root.TryGetProperty("cloud", out var cloud) || cloud.ValueKind != JsonValueKind.Object)
            return ServerCloudAnswer.NoPreference with { InstanceId = instanceId };

        var disabled = cloud.TryGetProperty("disabled", out var flag) && flag.ValueKind == JsonValueKind.True;

        if (!cloud.TryGetProperty("endpoint", out var endpoint) || endpoint.ValueKind != JsonValueKind.String)
            return new ServerCloudAnswer(null, disabled, instanceId);

        return Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var address)
            ? new ServerCloudAnswer(address, disabled, instanceId)
            : new ServerCloudAnswer(null, true, instanceId);
    }
}
