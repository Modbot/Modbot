namespace Modbot.Api.Features.Onboarding.TestConnection;

/// <summary>
/// What to test with. An empty body tests the current configuration unchanged.
/// </summary>
/// <param name="UseProxy">
/// Whether the egress proxy should be used at all. Explicit rather than inferred from
/// <paramref name="ProxyUrl"/> being empty, so that "test without the proxy to find out which
/// side is broken" is a thing the operator can actually ask for — and so that clearing a proxy is
/// distinguishable from not mentioning one.
/// </param>
/// <param name="ProxyUrl">
/// The one optional egress proxy (spec 2.3.1). One, statically configured, never rotated: that is
/// the difference between reaching an API that blocks your IP range and evading a rate limit.
/// </param>
/// <param name="ProxyPassword">
/// Encrypted at rest and never returned. Null leaves any stored password in place, so an operator
/// re-testing after fixing the URL does not have to retype a password they cannot read back.
/// </param>
public sealed record TestConnectionRequest(
    bool? UseProxy = null,
    string? ProxyUrl = null,
    string? ProxyUsername = null,
    string? ProxyPassword = null);
