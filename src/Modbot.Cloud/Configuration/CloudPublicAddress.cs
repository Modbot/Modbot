namespace Modbot.Cloud.Configuration;

/// <summary>
/// Where Cloud is reachable from outside, from <c>CLOUD_PUBLIC_URL</c>.
/// </summary>
/// <remarks>
/// Needed wherever Cloud has to write one of its own addresses into an answer rather than a link in
/// a page — the showcase pictures it serves, which are read by a desktop client and by servers that
/// have no idea what host the request arrived on.
/// </remarks>
public sealed record CloudPublicAddress(Uri Address);
