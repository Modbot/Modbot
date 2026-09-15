namespace Modbot.My.Features.Instances;

/// <param name="InstanceId">Random id the deployment assigned itself on first boot.</param>
/// <param name="InstanceUrl">An absolute https URL. Only its origin is kept.</param>
/// <param name="Version">The release the deployment is running.</param>
public sealed record RegisterRequest(string? InstanceId, string? InstanceUrl, string? Version);

public sealed record RegisterResponse(bool Registered, string InstanceId, DateTimeOffset RegisteredAt);

/// <param name="Version">The release the deployment is running.</param>
/// <param name="ScaleBucket">Bucketed member count, never an exact figure.</param>
public sealed record UsageReport(
    string? Version,
    string? ScaleBucket,
    int? PairedClients,
    bool? DiscordConnected,
    IReadOnlyList<string>? TermListsImported,
    int? RateLimitColdStops,
    int? WafBlocks);

/// <summary>One registered deployment, as an admin reads it.</summary>
/// <param name="IpAddress">The address its last register or usage call came from.</param>
public sealed record InstanceView(
    string InstanceId,
    string InstanceUrl,
    string? Version,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt,
    bool AnalyticsEnabled,
    DateTimeOffset? LastUsageReportAt,
    string? ScaleBucket,
    int? PairedClients,
    bool? DiscordConnected,
    IReadOnlyList<string>? TermListsImported,
    int? RateLimitColdStops,
    int? WafBlocks,
    string? IpAddress)
{
    internal static InstanceView From(RegisteredInstance i) => new(
        i.InstanceId,
        i.InstanceUrl,
        i.Version,
        i.RegisteredAt,
        i.LastSeenAt,
        i.AnalyticsEnabled,
        i.LastUsageReportAt,
        i.ScaleBucket,
        i.PairedClients,
        i.DiscordConnected,
        i.TermListsImported,
        i.RateLimitColdStops,
        i.WafBlocks,
        i.IpAddress);
}

/// <param name="Requests">Register and usage calls from this address.</param>
public sealed record InstanceIpView(string IpAddress, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, int Requests);

public sealed record InstanceIpHistory(IReadOnlyList<InstanceIpView> Items);
