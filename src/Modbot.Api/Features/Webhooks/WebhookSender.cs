using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Modbot.Api.Features.Events;
using Modbot.Core;
using Modbot.Core.Time;
using Modbot.VRChat.Scheduling;

namespace Modbot.Api.Features.Webhooks;

public enum WebhookSendOutcome
{
    /// <summary>A 2xx. Move on.</summary>
    Delivered = 1,

    /// <summary>No answer, a 5xx, a 408 or a 429. Try this event again later.</summary>
    Retry = 2,

    /// <summary>Any other 4xx, or a redirect. Do not try this event again.</summary>
    Skip = 3,
}

/// <param name="StatusCode">Null when no answer came.</param>
/// <param name="RetryAfter">What the receiver's <c>Retry-After</c> asked for, on a 408 or 429.</param>
public sealed record WebhookSendResult(
    WebhookSendOutcome Outcome,
    int? StatusCode,
    TimeSpan Duration,
    string? Error,
    TimeSpan? RetryAfter);

/// <summary>
/// Sends one signed event to one address and says what came of it (API keys design §6.3–§6.6).
/// </summary>
/// <remarks>
/// <para>
/// Two clients, one guarded and one not, rather than one client and a per-request switch: a pooled
/// connection opened while private addresses were allowed must never be reused for a request made
/// after they were not.
/// </para>
/// <para>
/// Neither uses a proxy. The egress proxy exists for VRChat's WAF (foundation §2.3.1), and a
/// receiver is not VRChat. Redirects are not followed: a redirect is how a public address hands
/// the request to a private one.
/// </para>
/// </remarks>
public sealed class WebhookSender : IDisposable
{
    private readonly HttpMessageInvoker _guarded;
    private readonly HttpMessageInvoker _open;
    private readonly IModbotClock _clock;
    private readonly IMonotonicClock _elapsed;
    private readonly WebhookOptions _options;

    /// <param name="handler">For tests: one handler for both clients, in place of real connections.</param>
    public WebhookSender(IModbotClock clock, WebhookOptions options, IMonotonicClock? elapsed = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _clock = clock;
        _options = options;
        _elapsed = elapsed ?? new StopwatchMonotonicClock();

        if (handler is not null)
        {
            _guarded = new HttpMessageInvoker(handler, disposeHandler: false);
            _open = _guarded;
        }
        else
        {
            _guarded = new HttpMessageInvoker(Handler(guarded: true));
            _open = new HttpMessageInvoker(Handler(guarded: false));
        }
    }

    private static SocketsHttpHandler Handler(bool guarded)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        if (guarded)
            handler.ConnectCallback = WebhookAddressGuard.ConnectAsync;

        return handler;
    }

    public async Task<WebhookSendResult> SendAsync(
        Uri url, string secret, EventEnvelope envelope, bool allowPrivate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(envelope);

        var started = _elapsed.Elapsed;
        TimeSpan Took() => _elapsed.Elapsed - started;

        // Refused before any connection: a name that is itself private, or an address outside the
        // rules the settings allow. The connect-time check covers what a name resolves to.
        if (!allowPrivate && (url.Scheme != Uri.UriSchemeHttps || WebhookAddressGuard.IsBlockedHost(url.Host)))
        {
            return new WebhookSendResult(
                WebhookSendOutcome.Retry, null, TimeSpan.Zero,
                url.Scheme != Uri.UriSchemeHttps ? "Only https addresses are allowed." : "That address is private.",
                null);
        }

        var body = EventEnvelopes.Serialize(envelope);
        var timestamp = _clock.UtcNow.ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("User-Agent", $"Modbot-Webhooks/{ModbotVersion.Release}");
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventIdHeader, envelope.Id);
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventTypeHeader, envelope.Type);
        request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, WebhookSignature.Sign(secret, timestamp, body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var response = await (allowPrivate ? _open : _guarded).SendAsync(request, timeout.Token);
            var code = (int)response.StatusCode;

            if (code is >= 200 and < 300)
                return new WebhookSendResult(WebhookSendOutcome.Delivered, code, Took(), null, null);

            if (code is 408 or 429)
                return new WebhookSendResult(WebhookSendOutcome.Retry, code, Took(), $"Answered {code}.", RetryAfter(response));

            if (code >= 500)
                return new WebhookSendResult(WebhookSendOutcome.Retry, code, Took(), $"Answered {code}.", null);

            if (code is >= 300 and < 400)
                return new WebhookSendResult(WebhookSendOutcome.Skip, code, Took(), $"Answered {code}. Redirects are not followed.", null);

            return new WebhookSendResult(WebhookSendOutcome.Skip, code, Took(), $"Answered {code}.", null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookSendResult(
                WebhookSendOutcome.Retry, null, Took(), $"No answer in {(int)_options.Timeout.TotalSeconds} seconds.", null);
        }
        catch (HttpRequestException e)
        {
            var blocked = FindBlocked(e);
            return new WebhookSendResult(
                WebhookSendOutcome.Retry, null, Took(),
                blocked is not null ? blocked.Message : Shorten(e.Message),
                null);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
            return delta;

        if (header?.Date is { } date)
            return date - _clock.UtcNow;

        return null;
    }

    private static WebhookAddressBlockedException? FindBlocked(Exception e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is WebhookAddressBlockedException blocked)
                return blocked;
        }

        return null;
    }

    private static string Shorten(string message) => message.Length <= 480 ? message : message[..480];

    public void Dispose()
    {
        _guarded.Dispose();
        if (!ReferenceEquals(_open, _guarded))
            _open.Dispose();
    }
}
