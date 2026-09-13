using System.Text.Json.Serialization;
using Modbot.VRChat;

namespace Modbot.Api.Features.Onboarding;

/// <summary>
/// What went wrong reaching VRChat, in the vocabulary the wizard speaks.
/// </summary>
/// <remarks>
/// A separate enum from <see cref="VRChatFailureKind"/> on purpose. That one is the gate's
/// internal classification and is free to gain members as the gate learns to tell more failures
/// apart; this one is an HTTP contract that generated clients and the documentation site are
/// built against. Mapping between them is one switch in one file, and it is the place where a
/// new internal classification has to be given a public name and a remedy before it can escape.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionOutcome>))]
public enum ConnectionOutcome
{
    Ok,
    NotConfigured,
    DnsFailure,
    Timeout,
    NetworkFailure,
    WafBlocked,
    RateLimited,
    CredentialsRejected,
    TwoFactorMissing,
    Error,
}

/// <summary>
/// One connection attempt, explained.
/// </summary>
/// <param name="Outcome">The classification the rest of the fields elaborate.</param>
/// <param name="ProxyWouldHelp">
/// <strong>True for exactly one outcome.</strong> Spec 7.1.1 exists because offering a proxy for
/// a DNS failure or a wrong password sends an operator to buy a subscription that cannot possibly
/// fix their problem, and then to conclude Modbot is broken when it does not.
/// </param>
/// <param name="Headline">One short sentence naming what happened.</param>
/// <param name="Detail">
/// Why, in the operator's terms. For a WAF block this is where "it is not your account and not
/// your fault" is said plainly, because the default assumption on a 403 is that you did something.
/// </param>
/// <param name="NextStep">What to do about it, or null when there is nothing to do.</param>
/// <param name="DisplayName">On success, the account VRChat says Modbot is.</param>
/// <param name="StatusCode">The HTTP status, or 0 when no request was completed.</param>
/// <param name="WafCode">Cloudflare's own error code, when the block page carried one.</param>
/// <param name="ElapsedMs">
/// How long the attempt took, measured monotonically. Shown because "reached VRChat in 412 ms"
/// and "reached VRChat in 9 seconds" are the difference between a working host and one about to
/// time out under load, and the second is worth noticing during setup rather than in a month.
/// </param>
public sealed record ConnectionDiagnosis(
    ConnectionOutcome Outcome,
    bool ProxyWouldHelp,
    string Headline,
    string Detail,
    string? NextStep,
    string? DisplayName,
    int StatusCode,
    int? WafCode,
    long ElapsedMs)
{
    /// <summary>Whether the account can be considered verified.</summary>
    public bool Succeeded => Outcome is ConnectionOutcome.Ok;

    /// <summary>
    /// A known-working provider, named because "find a proxy" is not actionable advice to
    /// somebody who has just learned proxies exist. Spec 7.1.1 names it explicitly.
    /// </summary>
    public const string SuggestedProxyProvider = "iproyal.com";

    public static ConnectionDiagnosis Describe<T>(VRChatResult<T> result, string? displayName, long elapsedMs)
    {
        var outcome = Classify(result);

        var (headline, detail, nextStep) = Explain(outcome, result, displayName);

        return new ConnectionDiagnosis(
            outcome,
            // The single decision this whole feature exists to get right.
            ProxyWouldHelp: outcome is ConnectionOutcome.WafBlocked,
            headline,
            detail,
            nextStep,
            outcome is ConnectionOutcome.Ok ? displayName : null,
            result.StatusCode,
            result.WafCode,
            elapsedMs);
    }

    private static ConnectionOutcome Classify<T>(VRChatResult<T> result)
    {
        if (result.Success)
            return ConnectionOutcome.Ok;

        return result.Kind switch
        {
            VRChatFailureKind.NotConfigured => ConnectionOutcome.NotConfigured,
            VRChatFailureKind.NameResolution => ConnectionOutcome.DnsFailure,
            VRChatFailureKind.Timeout => ConnectionOutcome.Timeout,
            VRChatFailureKind.Network => ConnectionOutcome.NetworkFailure,
            VRChatFailureKind.WafBlocked => ConnectionOutcome.WafBlocked,
            VRChatFailureKind.RateLimited => ConnectionOutcome.RateLimited,
            VRChatFailureKind.CredentialsRejected => ConnectionOutcome.CredentialsRejected,
            VRChatFailureKind.TwoFactorMissing => ConnectionOutcome.TwoFactorMissing,

            // A gate that learns a new classification lands here rather than silently being
            // reported as something with a different remedy.
            _ => ConnectionOutcome.Error,
        };
    }

    private static (string Headline, string Detail, string? NextStep) Explain<T>(
        ConnectionOutcome outcome, VRChatResult<T> result, string? displayName) => outcome switch
    {
        ConnectionOutcome.Ok => (
            "Reached VRChat directly.",
            displayName is { Length: > 0 }
                ? $"No proxy needed — logged in as {displayName}."
                : "No proxy needed.",
            null),

        // Spec 7.1.1, almost word for word, and deliberately so. An operator reading a 403 assumes
        // they did something wrong; on a VPS they almost certainly did not, and the first job of
        // this message is to stop them auditing their VRChat account for an afternoon.
        ConnectionOutcome.WafBlocked => (
            "Cloudflare is blocking this network.",
            "This host's IP range cannot reach the VRChat API at all — the request never got as "
            + "far as VRChat. This is common on VPS and datacentre hosting. It is not a problem "
            + "with your VRChat account, and it is not something you did.",
            "Modbot supports one egress proxy. Enter its URL below and test again — "
            + $"{SuggestedProxyProvider} is a known working option."),

        ConnectionOutcome.DnsFailure => (
            "VRChat's address could not be resolved.",
            "The hostname did not resolve, so nothing was sent. This is a DNS problem on this "
            + "host or its network, not a block and not a credential problem.",
            "Check this host's DNS resolver and that it has working outbound networking. "
            + "A proxy will not help with this."),

        ConnectionOutcome.Timeout => (
            "The connection timed out.",
            "The request was sent and nothing came back. Outbound HTTPS is usually being filtered "
            + "or silently dropped — or, if a proxy is configured, the proxy itself is not "
            + "responding.",
            "Check outbound HTTPS from this host. If a proxy is configured, test once without it "
            + "to find out which side is unresponsive."),

        ConnectionOutcome.NetworkFailure => (
            "The connection could not be established.",
            "Something answered, but the connection was refused, reset, or could not be secured. "
            + "If a proxy is configured, its address or credentials are the usual cause.",
            "Check the proxy URL and credentials if you are using one, and that this host can "
            + "open outbound HTTPS connections."),

        ConnectionOutcome.CredentialsRejected => (
            "VRChat rejected these credentials.",
            "The network is fine — the request reached VRChat and VRChat said no. Either the "
            + "username or password is wrong, or the account is locked.",
            "Check the account by signing in to vrchat.com with it, then re-enter the details. "
            + "A proxy will not help with this."),

        ConnectionOutcome.TwoFactorMissing => (
            "VRChat asked for a two-factor code.",
            "The account has two-factor authentication enabled and no TOTP secret is stored. "
            + "Modbot runs unattended and cannot be asked for a code.",
            "Add the account's TOTP secret — the base32 string the authenticator app was set up "
            + "with — and try again."),

        ConnectionOutcome.RateLimited => (
            "VRChat is rate limiting this account.",
            "Modbot stops entirely rather than retrying into a penalty that grows each time "
            + "(spec 4.3.1), so this clears by waiting and by nothing else.",
            "Wait, then test again. Do not retry in a loop — that is what lengthens it."),

        ConnectionOutcome.NotConfigured => (
            "No VRChat account is configured yet.",
            "There is nothing to test until the VRChat account step is completed.",
            "Go back and enter the VRChat account details."),

        _ => (
            "The connection attempt failed.",
            result.ErrorMessage ?? "VRChat returned an error with no explanation.",
            "If this keeps happening, check status.vrchat.com — VRChat's own outages look like "
            + "this."),
    };
}
