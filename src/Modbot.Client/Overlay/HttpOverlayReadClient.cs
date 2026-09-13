using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Modbot.Client.Ingest;
using Modbot.Core.Time;

namespace Modbot.Client.Overlay;

/// <summary>How a read or a long poll ended.</summary>
public enum ReadOutcome
{
    /// <summary>Fresh data came back.</summary>
    Fetched,

    /// <summary>
    /// The long poll ended with nothing to report. Normal, and by far the commonest answer.
    /// </summary>
    NothingWaiting,

    /// <summary>
    /// The token was rejected. Terminal for this pairing — surfaced, never retried, because a
    /// revoked moderator's client must stop and be seen to stop.
    /// </summary>
    Unauthorised,

    /// <summary>
    /// The server is unreachable, slow, or mid-deploy. The overlay keeps rendering the cache and
    /// says how old it is; it does not go blank, because a blank overlay helps nobody.
    /// </summary>
    Unreachable,
}

/// <param name="Elapsed">
/// How long the request actually took. Load-bearing for the long poll: a failure that arrives
/// after nearly the whole requested wait is an idle-connection timeout somewhere in the middle,
/// not a broken server.
/// </param>
public sealed record ReadResult<T>(ReadOutcome Outcome, T? Value = default, TimeSpan Elapsed = default);

/// <summary>The overlay's three read endpoints. Read-only, small, and never a write.</summary>
public interface IOverlayReadClient
{
    Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, CancellationToken cancellationToken);

    Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken);

    Task<ReadResult<FlaggedJoinAlert>> WaitForAlertAsync(ServerPairing pairing, int waitSeconds, CancellationToken cancellationToken);
}

/// <summary>
/// The overlay's reads: roster context, one profile summary, and the flagged-join long poll.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> Three kinds of GET, each to one paired server, each
/// carrying a bearer token and no body. The context read names the instance the moderator is
/// standing in — which that server already knows about, because it is that group's own instance
/// and the client has been reporting presence for it. The profile read names one VRChat user the
/// moderator chose to look up. The alert poll sends nothing but the token.</para>
/// <para><strong>What it does not send.</strong> Nothing about the moderator's machine, nothing
/// from the log, and nothing about instances belonging to any other group. A pairing sees exactly
/// one group's context.</para>
/// <para><strong>Nothing that comes back is a command.</strong> These are reads. The server has no
/// way to tell this client to do anything, by design, which is what keeps the client's behaviour
/// fully described by its own source.</para>
/// <para><strong>The overlay never calls this while drawing.</strong> It renders from the local
/// cache these responses fill, so a server that is slow or down produces stale data with its age
/// shown, never a spinner.</para>
/// </remarks>
public sealed class HttpOverlayReadClient : IOverlayReadClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IModbotClock _clock;

    public HttpOverlayReadClient(HttpClient http, IModbotClock clock)
    {
        _http = http;
        _clock = clock;
    }

    public Task<ReadResult<InstanceContext>> GetContextAsync(
        ServerPairing pairing,
        string instanceId,
        CancellationToken cancellationToken)
        => GetAsync<InstanceContext>(pairing, pairing.ContextEndpoint(instanceId), cancellationToken);

    public Task<ReadResult<UserSummary>> GetUserAsync(
        ServerPairing pairing,
        string subjectId,
        CancellationToken cancellationToken)
        => GetAsync<UserSummary>(pairing, pairing.UserEndpoint(subjectId), cancellationToken);

    public Task<ReadResult<FlaggedJoinAlert>> WaitForAlertAsync(
        ServerPairing pairing,
        int waitSeconds,
        CancellationToken cancellationToken)
        => GetAsync<FlaggedJoinAlert>(pairing, pairing.AlertsEndpoint(waitSeconds), cancellationToken);

    private async Task<ReadResult<T>> GetAsync<T>(ServerPairing pairing, Uri endpoint, CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var elapsed = _clock.UtcNow - startedAt;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new ReadResult<T>(ReadOutcome.Unauthorised, Elapsed: elapsed);

            // The long poll's ordinary ending: the wait expired with nothing to say.
            if (response.StatusCode is HttpStatusCode.NoContent)
                return new ReadResult<T>(ReadOutcome.NothingWaiting, Elapsed: elapsed);

            if (!response.IsSuccessStatusCode)
                return new ReadResult<T>(ReadOutcome.Unreachable, Elapsed: elapsed);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return new ReadResult<T>(ReadOutcome.NothingWaiting, Elapsed: elapsed);

            var value = JsonSerializer.Deserialize<T>(body, Json);
            return value is null
                ? new ReadResult<T>(ReadOutcome.NothingWaiting, Elapsed: elapsed)
                : new ReadResult<T>(ReadOutcome.Fetched, value, elapsed);
        }
        catch (HttpRequestException)
        {
            return new ReadResult<T>(ReadOutcome.Unreachable, Elapsed: _clock.UtcNow - startedAt);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReadResult<T>(ReadOutcome.Unreachable, Elapsed: _clock.UtcNow - startedAt);
        }
        catch (JsonException)
        {
            return new ReadResult<T>(ReadOutcome.Unreachable, Elapsed: _clock.UtcNow - startedAt);
        }
    }
}
