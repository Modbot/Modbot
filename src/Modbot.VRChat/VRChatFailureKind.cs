using System.Net.Http;
using System.Net.Sockets;

namespace Modbot.VRChat;

/// <summary>
/// Why a call through the gate did not succeed, at the granularity an operator can act on.
/// </summary>
/// <remarks>
/// <para>
/// Spec 7.1.1 is the reason this exists. The onboarding connection check has to tell a Cloudflare
/// WAF block apart from a DNS failure, a timeout and rejected credentials, because <em>only</em>
/// the first is fixed by an egress proxy — and telling an operator with a broken DNS resolver to
/// buy a proxy subscription sends them down a dead end they will not come back from quickly.
/// </para>
/// <para>
/// A status code alone cannot carry that. Every transport failure arrives as "no response at all",
/// which <see cref="VRChatResult{T}.StatusCode"/> reports as <c>0</c> for all of them. The
/// distinction lives in the exception, and the exception is only visible inside the gate — so the
/// gate classifies it once, here, rather than every caller re-deriving it from message text.
/// </para>
/// </remarks>
public enum VRChatFailureKind
{
    /// <summary>The call succeeded.</summary>
    None = 0,

    /// <summary>No VRChat account is configured yet. Onboarding has not got that far.</summary>
    NotConfigured,

    /// <summary>
    /// The host could not resolve VRChat's name. A proxy does not fix this; broken DNS, a
    /// captive portal or an offline container network do.
    /// </summary>
    NameResolution,

    /// <summary>
    /// The request was sent and nothing came back in time. Usually egress filtering or a
    /// black-holed route — and, when a proxy is configured, very often the proxy itself.
    /// </summary>
    Timeout,

    /// <summary>
    /// The connection could not be established or secured: refused, reset, TLS failure, or a
    /// proxy that would not tunnel. Distinct from <see cref="Timeout"/> because something
    /// answered, and distinct from <see cref="WafBlocked"/> because nothing HTTP happened.
    /// </summary>
    Network,

    /// <summary>
    /// Cloudflare refused the request before VRChat saw it. <strong>This is the one a proxy
    /// fixes</strong> (spec 2.3.1, 7.1.1).
    /// </summary>
    WafBlocked,

    /// <summary>VRChat rate limited, or a bucket is cold-stopped. Waiting is the only remedy.</summary>
    RateLimited,

    /// <summary>
    /// Nothing was sent: a sign-in is needed and Modbot is waiting out a rate limit on signing in
    /// (spec 4.1.2). Waiting is the only remedy, the same as <see cref="RateLimited"/>, and sync
    /// treats the two alike.
    /// </summary>
    SignInWaiting,

    /// <summary>VRChat rejected the username or password. Nothing about the network is wrong.</summary>
    CredentialsRejected,

    /// <summary>
    /// VRChat asked for a two-factor code and no TOTP secret is stored. Modbot is a daemon; it
    /// cannot ask anyone for one.
    /// </summary>
    TwoFactorMissing,

    /// <summary>Anything else, including VRChat's own 5xx. The message carries the detail.</summary>
    Other,
}

/// <summary>
/// Turns the exception a failed HTTP request throws into a <see cref="VRChatFailureKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>VRChat.API</c>'s generated methods catch <c>ApiException</c> and return a non-success
/// <c>ApiResponse</c> instead of throwing (spec 4.1) — but that only covers failures that produced
/// an HTTP response. A name that will not resolve, a refused connection or a proxy that will not
/// tunnel never reaches that catch, and arrives at the gate as a live exception. This is where it
/// is read.
/// </para>
/// <para>
/// Matching is on exception <em>types and codes</em>, never on message text: those messages are
/// localised, change between runtimes, and differ per platform. The one text-free signal .NET
/// gives for a timeout is the <see cref="TimeoutException"/> the SDK nests inside the
/// <see cref="TaskCanceledException"/> it throws when its own timeout elapses.
/// </para>
/// </remarks>
public static class VRChatTransportFailure
{
    /// <summary>
    /// Reads the whole exception chain, because the useful classification is usually two or three
    /// levels down inside an <see cref="HttpRequestException"/>.
    /// </summary>
    public static VRChatFailureKind Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TimeoutException:
                    return VRChatFailureKind.Timeout;

                case SocketException socket:
                    return FromSocketError(socket.SocketErrorCode);

                case HttpRequestException http when FromHttpRequestError(http.HttpRequestError) is { } kind:
                    return kind;
            }
        }

        // A cancellation with no nested TimeoutException is the caller's own token, not a
        // network problem -- but the gate only reaches here for genuine failures, and a shutdown
        // mid-check should not be reported as "your network is broken".
        return exception is OperationCanceledException
            ? VRChatFailureKind.Timeout
            : VRChatFailureKind.Other;
    }

    private static VRChatFailureKind FromSocketError(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
            => VRChatFailureKind.NameResolution,
        SocketError.TimedOut => VRChatFailureKind.Timeout,
        _ => VRChatFailureKind.Network,
    };

    private static VRChatFailureKind? FromHttpRequestError(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => VRChatFailureKind.NameResolution,

        HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError
            => VRChatFailureKind.Network,

        // Unknown is the default for an HttpRequestException whose cause was not classified by
        // the runtime, and it is not evidence of anything -- keep walking the chain, because the
        // SocketException underneath usually is.
        HttpRequestError.Unknown => null,

        _ => VRChatFailureKind.Other,
    };
}
