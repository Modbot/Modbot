using System.Net.Sockets;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Spec 7.1.1: the connection check must tell a WAF block apart from DNS, a timeout and bad
/// credentials, because a proxy fixes exactly one of them.
/// </summary>
/// <remarks>
/// Every one of these arrives at the gate with no HTTP response and therefore no status code, so
/// if the exception is not read they are indistinguishable — and "configure a proxy" is the wrong
/// advice for three of the four.
/// </remarks>
public class TransportFailureTests
{
    [Fact]
    public void ANameThatWillNotResolveIsNotANetworkFailure()
    {
        var exception = new HttpRequestException(
            HttpRequestError.NameResolutionError,
            "No such host is known.",
            new SocketException((int)SocketError.HostNotFound));

        Assert.Equal(VRChatFailureKind.NameResolution, VRChatTransportFailure.Classify(exception));
    }

    [Fact]
    public void ASocketLevelLookupFailureIsAlsoDns()
    {
        // .NET does not always populate HttpRequestError -- on some platforms and for some
        // transports the only evidence is the nested SocketException, which is why the classifier
        // walks the whole chain rather than reading the outermost type.
        var exception = new HttpRequestException(
            "Resource temporarily unavailable",
            new SocketException((int)SocketError.TryAgain));

        Assert.Equal(VRChatFailureKind.NameResolution, VRChatTransportFailure.Classify(exception));
    }

    [Fact]
    public void TheSdksTimeoutIsATimeout()
    {
        // This is the exact shape VRChat.API throws when its own HttpClient timeout elapses: a
        // TaskCanceledException wrapping a TimeoutException. Without the nested one it would be
        // indistinguishable from an ordinary cancellation.
        var exception = new TaskCanceledException(
            "[GET] https://api.vrchat.cloud/... was timeout.",
            new TimeoutException("The operation was canceled."));

        Assert.Equal(VRChatFailureKind.Timeout, VRChatTransportFailure.Classify(exception));
    }

    [Fact]
    public void ARefusedConnectionIsNeitherDnsNorATimeout()
    {
        var exception = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(VRChatFailureKind.Network, VRChatTransportFailure.Classify(exception));
    }

    [Fact]
    public void AProxyThatWillNotTunnelIsANetworkFailure()
    {
        // The case an operator hits immediately after entering proxy credentials wrongly. It must
        // not read as "Cloudflare is blocking you", or they will go and buy a second proxy.
        var exception = new HttpRequestException(
            HttpRequestError.ProxyTunnelError, "The proxy tunnel request failed");

        Assert.Equal(VRChatFailureKind.Network, VRChatTransportFailure.Classify(exception));
    }

    [Fact]
    public void AnUnrecognisedFailureIsNotGuessedAt()
    {
        Assert.Equal(VRChatFailureKind.Other, VRChatTransportFailure.Classify(new InvalidOperationException()));
    }
}
