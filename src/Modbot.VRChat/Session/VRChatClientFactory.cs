using System.Net;
using Modbot.Core;
using VRChat.API.Client;

namespace Modbot.VRChat.Session;

/// <summary>How Modbot identifies itself to VRChat (spec 4.1).</summary>
/// <remarks>
/// <para>
/// Being a legible API citizen is a design goal, not an afterthought. VRChat rejects requests
/// without a descriptive User-Agent, and an operator VRChat can contact is an operator VRChat
/// talks to before it blocks.
/// </para>
/// <para>
/// Two different people are named on every request, because they answer different questions.
/// The <strong>operator</strong> -- whoever runs this Modbot -- is the contact in the User-Agent,
/// supplied at run time by <see cref="IOperatorContact"/> from the administrator's account. The
/// <strong>developer</strong> -- the Modbot project -- rides in two fixed headers, so that when an
/// operator's address bounces or an install is abandoned, VRChat can still reach someone who can
/// explain what the traffic is and fix the code that produced it.
/// </para>
/// </remarks>
public sealed record VRChatClientOptions
{
    public string ApplicationName { get; init; } = "Modbot";

    public string ApplicationVersion { get; init; } = ModbotVersion.Release;

    /// <summary>Who wrote Modbot. Sent as <c>X-Modbot-Developer-Contact-Email</c>.</summary>
    public string DeveloperContactEmail { get; init; } = "me@bin.moe";

    /// <summary>Where Modbot lives. Sent as <c>X-Modbot-Developer-Contact-URL</c>.</summary>
    public string DeveloperContactUrl { get; init; } = "https://github.com/binn/Modbot";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The email address of whoever runs this Modbot: the contact VRChat sees in the User-Agent.
/// </summary>
/// <remarks>
/// Read every time a client is built rather than captured at start-up, because the
/// administrator's address does not exist until onboarding has finished and can change after it.
/// The implementation lives beside the account code that owns the address; this project only
/// knows that it may be absent, in which case the developer contact stands in so that VRChat
/// always has someone to write to.
/// </remarks>
public interface IOperatorContact
{
    string? Email { get; }
}

/// <summary>The default until something better is registered: no operator address known.</summary>
public sealed class NoOperatorContact : IOperatorContact
{
    public string? Email => null;
}

/// <summary>
/// The only place in Modbot that constructs a VRChat client.
/// </summary>
/// <remarks>
/// Spec 4.1: nothing else may ever build one, because a client built elsewhere is a client that
/// bypasses the rate limiter, the priority queue and the single session -- all three of which only
/// work if everything goes through them. It is a factory rather than inline construction in the
/// gate so that the gate can be tested without HTTP, and so this rule stays enforceable by
/// looking at one file.
/// </remarks>
public interface IVRChatClientFactory
{
    IVRChat Create(VRChatConnection connection);
}

public sealed class VRChatClientFactory(
    VRChatClientOptions? options = null,
    IOperatorContact? operatorContact = null) : IVRChatClientFactory
{
    private readonly VRChatClientOptions _options = options ?? new VRChatClientOptions();
    private readonly IOperatorContact _operator = operatorContact ?? new NoOperatorContact();

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

        // The developer is named on every request, not only in the User-Agent, so the project is
        // reachable from a single log line on VRChat's side even when the operator is not.
        client.Configuration.DefaultHeaders["X-Modbot-Developer-Contact-Email"] = _options.DeveloperContactEmail;
        client.Configuration.DefaultHeaders["X-Modbot-Developer-Contact-URL"] = _options.DeveloperContactUrl;

        return client;
    }

    /// <summary>
    /// The User-Agent contact: the operator's email, or the developer's when no operator address
    /// is known yet. Never empty -- a request with nobody to contact is the kind VRChat blocks.
    /// </summary>
    private string Contact()
    {
        var email = _operator.Email;
        return string.IsNullOrWhiteSpace(email) ? _options.DeveloperContactEmail : email.Trim();
    }

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
