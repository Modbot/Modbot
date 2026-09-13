using System.Net;
using Modbot.Core.Logging;
using Modbot.Core.Time;
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
/// So every call here is a <c>...WithHttpInfoAsync</c> variant, and the status, the body and the
/// cookies are what drive the state machine.
/// </para>
/// </remarks>
public sealed class VRChatGate : IVRChatGate, IDisposable
{
    private const string ServiceName = "VRChat";

    private readonly IVRChatClientFactory _clients;
    private readonly IVRChatConnectionStore _connections;
    private readonly IRateLimiter _limiter;
    private readonly IModbotClock _clock;
    private readonly IMonotonicClock _elapsed;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _session = new(1, 1);

    private IVRChat? _client;

    public VRChatGate(
        IVRChatClientFactory clients,
        IVRChatConnectionStore connections,
        IRateLimiter limiter,
        IModbotClock clock,
        IMonotonicClock? elapsed = null,
        ILogger? logger = null)
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
    }

    public VRChatSessionState State { get; private set; } = VRChatSessionState.Unconfigured;

    public Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default) =>
        _limiter.DescribeAsync(ct);

    public async Task<VRChatResult<T>> ExecuteAsync<T>(
        VRChatEndpoint endpoint,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var session = await EnsureSessionAsync(SessionRefresh.Reuse, ct).ConfigureAwait(false);
        if (!session.Success)
            return session.ToFailure<T>();

        var result = await IssueAsync(session.Value!, endpoint, call, priority, ct).ConfigureAwait(false);

        if (result.StatusCode != (int)HttpStatusCode.Unauthorized)
            return result;

        // The session expired rather than the credentials being wrong -- the usual case, since
        // VRChat's cookies outlive nothing in particular. One re-login, then one retry, and never
        // a loop: if the second attempt is also a 401 the credentials themselves are the problem
        // and hammering the auth endpoint will not discover anything new.
        _logger
            .ForContext(LogArea.Name, LogArea.Http)
            .Information("VRChat returned 401 on {Endpoint}; re-authenticating once", endpoint.ToString());

        var renewed = await EnsureSessionAsync(SessionRefresh.Reauthenticate, ct).ConfigureAwait(false);
        if (!renewed.Success)
            return renewed.ToFailure<T>();

        return await IssueAsync(renewed.Value!, endpoint, call, priority, ct).ConfigureAwait(false);
    }

    public async Task<VRChatResult<CurrentUser>> SignInAsync(CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(SessionRefresh.Revalidate, ct).ConfigureAwait(false);

        return session.Success
            ? VRChatResult<CurrentUser>.Ok(session.User, 200)
            : session.ToFailure<CurrentUser>();
    }

    public void Dispose() => _session.Dispose();

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
                State = VRChatSessionState.RateLimited;

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
            catch (OperationCanceledException)
            {
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
            State = VRChatSessionState.Healthy;

            return VRChatResult<T>.Ok(response.Data, status, response.RawContent);
        }

        if (status == 429)
        {
            State = VRChatSessionState.RateLimited;
            return VRChatResult<T>.Failure(
                status,
                $"VRChat rate limited {endpoint}. The bucket is now cold-stopped; nothing will be retried.",
                rawResponse: response.RawContent,
                kind: VRChatFailureKind.RateLimited);
        }

        if (WafBlock.TryClassify(status, response.ErrorText, response.RawContent, out var wafCode))
        {
            State = VRChatSessionState.WafBlocked;
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

    /// <summary>How much of the session to rebuild.</summary>
    private enum SessionRefresh
    {
        /// <summary>Use the established session if there is one.</summary>
        Reuse,

        /// <summary>Rebuild from stored settings, stored cookie included, and check it.</summary>
        Revalidate,

        /// <summary>The stored session is known bad. Rebuild without it and log in properly.</summary>
        Reauthenticate,
    }

    private async Task<SessionResult> EnsureSessionAsync(SessionRefresh refresh, CancellationToken ct)
    {
        await _session.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (refresh is SessionRefresh.Reuse && _client is not null && State is VRChatSessionState.Healthy)
                return SessionResult.Ok(_client, user: null);

            var connection = await _connections.ReadAsync(ct).ConfigureAwait(false);
            if (!connection.IsConfigured)
            {
                State = VRChatSessionState.Unconfigured;
                return SessionResult.Fail(
                    0,
                    "No VRChat account is configured. Complete onboarding first.",
                    kind: VRChatFailureKind.NotConfigured);
            }

            // After a 401 the stored session is the thing that failed, so it is discarded rather
            // than sent again -- otherwise the retry authenticates with the cookie that just
            // caused the 401 and gets the same answer.
            if (refresh is SessionRefresh.Reauthenticate)
                connection = connection.WithoutSession();

            State = VRChatSessionState.Reauthenticating;

            var client = _clients.Create(connection);
            _client = client;

            return await AuthenticateAsync(client, connection, ct).ConfigureAwait(false);
        }
        finally
        {
            _session.Release();
        }
    }

    /// <summary>
    /// Spec 4.1.1's flow, driven on <c>...WithHttpInfoAsync</c> so every branch has a status.
    /// </summary>
    private async Task<SessionResult> AuthenticateAsync(
        IVRChat client, VRChatConnection connection, CancellationToken ct)
    {
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.Auth, Operation: "GetCurrentUser");

        var current = await IssueAsync<CurrentUser>(
                client, endpoint,
                (vrchat, token) => vrchat.Authentication.GetCurrentUserWithHttpInfoAsync(token),
                VRChatCallPriority.Interactive, ct)
            .ConfigureAwait(false);

        if (!current.Success)
        {
            // A 401 from a stored cookie means the session expired, which is ordinary and says
            // nothing about the password. One retry without it, and only one: if the password
            // login is refused too, the credentials are genuinely wrong (spec 4.1.1).
            if (current.StatusCode == (int)HttpStatusCode.Unauthorized && connection.AuthCookie is not null)
            {
                _logger
                    .ForContext(LogArea.Name, LogArea.Http)
                    .Information("The stored VRChat session was rejected; logging in with the password");

                await _connections.SaveSessionAsync(null, null, ct).ConfigureAwait(false);

                var fresh = connection.WithoutSession();
                var rebuilt = _clients.Create(fresh);
                _client = rebuilt;

                return await AuthenticateAsync(rebuilt, fresh, ct).ConfigureAwait(false);
            }

            return Rejected(current, connection);
        }

        var user = current.Value;
        if (user is null)
        {
            State = VRChatSessionState.Unconfigured;
            return SessionResult.Fail(
                current.StatusCode,
                "VRChat returned no user for these credentials.",
                kind: VRChatFailureKind.Other);
        }

        if (user.RequiresTwoFactorAuth is { Count: > 0 } methods)
        {
            var verified = await VerifyTwoFactorAsync(client, connection, methods, ct).ConfigureAwait(false);
            if (!verified.Success)
                return SessionResult.Fail(
                    verified.StatusCode, verified.ErrorMessage!, verified.WafCode, verified.Kind);

            current = await IssueAsync<CurrentUser>(
                    client, endpoint with { Operation = "GetCurrentUser (post-2FA)" },
                    (vrchat, token) => vrchat.Authentication.GetCurrentUserWithHttpInfoAsync(token),
                    VRChatCallPriority.Interactive, ct)
                .ConfigureAwait(false);

            if (!current.Success)
                return Rejected(current, connection);

            user = current.Value;
        }

        await PersistSessionAsync(client, ct).ConfigureAwait(false);

        State = VRChatSessionState.Healthy;
        _logger.Information("Authenticated to VRChat as {DisplayName} ({UserId})", user?.DisplayName, user?.Id);

        return SessionResult.Ok(client, user);
    }

    private async Task<VRChatResult<Verify2FAResult>> VerifyTwoFactorAsync(
        IVRChat client, VRChatConnection connection, IReadOnlyList<string> methods, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.TotpSecret))
        {
            State = VRChatSessionState.Unconfigured;
            return VRChatResult<Verify2FAResult>.Failure(
                0,
                "VRChat is asking for a two-factor code (" + string.Join(", ", methods) + ") and no TOTP "
                + "secret is configured. Modbot is a daemon: it cannot ask anyone for a code.",
                kind: VRChatFailureKind.TwoFactorMissing);
        }

        // The code is derived from Modbot's clock, not the machine's, for the same reason every
        // other time-dependent value is (spec 4.4): a host whose clock has drifted would generate
        // codes VRChat rejects, and the failure would look like a wrong secret.
        var totp = new Totp(Base32Encoding.ToBytes(connection.TotpSecret));
        var code = totp.ComputeTotp(_clock.UtcNow.UtcDateTime);

        return await IssueAsync<Verify2FAResult>(
                client,
                new VRChatEndpoint(VRChatEndpointClass.Auth, Operation: "Verify2FA"),
                (vrchat, token) => vrchat.Authentication.Verify2FAWithHttpInfoAsync(
                    new TwoFactorAuthCode(code), token),
                VRChatCallPriority.Interactive, ct)
            .ConfigureAwait(false);
    }

    private SessionResult Rejected<T>(VRChatResult<T> result, VRChatConnection connection)
    {
        // 401 here means the password was refused, not that a session expired: the stored session
        // was already dropped before this attempt. That is an operator problem, and re-trying it
        // would only lock the account faster.
        if (result.StatusCode == (int)HttpStatusCode.Unauthorized)
        {
            State = VRChatSessionState.Unconfigured;
            return SessionResult.Fail(
                result.StatusCode,
                $"VRChat rejected the credentials for {connection.Username}. Check the account in settings.",
                kind: VRChatFailureKind.CredentialsRejected);
        }

        return SessionResult.Fail(
            result.StatusCode, result.ErrorMessage ?? "VRChat login failed.", result.WafCode, result.Kind);
    }

    private async Task PersistSessionAsync(IVRChat client, CancellationToken ct)
    {
        var cookies = client.GetCookies() ?? [];

        var auth = Find(cookies, "auth");
        if (auth is null)
            return;

        await _connections
            .SaveSessionAsync(auth, Find(cookies, "twoFactorAuth"), ct)
            .ConfigureAwait(false);

        static string? Find(IEnumerable<Cookie> cookies, string name) =>
            cookies.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private readonly record struct SessionResult(
        bool Success,
        IVRChat? Value,
        CurrentUser? User,
        int StatusCode,
        string? ErrorMessage,
        int? WafCode,
        VRChatFailureKind Kind = VRChatFailureKind.None)
    {
        public static SessionResult Ok(IVRChat client, CurrentUser? user) =>
            new(true, client, user, 200, null, null);

        public static SessionResult Fail(
            int statusCode,
            string errorMessage,
            int? wafCode = null,
            VRChatFailureKind kind = VRChatFailureKind.Other) =>
            new(false, null, null, statusCode, errorMessage, wafCode, kind);

        /// <summary>Re-types a login failure so a caller waiting on data gets the same reason.</summary>
        public VRChatResult<T> ToFailure<T>() =>
            VRChatResult<T>.Failure(StatusCode, ErrorMessage ?? "VRChat login failed.", WafCode, kind: Kind);
    }
}
