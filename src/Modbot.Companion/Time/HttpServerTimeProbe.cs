using System.Net.Http.Headers;
using System.Text.Json;
using Modbot.Companion.Ingest;
using Modbot.Core.Time;

namespace Modbot.Companion.Time;

/// <summary>Asks one server what time it thinks it is.</summary>
public interface IServerTimeProbe
{
    Task<ClockSample?> MeasureAsync(ServerPairing pairing, CancellationToken cancellationToken);
}

/// <summary>
/// <c>GET /api/v{n}/client/time</c>, timed at both ends.
/// </summary>
/// <remarks>
/// <para><strong>What this sends:</strong> a GET with a bearer token and no body. It reports
/// nothing about the machine, the user, or VRChat — it exists only so timestamps from several
/// moderators' PCs can be compared with each other.</para>
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

    public async Task<ClockSample?> MeasureAsync(ServerPairing pairing, CancellationToken cancellationToken)
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

            return new ClockSample(sentAt, reported, receivedAt);
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
}
