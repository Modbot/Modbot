using System.Net;
using Modbot.Core;
using VRChat.API.Client;

namespace Modbot.VRChat.Session;

/// <summary>How Modbot identifies itself to VRChat (spec 4.1).</summary>
/// <remarks>
/// Being a legible API citizen is a design goal, not an afterthought. VRChat rejects requests
/// without a descriptive User-Agent, and an operator VRChat can contact is an operator VRChat
/// talks to before it blocks.
/// </remarks>
public sealed record VRChatClientOptions
{
    public string ApplicationName { get; init; } = "Modbot";

    public string ApplicationVersion { get; init; } = ModbotVersion.Release;

    /// <summary>Where VRChat can reach whoever runs this instance.</summary>
    public string ContactUrl { get; init; } = "https://github.com/binnsh/Modbot";

    public string? ContactEmail { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The only place in Modbot that constructs a VRChat client.
/// </summary>
/// <remarks>
/// Spec 4.1: nothing else may ever build one, because a client built elsewhere is a client that
/// bypasses the rate limiter, the priority queue and the single session — all three of which only
/// work if everything goes through them. It is a factory rather than inline construction in the
/// gate so that the gate can be tested without HTTP, and so this rule stays enforceable by
/// looking at one file.
/// </remarks>
public interface IVRChatClientFactory
{
    IVRChat Create(VRChatConnection connection);
}

public sealed class VRChatClientFactory(VRChatClientOptions? options = null) : IVRChatClientFactory
{
    private readonly VRChatClientOptions _options = options ?? new VRChatClientOptions();

    public IVRChat Create(VRChatConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var builder = new VRChatClientBuilder()
            .WithApplication(_options.ApplicationName, _options.ApplicationVersion, Contact())
            .WithTimeout(_options.Timeout);

        if (!string.IsNullOrWhiteSpace(connection.Username))
            builder = builder.WithUsername(connection.Username);

        if (!string.IsNullOrWhiteSpace(connection.Password))
            builder = builder.WithPassword(connection.Password);

        if (!string.IsNullOrWhiteSpace(connection.TotpSecret))
            builder = builder.WithTwoFactorSecret(connection.TotpSecret);

        if (!string.IsNullOrWhiteSpace(connection.AuthCookie))
        {
            // The two-factor cookie is optional: without it a rebuilt session simply re-verifies,
            // which costs one more request against the auth endpoint. It is omitted rather than
            // sent empty, because an empty cookie is a cookie VRChat still has to reject.
            builder = string.IsNullOrWhiteSpace(connection.TwoFactorAuthCookie)
                ? builder.WithAuthCookie(connection.AuthCookie)
                : builder.WithAuthCookie(connection.AuthCookie, connection.TwoFactorAuthCookie);
        }

        if (!string.IsNullOrWhiteSpace(connection.ProxyUrl))
            builder = builder.WithProxy(Proxy(connection));

        var client = builder.Build();

        // Contact headers ride on every request, not only the User-Agent, so an operator is
        // reachable from a log line on VRChat's side as well as from a traffic sample.
        if (!string.IsNullOrWhiteSpace(_options.ContactEmail))
            client.Configuration.DefaultHeaders["X-Modbot-Contact-Email"] = _options.ContactEmail;

        client.Configuration.DefaultHeaders["X-Modbot-Contact-URL"] = _options.ContactUrl;

        return client;
    }

    private string Contact() =>
        string.IsNullOrWhiteSpace(_options.ContactEmail)
            ? _options.ContactUrl
            : $"{_options.ContactEmail}, {_options.ContactUrl}";

    private static WebProxy Proxy(VRChatConnection connection)
    {
        var proxy = new WebProxy(connection.ProxyUrl, true);

        if (!string.IsNullOrWhiteSpace(connection.ProxyUsername))
        {
            proxy.Credentials = new NetworkCredential(
                connection.ProxyUsername, connection.ProxyPassword ?? string.Empty);
        }

        return proxy;
    }
}
