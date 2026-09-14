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

/// <summary>One registered deployment, as the root API key holder reads it.</summary>
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
    int? WafBlocks)
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
        i.WafBlocks);
}
