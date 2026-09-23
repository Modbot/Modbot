using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Files;
using Modbot.VRChat.Proxy;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Session;
using OtpNet;
using Serilog;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat;

/// <summary>
/// The one authenticated VRChat session, behind the rate limiter.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.1 and 4.1.1. The gate drives authentication itself rather than through the SDK's
/// <c>LoginAsync</c> / <c>TryLoginAsync</c> helpers, and that is a decision, not a workaround.
/// Those helpers collapse a 401, a 403, a 429 and a Cloudflare block into one bare <c>null</c>,
/// and Modbot has to respond differently to every one of them: re-authenticate, tell the
/// operator, cold stop, offer a proxy. <c>TryLoginAsync</c> additionally inverts its own result,
/// reporting a successful login as a failure with no exception.
/// </para>
/// <para>
/// Spec 4.1.2 is the other half. VRChat allows about four or five sign-ins an hour and answers the
/// next with an hour-long block, so signing in is treated as the scarcest thing Modbot does:
/// </para>
/// <list type="bullet">
/// <item>A stored session is always tried first, and it is checked with Get Group and Get User --
/// never with <c>/auth/user</c>, which VRChat counts as signing in again.</item>
/// <item>Every request that could count as a sign-in is recorded in the database before it is sent,
/// and no more than <see cref="RateLimitOptions.SignInsPerHour"/> go out in any rolling hour.</item>
/// <item>A rate limit on a sign-in stops every sign-in, and everything that needs a session, for an
/// hour from that moment -- also recorded, so a restart does not cut it short.</item>
/// <item>One check or sign-in at a time. Calls that fail together share the one that follows.</item>
/// </list>
/// </remarks>
public sealed class VRChatGate : IVRChatGate, IDisposable
{
    private const string ServiceName = "VRChat";

    /// <summary>
    /// How long a finding that the account has lost the group stands before a 401 checks again.
    /// Without it, every producer's refused poll would spend two more requests learning the same
    /// thing.
    /// </summary>
    private static readonly TimeSpan LostGroupStandsFor = TimeSpan.FromMinutes(15);

    /// <summary>Where VRChat's API lives, for a forwarded request that has no session client to ask.</summary>
    public static readonly Uri DefaultApiHost = new("https://api.vrchat.cloud");

    /// <summary>How long a forwarded request on a caller's own cookie may take. The SDK's own default.</summary>
    private static readonly TimeSpan PassthroughTimeout = TimeSpan.FromSeconds(30);

    private readonly IVRChatClientFactory _clients;
    private readonly IVRChatConnectionStore _connections;
    private readonly IRateLimiter _limiter;
    private readonly IModbotClock _clock;
    private readonly IMonotonicClock _elapsed;
    private readonly ILogger _logger;
    private readonly IVRChatSignInStore _signIns;
    private readonly int _signInLimit;
    private readonly Uri _apiHost;
    private readonly string _passthroughUserAgent;

    /// <summary>One check or sign-in at a time.</summary>
    private readonly SemaphoreSlim _session = new(1, 1);

    /// <summary>
    /// The client for everything that must not carry the service account's cookies: a request
    /// forwarded on a caller's own cookie, and a file fetch. No jar, no credentials, no redirect
    /// followed for it, and the same egress proxy as the session client. Rebuilt when the proxy
    /// changes.
    /// </summary>
    private readonly Lock _withoutCookiesLock = new();
    private (string ProxyKey, HttpClient Client)? _withoutCookies;

    /// <summary>
    /// Clients replaced because the proxy settings changed, kept until this gate is disposed.
    /// </summary>
    /// <remarks>
    /// <strong>A client is never disposed while a request may still be on it.</strong> These are
    /// built with <c>disposeHandler: true</c>, so disposing one tears down its connection pool,
    /// and tearing down a connection pool under a request that is still reading is how a socket
    /// gets used after it has been freed -- a native fault, not an exception anything can catch.
    /// Retiring instead costs one idle handler per change of the operator's proxy settings, which
    /// happens approximately never; the connections inside it close on their own idle timeout.
    /// </remarks>
    private readonly List<HttpClient> _retired = [];

    /// <summary>For tests: what to send over instead of a real connection. Never set in production.</summary>
    private readonly Func<HttpMessageHandler>? _handlerWithoutCookies;

    /// <summary>Separate from the session lock, so a health read never waits behind a sign-in.</summary>
    private readonly SemaphoreSlim _loading = new(1, 1);

    private readonly Lock _attemptsLock = new();

    private IVRChat? _client;
    private VRChatSignedInAccount? _account;

    /// <summary>Goes up whenever <see cref="_client"/> is replaced.</summary>
    private int _sessionNumber;

    /// <summary>Goes up whenever a 401 has been answered, successfully or not.</summary>
    private int _renewals;

    private SessionResult _lastRenewal;
    private (int Session, DateTimeOffset At)? _lostGroup;

    /// <summary>The credentials VRChat last refused, hashed. Not tried again until they change.</summary>
    private string? _refusedCredentials;

    private string? _savedAuth;
    private string? _savedTwoFactor;

    private volatile bool _loaded;
    private List<DateTimeOffset> _attempts = [];
    private SignInWait? _wait;
    private DateTimeOffset? _lastSignedInAt;

    private VRChatSessionState _state = VRChatSessionState.Unconfigured;

    public VRChatGate(
        IVRChatClientFactory clients,
        IVRChatConnectionStore connections,
        IRateLimiter limiter,
        IModbotClock clock,
        IMonotonicClock? elapsed = null,
        ILogger? logger = null,
        IVRChatSignInStore? signIns = null,
        RateLimitOptions? rateLimits = null,
        Uri? apiHost = null,
        VRChatClientOptions? clientOptions = null,
        Func<HttpMessageHandler>? handlerWithoutCookies = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(clock);

        _clients = clients;
        _connections = connections;
        _limiter = limiter;
        _clock = clock;
        _elapsed = elapsed ?? new StopwatchMonotonicClock();
        _logger = logger ?? Log.Logger;
        _signIns = signIns ?? new MemorySignInStore();
        _signInLimit = (rateLimits ?? new RateLimitOptions()).SignInsPerHour;
        _apiHost = apiHost ?? DefaultApiHost;
        _handlerWithoutCookies = handlerWithoutCookies;

        var options = clientOptions ?? new VRChatClientOptions();
        _passthroughUserAgent = $"{options.ApplicationName}/{options.ApplicationVersion} {options.DeveloperContactEmail}";
    }

    public VRChatSessionState State => _wait is not null ? VRChatSessionState.SignInWaiting : _state;

    public Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default) =>
        _limiter.DescribeAsync(ct);

    public async Task<SignInStatus> DescribeSignInAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);

        var now = _clock.UtcNow;

        int counted;
        lock (_attemptsLock)
            counted = SignInBudget.InWindow(_attempts, now);

        return new SignInStatus(State, _wait, _lastSignedInAt, counted, _signInLimit, now);
    }

    public async Task ResumeAfterWaitAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);

        // Cheap enough to ask every few seconds: nothing is read or sent unless a wait has ended.
        if (_wait is not { } wait || _clock.UtcNow < wait.RetryAt)
            return;

        await EnsureSessionAsync(Need.Use, 0, 0, VRChatCallPriority.Background, ct).ConfigureAwait(false);
    }

    public async Task<VRChatResult<T>> ExecuteAsync<T>(
        VRChatEndpoint endpoint,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var session = await EnsureSessionAsync(Need.Use, 0, 0, priority, ct).ConfigureAwait(false);
        if (!session.Success)
            return session.ToFailure<T>();

        // Read before the call, so that if it comes back 401 the gate can tell whether somebody
        // else's 401 has already been dealt with in the meantime.
        var renewalsBefore = Volatile.Read(ref _renewals);

        var result = await IssueAsync(session.Client!, endpoint, call, priority, ct).ConfigureAwait(false);

        if (result.Success)
            await KeepNewCookiesAsync(session.Client!, ct).ConfigureAwait(false);

        // Refused reading the group itself, on a session that is working. Not a reason to sign in:
        // the account has lost the group, and only giving it back fixes that.
        if (result.StatusCode == (int)HttpStatusCode.Forbidden
            && endpoint.Class == VRChatEndpointClass.GroupsRead
            && !result.IsWafBlocked)
        {
            MarkLostGroup(session.Number, endpoint);
            return result;
        }

        if (result.StatusCode != (int)HttpStatusCode.Unauthorized)
            return result;

        var groupCall = IsGroupCall(endpoint);

        // The session was checked a few minutes ago and works; a group read refused since then is
        // the same lost group, and checking again for every refused poll would learn nothing.
        if (groupCall && LostGroupStands(session.Number))
            return LostGroupFailure<T>(result);

        var renewed = await EnsureSessionAsync(Need.Renew, session.Number, renewalsBefore, priority, ct)
            .ConfigureAwait(false);

        if (!renewed.Success)
            return renewed.ToFailure<T>();

        // The same session came back: the check found it working, so this 401 was about the call,
        // not the session. Retrying it would only get the same answer.
        if (renewed.Number == session.Number)
        {
            if (groupCall)
            {
                MarkLostGroup(session.Number, endpoint);
                return LostGroupFailure<T>(result);
            }

            return VRChatResult<T>.Failure(
                result.StatusCode,
                result.ErrorMessage ?? $"VRChat returned 401 for {endpoint}.",
                rawResponse: result.RawResponse,
                kind: VRChatFailureKind.Other);
        }

        // A new session: one retry, and never a loop (spec 4.1).
        return await IssueAsync(renewed.Client!, endpoint, call, priority, ct).ConfigureAwait(false);
    }

    public async Task<VRChatResult<CurrentUserLoginResponse>> SignInAsync(CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(Need.Check, 0, 0, VRChatCallPriority.Interactive, ct)
            .ConfigureAwait(false);

        if (!session.Success)
            return session.ToFailure<CurrentUserLoginResponse>();

        var user = session.User ?? new CurrentUserLoginResponse
        {
            Id = _account?.UserId!,
            DisplayName = _account?.DisplayName!,
        };

        return VRChatResult<CurrentUserLoginResponse>.Ok(user, 200);
    }

    public Task<VRChatResult<VRChatProxyResponse>> ForwardAsync(
        VRChatEndpoint endpoint,
        VRChatProxyRequest request,
        VRChatProxyAccount account,
        VRChatCallPriority priority = VRChatCallPriority.Interactive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return account == VRChatProxyAccount.Caller
            ? ForwardAsCallerAsync(endpoint, request, priority, ct)
            : ForwardAsServiceAccountAsync(endpoint, request, priority, ct);
    }

    /// <summary>
    /// A forwarded request on the service account is an ordinary gated call whose "SDK call" is
    /// the session client's own <c>HttpClient</c>: same session, same jar, same limiter, same
    /// 401 renewal, same cold stop. VRChat's answer is kept aside on the way through, because
    /// <see cref="Interpret{T}"/> keeps only a 2xx's body and the proxy returns every status as
    /// it came.
    /// </summary>
    private async Task<VRChatResult<VRChatProxyResponse>> ForwardAsServiceAccountAsync(
        VRChatEndpoint endpoint, VRChatProxyRequest request, VRChatCallPriority priority, CancellationToken ct)
    {
        VRChatProxyResponse? answered = null;

        var result = await ExecuteAsync<VRChatProxyResponse>(
                endpoint,
                async (vrchat, token) =>
                {
                    var host = new Uri(new Uri(vrchat.Configuration.BasePath).GetLeftPart(UriPartial.Authority));

                    using var message = VRChatProxyCall.Build(
                        host, request, vrchat.Configuration.UserAgent, vrchat.Configuration.DefaultHeaders,
                        VRChatProxyAccount.Service);

                    using var response = await vrchat.HttpClient
                        .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);

                    answered = await VRChatProxyCall.ReadAsync(response, VRChatProxyAccount.Service, token)
                        .ConfigureAwait(false);

                    // The body as text, so a Cloudflare block on a forwarded request is
                    // classified and shown the way one on any other call is.
                    return new ApiResponse<VRChatProxyResponse>(
                        response.StatusCode, answered, VRChatProxyCall.TextOf(answered) ?? string.Empty);
                },
                priority,
                ct)
            .ConfigureAwait(false);

        // VRChat answered, whatever it said: that answer is the result. The gate's own verdict on
        // it -- a 401 it could not renew past, a rate limit it recorded -- has already been acted
        // on above, and the caller gets the status VRChat gave.
        if (answered is not null && result.StatusCode != 0)
            return VRChatResult<VRChatProxyResponse>.Ok(answered, answered.StatusCode);

        return VRChatResult<VRChatProxyResponse>.From(result);
    }

    /// <summary>
    /// A forwarded request on the caller's own cookie needs no session and must not have one:
    /// it is paced through the limiter on its own class and sent through the same egress proxy,
    /// and that is all the gate does for it. It never touches the gate's state, because a
    /// stranger's 429 says nothing about the service account.
    /// </summary>
    private async Task<VRChatResult<VRChatProxyResponse>> ForwardAsCallerAsync(
        VRChatEndpoint endpoint, VRChatProxyRequest request, VRChatCallPriority priority, CancellationToken ct)
    {
        var connection = await _connections.ReadAsync(ct).ConfigureAwait(false);
        var client = ClientWithoutCookies(connection);

        var lease = await _limiter.AcquireAsync(endpoint, priority, ct).ConfigureAwait(false);
        await using (lease)
        {
            if (!lease.IsAcquired)
            {
                var denial = lease.Denial!;
                return VRChatResult<VRChatProxyResponse>.Failure(
                    0,
                    $"{denial.Bucket} is rate limited ({denial.Reason}); " +
                    $"nothing will be sent on it for another {denial.RetryAfter:g}.",
                    kind: VRChatFailureKind.RateLimited);
            }

            var operation = endpoint.Operation ?? "forward";
            var started = _elapsed.Elapsed;

            try
            {
                using var message = VRChatProxyCall.Build(
                    _apiHost, request, _passthroughUserAgent, defaultHeaders: null, VRChatProxyAccount.Caller);

                using var response = await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                var answered = await VRChatProxyCall.ReadAsync(response, VRChatProxyAccount.Caller, ct)
                    .ConfigureAwait(false);

                HttpLog.Completed(
                    _logger, ServiceName, endpoint.Class, operation,
                    answered.StatusCode, _elapsed.Elapsed - started, lease.Tokens);

                await lease.ReportAsync(answered.StatusCode, ct).ConfigureAwait(false);

                return VRChatResult<VRChatProxyResponse>.Ok(answered, answered.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                HttpLog.Failed(_logger, ServiceName, endpoint.Class, operation, exception, _elapsed.Elapsed - started);
                await lease.ReportAsync(0, ct).ConfigureAwait(false);

                return VRChatResult<VRChatProxyResponse>.Failure(
                    0, exception.Message, kind: VRChatTransportFailure.Classify(exception));
            }
        }
    }

    /// <summary>
    /// A file fetch is a session call with no budget behind it: VRChat does not rate limit its
    /// file and image addresses, so there is no lease to take, nothing to report a status to,
    /// and no bucket a 429 could cold stop. Everything else about it is ordinary -- the session
    /// cookie, the User-Agent, the developer headers, the operator's egress proxy -- because all
    /// of those live on the session client and the session client is what sends it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The redirect is followed here rather than by the handler, because the handler would
    /// follow a <c>Location</c> anywhere and this must only ever reach VRChat. Each hop is
    /// checked before it is sent, and the address that finally answered is checked as well, so
    /// a handler that followed one on its own cannot get past it either -- and the client this
    /// uses is told to follow none, so none is sent unchecked in the first place.
    /// </para>
    /// <para>
    /// <strong>The session cookie goes to VRChat's API host and to nothing else.</strong> The
    /// stored address is on <c>api.vrchat.cloud</c> and that is the host that decides whether the
    /// file may be read; it then redirects to a delivery host, which serves the bytes to anybody
    /// holding the address it just issued and needs no cookie to do it. Sending it one anyway is
    /// handing the service account's session to a machine that never asked for it, on every face
    /// in a member list. So this does not use the session's shared client at all: that client
    /// carries the cookie jar, and a jar sends a <c>.vrchat.cloud</c> cookie to every host under
    /// that domain by itself. The cookie is put on the one request that needs it, by hand.
    /// </para>
    /// <para>
    /// Everything else about the fetch still comes from the session -- the User-Agent, the
    /// developer headers, the operator's egress proxy -- because those are about who Modbot is
    /// and how it reaches the internet, not about who it is signed in as.
    /// </para>
    /// </remarks>
    public async Task<VRChatFileResult> FetchFileAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!VRChatFiles.IsVRChatAddress(url))
        {
            return VRChatFileResult.Problems(
                VRChatFileOutcome.NotVRChatAddress, $"{url} is not an address VRChat serves files from.");
        }

        var session = await EnsureSessionAsync(Need.Use, 0, 0, VRChatCallPriority.Interactive, ct)
            .ConfigureAwait(false);

        if (!session.Success)
        {
            return VRChatFileResult.Problems(
                VRChatFileOutcome.NoSession, session.ErrorMessage ?? "There is no VRChat session to fetch that on.");
        }

        var configuration = session.Client!.Configuration;
        var (auth, _) = CookiesOf(session.Client!);

        var connection = await _connections.ReadAsync(ct).ConfigureAwait(false);
        var client = ClientWithoutCookies(connection);

        var started = _elapsed.Elapsed;
        var next = url;

        try
        {
            for (var hop = 0; hop <= VRChatFiles.MaxRedirects; hop++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, next);

                if (!string.IsNullOrWhiteSpace(configuration.UserAgent))
                    message.Headers.TryAddWithoutValidation("User-Agent", configuration.UserAgent);

                foreach (var header in configuration.DefaultHeaders)
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);

                // Only the API host, and only when there is a cookie to send. A delivery host is
                // handed nothing.
                if (auth is not null && IsApiHost(next))
                    message.Headers.TryAddWithoutValidation("Cookie", $"auth={auth}");

                using var response = await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                var status = (int)response.StatusCode;

                // Where the request actually ended up, which is not where it was sent if the
                // handler followed a redirect itself.
                var answered = response.RequestMessage?.RequestUri ?? next;
                if (!VRChatFiles.IsVRChatAddress(answered))
                {
                    return VRChatFileResult.Problems(
                        VRChatFileOutcome.NotVRChatAddress,
                        $"The file fetch ended at {answered.Host}, which is not one of VRChat's hosts.");
                }

                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    next = location.IsAbsoluteUri ? location : new Uri(answered, location);

                    if (!VRChatFiles.IsVRChatAddress(next))
                    {
                        return VRChatFileResult.Problems(
                            VRChatFileOutcome.NotVRChatAddress,
                            $"VRChat redirected the file fetch to {next.Host}, which is not one of its own hosts.");
                    }

                    continue;
                }

                HttpLog.Completed(
                    _logger, ServiceName, "files", "fetch", status, _elapsed.Elapsed - started);

                if (status == (int)HttpStatusCode.NotFound)
                    return VRChatFileResult.Problems(VRChatFileOutcome.NotFound, "VRChat has no such file.");

                if (status is < 200 or >= 300)
                {
                    return VRChatFileResult.Problems(
                        VRChatFileOutcome.Failed, $"VRChat answered {status} for that file.");
                }

                var contentType = response.Content.Headers.ContentType?.ToString();
                if (!VRChatFiles.IsShowable(contentType))
                {
                    return VRChatFileResult.Problems(
                        VRChatFileOutcome.NotShowable,
                        $"VRChat answered with {contentType ?? "no content type"}, which is not a picture or a video.");
                }

                var bytes = await ReadCappedAsync(response.Content, VRChatFiles.MaxBytes, ct).ConfigureAwait(false);
                if (bytes is null)
                {
                    return VRChatFileResult.Problems(
                        VRChatFileOutcome.TooBig, $"That file is larger than {VRChatFiles.MaxBytes} bytes.");
                }

                return VRChatFileResult.Ok(new VRChatFile(bytes, VRChatFiles.BareType(contentType!)));
            }

            return VRChatFileResult.Problems(
                VRChatFileOutcome.Failed,
                $"VRChat redirected that file more than {VRChatFiles.MaxRedirects} times.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HttpLog.Failed(_logger, ServiceName, "files", "fetch", exception, _elapsed.Elapsed - started);

            return VRChatFileResult.Problems(VRChatFileOutcome.Failed, exception.Message);
        }
    }

    /// <summary>
    /// Whether this is VRChat's API host -- the one that decides whether a file may be read, and
    /// so the only one a file fetch sends the session cookie to.
    /// </summary>
    private bool IsApiHost(Uri url) =>
        url.Host.Equals(_apiHost.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>The body, or null when it runs past <paramref name="maxBytes"/>.</summary>
    /// <remarks>
    /// Read rather than trusted: <c>Content-Length</c> is what the server claimed, and a body
    /// that keeps coming after it has to stop somewhere that is not memory.
    /// </remarks>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The client with no cookie jar, so nothing of the service account's can ride along and
    /// nothing of anybody else's is kept; the operator's egress proxy, because the reason for one
    /// (spec 2.3.1) is this host's network, which whose cookie is on the request does not change.
    /// </summary>
    /// <remarks>
    /// It follows no redirect by itself either. Both callers check every hop against VRChat's own
    /// hosts before sending it, and a handler that follows a <c>Location</c> on its own sends a
    /// hop nobody checked -- with whatever headers were on the request.
    /// </remarks>
    private HttpClient ClientWithoutCookies(VRChatConnection connection)
    {
        var key = $"{connection.ProxyUrl}\0{connection.ProxyUsername}\0{connection.ProxyPassword}";

        lock (_withoutCookiesLock)
        {
            if (_withoutCookies is { } current && current.ProxyKey == key)
                return current.Client;

            // Retired, never disposed: see _retired.
            if (_withoutCookies is { } old)
                _retired.Add(old.Client);

            var client = _handlerWithoutCookies is null
                ? new HttpClient(Handler(connection), disposeHandler: true) { Timeout = PassthroughTimeout }
                : new HttpClient(_handlerWithoutCookies(), disposeHandler: false) { Timeout = PassthroughTimeout };
            _withoutCookies = (key, client);
            return client;
        }

        static HttpClientHandler Handler(VRChatConnection connection)
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            };

            if (!string.IsNullOrWhiteSpace(connection.ProxyUrl))
            {
                var proxy = new WebProxy(connection.ProxyUrl, true);
                if (!string.IsNullOrWhiteSpace(connection.ProxyUsername))
                {
                    proxy.Credentials = new NetworkCredential(
                        connection.ProxyUsername, connection.ProxyPassword ?? string.Empty);
                }

                handler.Proxy = proxy;
                handler.UseProxy = true;
            }

            return handler;
        }
    }

    /// <summary>
    /// Disposed at the end, when the host has stopped and no request is left to be reading one.
    /// That is the only moment at which tearing a connection pool down is safe.
    /// </summary>
    public void Dispose()
    {
        _session.Dispose();
        _loading.Dispose();

        lock (_withoutCookiesLock)
        {
            _withoutCookies?.Client.Dispose();
            _withoutCookies = null;

            foreach (var retired in _retired)
                retired.Dispose();

            _retired.Clear();
        }
    }

    private async Task<VRChatResult<T>> IssueAsync<T>(
        IVRChat client,
        VRChatEndpoint endpoint,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority,
        CancellationToken ct)
    {
        var lease = await _limiter.AcquireAsync(endpoint, priority, ct).ConfigureAwait(false);
        await using (lease)
        {
            if (!lease.IsAcquired)
            {
                var denial = lease.Denial!;
                _state = VRChatSessionState.RateLimited;

                // Fail fast with something an operator can read, rather than queueing into a
                // penalty nobody can see the end of (spec 4.3.1).
                return VRChatResult<T>.Failure(
                    0,
                    $"{denial.Bucket} is rate limited ({denial.Reason}); " +
                    $"nothing will be sent on it for another {denial.RetryAfter:g}.",
                    kind: VRChatFailureKind.RateLimited);
            }

            var started = _elapsed.Elapsed;

            ApiResponse<T> response;
            try
            {
                response = await call(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Only when the *caller* gave up. VRChat.API reports its own HTTP timeout as a
                // TaskCanceledException wrapping a TimeoutException, and rethrowing that as a
                // cancellation escapes the gate entirely -- so a host whose egress is silently
                // dropped answered spec 7.1.1's connection check with a 500 and a stack trace
                // instead of "the connection timed out, and here is what to check".
                throw;
            }
            catch (Exception exception)
            {
                // No response, so no evidence about the rate limit in either direction. Reporting
                // it as a failure would spend budget on a request VRChat may never have seen.
                HttpLog.Failed(
                    _logger, ServiceName, endpoint.Class, endpoint.Operation ?? "call",
                    exception, _elapsed.Elapsed - started);

                await lease.ReportAsync(0, ct).ConfigureAwait(false);

                // The exception is the only place a DNS failure, a timeout and a refused
                // connection are distinguishable -- by the time this leaves the gate they are all
                // "no response". Spec 7.1.1 needs them apart, so they are classified here.
                return VRChatResult<T>.Failure(
                    0, exception.Message, kind: VRChatTransportFailure.Classify(exception));
            }

            var status = (int)response.StatusCode;

            HttpLog.Completed(
                _logger, ServiceName, endpoint.Class, endpoint.Operation ?? "call",
                status, _elapsed.Elapsed - started, lease.Tokens);

            await lease.ReportAsync(status, ct).ConfigureAwait(false);

            return Interpret(endpoint, response, status);
        }
    }

    private VRChatResult<T> Interpret<T>(VRChatEndpoint endpoint, ApiResponse<T> response, int status)
    {
        if (status is >= 200 and < 300)
        {
            // A coarse summary: something is working. Which individual buckets are stopped is a
            // different question, and DescribeBucketsAsync is where the UI asks it (spec 4.3.3).
            //
            // Lost group access is the exception. A profile read succeeding says nothing about the
            // group, so only a group read clears it.
            if (_state is not VRChatSessionState.NoGroupAccess
                || endpoint.Class.StartsWith("groups.", StringComparison.Ordinal))
            {
                _state = VRChatSessionState.Healthy;
                _lostGroup = null;
            }

            return VRChatResult<T>.Ok(response.Data, status, response.RawContent);
        }

        if (status == 429)
        {
            _state = VRChatSessionState.RateLimited;
            return VRChatResult<T>.Failure(
                status,
                $"VRChat rate limited {endpoint}. The bucket is now cold-stopped; nothing will be retried.",
                rawResponse: response.RawContent,
                kind: VRChatFailureKind.RateLimited);
        }

        if (WafBlock.TryClassify(status, response.ErrorText, response.RawContent, out var wafCode))
        {
            _state = VRChatSessionState.WafBlocked;
            _logger
                .ForContext(LogArea.Name, LogArea.Http)
                .Error(
                    "Cloudflare blocked {Endpoint} with code {WafCode}. This host's network cannot reach "
                    + "the VRChat API; an egress proxy is the fix",
                    endpoint.ToString(), wafCode);

            return VRChatResult<T>.Failure(
                status,
                "Cloudflare's WAF blocked this request. The VRChat account is fine -- this host's "
                + "network is not allowed to reach the API. Configure an egress proxy.",
                wafCode,
                WafBlock.Payload(response.RawContent ?? response.ErrorText),
                VRChatFailureKind.WafBlocked);
        }

        return VRChatResult<T>.Failure(
            status,
            response.ErrorText ?? $"VRChat returned {status} for {endpoint}.",
            rawResponse: response.RawContent,
            kind: status == (int)HttpStatusCode.Unauthorized
                ? VRChatFailureKind.CredentialsRejected
                : VRChatFailureKind.Other);
    }

    /// <summary>What the caller needs from the session.</summary>
    private enum Need
    {
        /// <summary>A call is about to be made. Use the session there is, or establish one.</summary>
        Use,

        /// <summary>A call came back 401. Check the session, and sign in only if it is really bad.</summary>
        Renew,

        /// <summary>
        /// An operator asked (the wizard, Settings). Rebuild from stored settings -- the proxy or
        /// the account may have changed -- and prove it works.
        /// </summary>
        Check,
    }

    private enum CheckOutcome
    {
        Good,
        SessionRejected,
        NoAnswer,
    }

    private async Task<SessionResult> EnsureSessionAsync(
        Need need, int failedSession, int renewalsBefore, VRChatCallPriority priority, CancellationToken ct)
    {
        await _session.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(ct).ConfigureAwait(false);
            var now = _clock.UtcNow;

            switch (need)
            {
                // Being rate limited or WAF blocked says nothing about the session -- RateLimited's
                // own definition is "not broken, waiting, on purpose" -- and rebuilding on either
                // would send a check or a sign-in at exactly the moment spec 4.3.1 requires
                // silence. Only a missing session, or a wait on signing in, means it cannot be used.
                case Need.Use when _client is not null && _wait is null:
                    return SessionResult.Ok(_client, _sessionNumber);

                // Somebody else's 401 has been answered since this call was sent. Many calls failing
                // together share one check and at most one sign-in; the rest take its result.
                case Need.Renew when Volatile.Read(ref _renewals) != renewalsBefore:
                    if (_client is not null && _sessionNumber != failedSession)
                        return SessionResult.Ok(_client, _sessionNumber);

                    return _lastRenewal;
            }

            var result = await EstablishAsync(need, now, priority, ct).ConfigureAwait(false);

            if (need is Need.Renew)
            {
                _lastRenewal = result;
                Interlocked.Increment(ref _renewals);
            }

            return result;
        }
        finally
        {
            _session.Release();
        }
    }

    private async Task<SessionResult> EstablishAsync(
        Need need, DateTimeOffset now, VRChatCallPriority priority, CancellationToken ct)
    {
        var connection = await _connections.ReadAsync(ct).ConfigureAwait(false);
        if (!connection.IsConfigured)
        {
            DropSession();
            _state = VRChatSessionState.Unconfigured;
            return SessionResult.Fail(
                0,
                "No VRChat account is configured. Complete onboarding first.",
                kind: VRChatFailureKind.NotConfigured);
        }

        // A session belonging to a different account is not this account's session, however well
        // it works (the operator has just changed the credentials).
        if (_client is not null
            && !string.Equals(_account?.Account, connection.Username, StringComparison.OrdinalIgnoreCase))
        {
            DropSession();
        }

        // Spec 4.1.2: while the wait lasts, nothing that could count as a sign-in is sent, and
        // nothing that needs a session either. Not even an operator's deliberate change: that
        // would only turn one hour of waiting into two.
        if (_wait is { } wait && now < wait.RetryAt)
        {
            DropSession();
            return Waiting(wait);
        }

        if (need is Need.Renew)
        {
            _logger
                .ForContext(LogArea.Name, LogArea.Http)
                .Information("VRChat returned 401; checking whether the session still works");
        }

        // The session to check: the one that just got a 401, or the stored cookies. Never checked
        // with /auth/user, which VRChat treats as signing in again (spec 4.1.2).
        var existing = need is Need.Renew ? _client : null;
        var candidate = existing
            ?? (connection.HasSession ? _clients.Create(connection.WithoutCredentials()) : null);

        if (candidate is not null)
        {
            var (outcome, answer) = await CheckSessionAsync(candidate, connection, priority, ct)
                .ConfigureAwait(false);

            switch (outcome)
            {
                // An operator's check needs to say which account it is. A session stored before the
                // user id was kept beside it cannot, so that one case signs in -- once, after which
                // the id is stored.
                case CheckOutcome.Good
                    when need is Need.Check && string.IsNullOrWhiteSpace(connection.SessionUserId):
                    break;

                case CheckOutcome.Good:
                    return await AdoptAsync(candidate, connection, ct).ConfigureAwait(false);

                case CheckOutcome.NoAnswer:
                    // Not evidence the session is bad. A 429 on the check is an ordinary cold stop
                    // on that bucket, and a timeout or a Cloudflare block is a network problem --
                    // none of them is a reason to sign in, and none is shown as one.
                    return SessionResult.Fail(
                        answer.StatusCode,
                        answer.ErrorMessage ?? "The session check got no answer from VRChat.",
                        answer.WafCode,
                        answer.Kind);

                case CheckOutcome.SessionRejected:
                    _logger
                        .ForContext(LogArea.Name, LogArea.Http)
                        .Information("The stored VRChat session was rejected; signing in again");

                    DropSession();
                    await _connections.SaveSessionAsync(null, connection.TwoFactorAuthCookie, ct)
                        .ConfigureAwait(false);
                    break;
            }
        }

        // Credentials VRChat has already refused are not sent again on anyone's behalf but the
        // operator's. Retrying a refused password only locks the account faster.
        if (need is not Need.Check && _refusedCredentials == Fingerprint(connection))
        {
            _state = VRChatSessionState.Unconfigured;
            return SessionResult.Fail(
                (int)HttpStatusCode.Unauthorized,
                $"VRChat rejected the credentials for {connection.Username}. Check the account in settings.",
                kind: VRChatFailureKind.CredentialsRejected);
        }

        return await SignInWithPasswordAsync(connection.ForSignIn(), now, priority, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a session works, without signing in (spec 4.1.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps, each asked only when the one before could not say:
    /// </para>
    /// <list type="number">
    /// <item><c>GET /auth</c>, Verify Auth Token. The maintainer confirmed it does not sign in
    /// again. <c>ok: true</c> is a good session; <c>ok: false</c> or a 401 is a bad one.</item>
    /// <item>Get User on a profile any signed-in account can read. 200 is good, 401 is bad.</item>
    /// <item>Get Group on the managed group. 200 is good. A refusal here cannot be told apart from
    /// a lost group, so it is not read as a bad session.</item>
    /// </list>
    /// <para>
    /// A 429 at any step is that bucket's ordinary cold stop, and ends the check there: no sign-in,
    /// and no sign-in banner.
    /// </para>
    /// </remarks>
    private async Task<(CheckOutcome Outcome, VRChatResult<object> Answer)> CheckSessionAsync(
        IVRChat client, VRChatConnection connection, VRChatCallPriority priority, CancellationToken ct)
    {
        var token = await IssueAsync<VerifyAuthTokenResult>(
                client,
                new VRChatEndpoint(VRChatEndpointClass.AuthVerify, Operation: "VerifyAuthToken"),
                (vrchat, t) => vrchat.Authentication.VerifyAuthTokenWithHttpInfoAsync(t),
                priority,
                ct)
            .ConfigureAwait(false);

        switch (ReadVerifyAuthToken(token))
        {
            case true:
                return (CheckOutcome.Good, VRChatResult<object>.From(token));

            case false:
                return (CheckOutcome.SessionRejected, VRChatResult<object>.From(token));

            case null when StopsTheCheck(token):
                return (CheckOutcome.NoAnswer, VRChatResult<object>.From(token));
        }

        var checkUserId = connection.CheckUserId;

        var user = await IssueAsync<UserResponse>(
                client,
                new VRChatEndpoint(VRChatEndpointClass.UsersRead, null, "GetUser (session check)"),
                (vrchat, t) => vrchat.Users.GetUserWithHttpInfoAsync(checkUserId, t),
                priority,
                ct)
            .ConfigureAwait(false);

        if (user.Success)
            return (CheckOutcome.Good, VRChatResult<object>.From(user));

        if (user.StatusCode == (int)HttpStatusCode.Unauthorized)
            return (CheckOutcome.SessionRejected, VRChatResult<object>.From(user));

        if (StopsTheCheck(user) || connection.GroupId is not { Length: > 0 } groupId)
            return (CheckOutcome.NoAnswer, VRChatResult<object>.From(user));

        var group = await IssueAsync<Group>(
                client,
                new VRChatEndpoint(VRChatEndpointClass.GroupsRead, groupId, "GetGroup (session check)"),
                (vrchat, t) => vrchat.Groups.GetGroupWithHttpInfoAsync(groupId, cancellationToken: t),
                priority,
                ct)
            .ConfigureAwait(false);

        if (group.Success)
            return (CheckOutcome.Good, VRChatResult<object>.From(group));

        // Step 2 did not end in a 401, or the check would have stopped there -- so a refusal here
        // is not read as a bad session. It could as easily be the group.
        return (CheckOutcome.NoAnswer, group.StatusCode is 401 or 403
            ? VRChatResult<object>.Failure(
                group.StatusCode, "Could not tell whether the VRChat session works.", kind: VRChatFailureKind.Other)
            : VRChatResult<object>.From(group));
    }

    /// <summary>What Verify Auth Token said: true, false, or null for no clear answer.</summary>
    /// <remarks>
    /// The body is read by hand rather than trusted to the SDK's model, because a missing
    /// <c>ok</c> would deserialise as <c>false</c> -- and an unexpected body is "unknown", not
    /// "signed out". The <c>token</c> beside it is the session itself and is never kept or logged.
    /// </remarks>
    private static bool? ReadVerifyAuthToken(VRChatResult<VerifyAuthTokenResult> result)
    {
        if (result.StatusCode == (int)HttpStatusCode.Unauthorized)
            return false;

        if (!result.Success || string.IsNullOrWhiteSpace(result.RawResponse))
            return null;

        try
        {
            using var body = JsonDocument.Parse(result.RawResponse);

            return body.RootElement.ValueKind == JsonValueKind.Object
                   && body.RootElement.TryGetProperty("ok", out var ok)
                   && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ok.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A rate limit ends the check where it is: asking the next endpoint is not waiting.</summary>
    private static bool StopsTheCheck<T>(VRChatResult<T> result) =>
        result.StatusCode == 429 || result.Kind is VRChatFailureKind.RateLimited;

    /// <summary>A checked session becomes the session.</summary>
    private async Task<SessionResult> AdoptAsync(IVRChat client, VRChatConnection connection, CancellationToken ct)
    {
        if (!ReferenceEquals(client, _client))
        {
            _client = client;
            _sessionNumber++;
            _account = new VRChatSignedInAccount(connection.Username, connection.SessionUserId, connection.DisplayName);
            _savedAuth = connection.AuthCookie;
            _savedTwoFactor = connection.TwoFactorAuthCookie;
        }

        // A working session is the end of any wait: nothing needs signing in.
        if (_wait is not null)
        {
            await EndWaitAsync(ct).ConfigureAwait(false);
            _logger.Information("The stored VRChat session works again; the wait to sign in is over");
        }

        return SessionResult.Ok(client, _sessionNumber);
    }

    private static bool IsGroupCall(VRChatEndpoint endpoint) =>
        endpoint.Class.StartsWith("groups.", StringComparison.Ordinal);

    private bool LostGroupStands(int session) =>
        _lostGroup is { } lost
        && lost.Session == session
        && _clock.UtcNow - lost.At < LostGroupStandsFor;

    /// <summary>
    /// The session works and the group refused: the account has lost the group. Shown as that, and
    /// never answered by signing in again (spec 4.1.2).
    /// </summary>
    private void MarkLostGroup(int session, VRChatEndpoint endpoint)
    {
        var already = _state is VRChatSessionState.NoGroupAccess;

        _state = VRChatSessionState.NoGroupAccess;
        _lostGroup = (session, _clock.UtcNow);

        if (!already)
        {
            _logger.Warning(
                "The VRChat session works, but the account cannot read the group ({Endpoint})",
                endpoint.ToString());
        }
    }

    private static VRChatResult<T> LostGroupFailure<T>(VRChatResult<T> result) =>
        VRChatResult<T>.Failure(
            result.StatusCode,
            "The VRChat account cannot read the group.",
            rawResponse: result.RawResponse,
            kind: VRChatFailureKind.Other);

    /// <summary>
    /// Spec 4.1.1's flow, driven on <c>...WithHttpInfoAsync</c> so every branch has a status, and
    /// spec 4.1.2's limit on how often it may run.
    /// </summary>
    private async Task<SessionResult> SignInWithPasswordAsync(
        VRChatConnection connection, DateTimeOffset now, VRChatCallPriority priority, CancellationToken ct)
    {
        var hasTotp = !string.IsNullOrWhiteSpace(connection.TotpSecret);

        // A sign-in that cannot finish is not started: with a TOTP secret and no two-factor cookie,
        // VRChat will ask for a code, and that is three counted requests, not one.
        var needed = hasTotp && string.IsNullOrWhiteSpace(connection.TwoFactorAuthCookie) ? 3 : 1;

        if (CheckLimit(needed, now) is { } full)
            return await StartWaitAsync(full, ct).ConfigureAwait(false);

        DropSession();
        _state = VRChatSessionState.Reauthenticating;

        var client = _clients.Create(connection);

        var current = await CountedAsync<CurrentUserLoginResponse>(
                client, "GetCurrentUser",
                (vrchat, token) => vrchat.Authentication.GetCurrentUserWithHttpInfoAsync(token),
                priority, ct)
            .ConfigureAwait(false);

        if (IsSignInRateLimit(current))
            return await RateLimitedAsync(ct).ConfigureAwait(false);

        if (!current.Success)
            return await RejectedAsync(current, connection, now, ct).ConfigureAwait(false);

        var user = current.Value;
        if (user is null)
        {
            await EndWaitIfOverAsync(now, ct).ConfigureAwait(false);
            return SessionResult.Fail(
                current.StatusCode,
                "VRChat returned no user for these credentials.",
                kind: VRChatFailureKind.Other);
        }

        if (user.RequiresTwoFactorAuth is { Count: > 0 } methods)
        {
            if (!hasTotp)
            {
                _state = VRChatSessionState.Unconfigured;
                _refusedCredentials = Fingerprint(connection);
                await EndWaitIfOverAsync(now, ct).ConfigureAwait(false);

                return SessionResult.Fail(
                    0,
                    // VRChat's own spelling for each method, not the SDK's: specification v1.21.0
                    // turned requiresTwoFactorAuth from a list of strings into an enum, and an
                    // operator matching this message against VRChat's documentation is looking
                    // for "emailOtp", not "EmailOtp".
                    "VRChat is asking for a two-factor code ("
                    + string.Join(", ", methods.Select(VRChatWords.Of)) + ") and no TOTP "
                    + "secret is configured. Modbot is a daemon: it cannot ask anyone for a code.",
                    kind: VRChatFailureKind.TwoFactorMissing);
            }

            // The two-factor cookie did not spare the challenge. Stop here rather than half-way.
            if (CheckLimit(2, _clock.UtcNow) is { } noRoom)
                return await StartWaitAsync(noRoom, ct).ConfigureAwait(false);

            // The code is derived from Modbot's clock, not the machine's, for the same reason every
            // other time-dependent value is (spec 4.4): a host whose clock has drifted would
            // generate codes VRChat rejects, and the failure would look like a wrong secret.
            var totp = new Totp(Base32Encoding.ToBytes(connection.TotpSecret));
            var code = totp.ComputeTotp(_clock.UtcNow.UtcDateTime);

            var verified = await CountedAsync<Verify2FAResult>(
                    client, "Verify2FA",
                    (vrchat, token) => vrchat.Authentication.Verify2FAWithHttpInfoAsync(
                        new TwoFactorAuthCode(code), token),
                    priority, ct)
                .ConfigureAwait(false);

            if (IsSignInRateLimit(verified))
                return await RateLimitedAsync(ct).ConfigureAwait(false);

            if (!verified.Success)
                return await RejectedAsync(verified, connection, now, ct).ConfigureAwait(false);

            if (verified.Value is { Verified: false })
            {
                _state = VRChatSessionState.Unconfigured;
                _refusedCredentials = Fingerprint(connection);
                await EndWaitIfOverAsync(now, ct).ConfigureAwait(false);

                return SessionResult.Fail(
                    verified.StatusCode,
                    "VRChat did not accept the two-factor code. Check the TOTP secret in settings.",
                    kind: VRChatFailureKind.CredentialsRejected);
            }

            current = await CountedAsync<CurrentUserLoginResponse>(
                    client, "GetCurrentUser (after two-factor)",
                    (vrchat, token) => vrchat.Authentication.GetCurrentUserWithHttpInfoAsync(token),
                    priority, ct)
                .ConfigureAwait(false);

            if (IsSignInRateLimit(current))
                return await RateLimitedAsync(ct).ConfigureAwait(false);

            if (!current.Success)
                return await RejectedAsync(current, connection, now, ct).ConfigureAwait(false);

            user = current.Value ?? user;
        }

        var account = new VRChatSignedInAccount(connection.Username, user.Id, user.DisplayName);
        var (auth, twoFactor) = CookiesOf(client);
        twoFactor ??= connection.TwoFactorAuthCookie;

        if (auth is not null)
            await _connections.SaveSignInAsync(auth, twoFactor, account, ct).ConfigureAwait(false);

        var signedInAt = _clock.UtcNow;
        await _signIns.RecordSignedInAsync(signedInAt, ct).ConfigureAwait(false);
        _lastSignedInAt = signedInAt;

        var waited = _wait is not null;
        await EndWaitAsync(ct).ConfigureAwait(false);

        _client = client;
        _sessionNumber++;
        _account = account;
        _savedAuth = auth;
        _savedTwoFactor = twoFactor;
        _refusedCredentials = null;
        _lostGroup = null;
        _state = VRChatSessionState.Healthy;

        if (waited)
            _logger.Information("Signed in to VRChat again as {DisplayName} ({UserId})", user.DisplayName, user.Id);
        else
            _logger.Information("Signed in to VRChat as {DisplayName} ({UserId})", user.DisplayName, user.Id);

        return SessionResult.Ok(client, _sessionNumber, user);
    }

    /// <summary>
    /// Sends one request that could count as a sign-in, recording it first.
    /// </summary>
    /// <remarks>
    /// Recorded inside the call, after the limiter has let it through and immediately before it is
    /// sent: a request the limiter refused was never sent, and a process that dies mid-request has
    /// still spent it as far as VRChat is concerned.
    /// </remarks>
    private Task<VRChatResult<T>> CountedAsync<T>(
        IVRChat client,
        string operation,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority,
        CancellationToken ct) =>
        IssueAsync<T>(
            client,
            new VRChatEndpoint(VRChatEndpointClass.Auth, Operation: operation),
            async (vrchat, token) =>
            {
                var at = _clock.UtcNow;

                lock (_attemptsLock)
                {
                    _attempts.RemoveAll(a => a <= at - SignInBudget.Window);
                    _attempts.Add(at);
                }

                await _signIns.RecordAttemptAsync(at, operation, token).ConfigureAwait(false);
                return await call(vrchat, token).ConfigureAwait(false);
            },
            priority,
            ct);

    private SignInWait? CheckLimit(int needed, DateTimeOffset now)
    {
        lock (_attemptsLock)
            return SignInBudget.Check(_attempts, _signInLimit, needed, now);
    }

    /// <summary>
    /// Whether VRChat said the sign-in limit was hit.
    /// </summary>
    /// <remarks>
    /// A 429, first of all. VRChat's exact answer to too many sign-ins is not documented and has
    /// not been captured, so a refusal whose body says "too many" is read the same way: waiting an
    /// hour when it was not needed costs an hour, and signing in again when it was costs another.
    /// </remarks>
    private static bool IsSignInRateLimit<T>(VRChatResult<T> result)
    {
        if (result.StatusCode == 429)
            return true;

        if (result.StatusCode is not (401 or 403))
            return false;

        var body = result.RawResponse ?? result.ErrorMessage;
        return body is not null && body.Contains("too many", StringComparison.OrdinalIgnoreCase);
    }

    private Task<SessionResult> RateLimitedAsync(CancellationToken ct) =>
        StartWaitAsync(
            new SignInWait(SignInWaitReason.RateLimitedByVRChat, _clock.UtcNow + SignInBudget.RateLimitWait),
            ct);

    private async Task<SessionResult> StartWaitAsync(SignInWait wait, CancellationToken ct)
    {
        DropSession();

        var isNew = _wait != wait;
        _wait = wait;

        if (isNew)
        {
            await _signIns.SaveWaitAsync(wait, ct).ConfigureAwait(false);

            // Once when the wait starts. Every call refused during it is silent: the state, the
            // health endpoint and the banner say it, and a line a second in the log would not.
            if (wait.Reason is SignInWaitReason.RateLimitedByVRChat)
            {
                _logger.Warning(
                    "VRChat rate limited signing in. Nothing that signs in or needs a session will be sent "
                    + "to VRChat until {RetryAt}",
                    wait.RetryAt);
            }
            else
            {
                _logger.Warning(
                    "Modbot has used its {Limit} sign-ins for this hour. Nothing that signs in or needs a "
                    + "session will be sent to VRChat until {RetryAt}",
                    _signInLimit, wait.RetryAt);
            }
        }

        return Waiting(wait);
    }

    private static SessionResult Waiting(SignInWait wait) =>
        SessionResult.Fail(
            0,
            $"Waiting to sign in to VRChat until {wait.RetryAt:yyyy-MM-dd HH:mm:ss} UTC.",
            kind: VRChatFailureKind.SignInWaiting);

    private async Task EndWaitAsync(CancellationToken ct)
    {
        if (_wait is null)
            return;

        _wait = null;
        await _signIns.SaveWaitAsync(null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A sign-in after the wait that failed for some other reason ends the wait: the wait was about
    /// the rate limit, and what went wrong now is shown as what it is.
    /// </summary>
    private Task EndWaitIfOverAsync(DateTimeOffset now, CancellationToken ct) =>
        _wait is { } wait && now >= wait.RetryAt ? EndWaitAsync(ct) : Task.CompletedTask;

    private async Task<SessionResult> RejectedAsync<T>(
        VRChatResult<T> result, VRChatConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        await EndWaitIfOverAsync(now, ct).ConfigureAwait(false);

        // 401 here means the password was refused, not that a session expired: this request did not
        // carry one. That is an operator problem, and trying it again would only lock the account
        // faster.
        if (result.StatusCode == (int)HttpStatusCode.Unauthorized)
        {
            _state = VRChatSessionState.Unconfigured;
            _refusedCredentials = Fingerprint(connection);

            return SessionResult.Fail(
                result.StatusCode,
                $"VRChat rejected the credentials for {connection.Username}. Check the account in settings.",
                kind: VRChatFailureKind.CredentialsRejected);
        }

        return SessionResult.Fail(
            result.StatusCode, result.ErrorMessage ?? "VRChat sign-in failed.", result.WafCode, result.Kind);
    }

    /// <summary>
    /// Stores cookies VRChat replaced, so the next start-up uses the newest session rather than one
    /// VRChat has already retired.
    /// </summary>
    private async Task KeepNewCookiesAsync(IVRChat client, CancellationToken ct)
    {
        var (auth, twoFactor) = CookiesOf(client);

        if (auth is null || (auth == _savedAuth && (twoFactor is null || twoFactor == _savedTwoFactor)))
            return;

        await _session.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(client, _client) || (auth == _savedAuth && (twoFactor is null || twoFactor == _savedTwoFactor)))
                return;

            twoFactor ??= _savedTwoFactor;
            await _connections.SaveSessionAsync(auth, twoFactor, ct).ConfigureAwait(false);

            _savedAuth = auth;
            _savedTwoFactor = twoFactor;
        }
        finally
        {
            _session.Release();
        }
    }

    private static (string? Auth, string? TwoFactor) CookiesOf(IVRChat client)
    {
        var cookies = client.GetCookies() ?? [];

        return (Find(cookies, "auth"), Find(cookies, "twoFactorAuth"));

        // The newest of each, in case the jar holds the stored cookie and the one VRChat set in
        // its place under slightly different domain rules.
        static string? Find(IEnumerable<Cookie> cookies, string name) =>
            cookies
                .Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !c.Expired)
                .OrderByDescending(c => c.TimeStamp)
                .FirstOrDefault()?.Value;
    }

    private void DropSession()
    {
        if (_client is null)
            return;

        _client = null;
        _account = null;
        _lostGroup = null;
        _sessionNumber++;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded)
            return;

        await _loading.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
                return;

            var now = _clock.UtcNow;
            var stored = await _signIns.LoadAsync(now - SignInBudget.Window, ct).ConfigureAwait(false);

            lock (_attemptsLock)
                _attempts = [.. stored.Attempts];

            _wait = stored.Wait;
            _lastSignedInAt = stored.LastSignedInAt;
            _loaded = true;

            if (_wait is { } wait && now < wait.RetryAt)
                _logger.Information("Still waiting to sign in to VRChat, until {RetryAt}", wait.RetryAt);
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>
    /// The credentials, hashed, so the gate can tell whether they changed without keeping a second
    /// copy of the password in memory in the clear.
    /// </summary>
    private static string Fingerprint(VRChatConnection connection) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{connection.Username}\0{connection.Password}\0{connection.TotpSecret}")));

    private readonly record struct SessionResult(
        bool Success,
        IVRChat? Client,
        CurrentUserLoginResponse? User,
        int Number,
        int StatusCode,
        string? ErrorMessage,
        int? WafCode,
        VRChatFailureKind Kind = VRChatFailureKind.None)
    {
        public static SessionResult Ok(IVRChat client, int number, CurrentUserLoginResponse? user = null) =>
            new(true, client, user, number, 200, null, null);

        public static SessionResult Fail(
            int statusCode,
            string errorMessage,
            int? wafCode = null,
            VRChatFailureKind kind = VRChatFailureKind.Other) =>
            new(false, null, null, 0, statusCode, errorMessage, wafCode, kind);

        /// <summary>Re-types a session failure so a caller waiting on data gets the same reason.</summary>
        public VRChatResult<T> ToFailure<T>() =>
            VRChatResult<T>.Failure(StatusCode, ErrorMessage ?? "VRChat sign-in failed.", WafCode, kind: Kind);
    }
}
