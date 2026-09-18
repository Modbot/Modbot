using System.Text.Json.Serialization;

namespace Modbot.Cloud.Features.Registry;

/// <param name="PublicAddress">The server's own address, as its operator entered it.</param>
/// <param name="Version">The release it is running.</param>
/// <param name="HostPlatform">The operating system and architecture it runs on.</param>
public sealed record RegisterServerRequest(
    [property: JsonPropertyName("publicAddress")] string? PublicAddress,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("hostPlatform")] string? HostPlatform);

/// <param name="Secret">Shown once, here. Cloud keeps only its hash.</param>
public sealed record RegisterServerResponse(
    [property: JsonPropertyName("serverId")] Guid ServerId,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime);

/// <summary>
/// Everything a Modbot server reports. <strong>There is deliberately no field for the number of
/// users, the number of group members, or the number of people in a VRChat instance.</strong>
/// </summary>
/// <remarks>
/// "We do not send X" is a promise somebody has to keep on every future change. "There is no field
/// for X" is checked by the compiler.
/// </remarks>
public sealed record ServerReportRequest(
    [property: JsonPropertyName("publicAddress")] string? PublicAddress,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("hostPlatform")] string? HostPlatform,
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupDescription")] string? GroupDescription,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl,
    [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl,
    [property: JsonPropertyName("discordConnected")] bool? DiscordConnected,
    [property: JsonPropertyName("termListsImported")] IReadOnlyList<string>? TermListsImported,
    [property: JsonPropertyName("rateLimitColdStops")] int? RateLimitColdStops,
    [property: JsonPropertyName("wafBlocks")] int? WafBlocks,
    [property: JsonPropertyName("aiModerationEnabled")] bool? AiModerationEnabled);

/// <param name="CodeHash">
/// Lower-case hex SHA-256 of the code the server is showing its owner. The code itself never leaves
/// the Modbot server.
/// </param>
public sealed record LinkCodeRequest([property: JsonPropertyName("codeHash")] string? CodeHash);

public sealed record ClaimServerRequest([property: JsonPropertyName("code")] string? Code);

/// <summary>A registered server as its owner reads it.</summary>
public sealed record ServerView(
    [property: JsonPropertyName("serverId")] Guid ServerId,
    [property: JsonPropertyName("publicAddress")] string? PublicAddress,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("hostPlatform")] string? HostPlatform,
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupDescription")] string? GroupDescription,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl,
    [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl,
    [property: JsonPropertyName("discordConnected")] bool? DiscordConnected,
    [property: JsonPropertyName("termListsImported")] IReadOnlyList<string>? TermListsImported,
    [property: JsonPropertyName("rateLimitColdStops")] int? RateLimitColdStops,
    [property: JsonPropertyName("wafBlocks")] int? WafBlocks,
    [property: JsonPropertyName("aiModerationEnabled")] bool? AiModerationEnabled,
    [property: JsonPropertyName("registeredAt")] DateTimeOffset RegisteredAt,
    [property: JsonPropertyName("lastReportAt")] DateTimeOffset? LastReportAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("claimedAt")] DateTimeOffset? ClaimedAt)
{
    public static ServerView From(RegisteredServer s)
    {
        ArgumentNullException.ThrowIfNull(s);

        return new ServerView(
            s.Id,
            s.PublicAddress,
            s.Version,
            s.HostPlatform,
            s.GroupId,
            s.GroupName,
            s.GroupDescription,
            s.GroupIconUrl,
            s.GroupBannerUrl,
            s.DiscordConnected,
            s.TermListsImported,
            s.RateLimitColdStops,
            s.WafBlocks,
            s.AiModerationEnabled,
            s.RegisteredAt,
            s.LastReportAt,
            s.LastSeenAt,
            s.ClaimedAt);
    }
}

/// <summary>A registered server as an admin reads it: the same, plus who holds it and where from.</summary>
/// <param name="OwnerEmail">
/// Who runs it, when a my.modbot.co register visit learned an address for this server's address.
/// <strong>Admin only</strong>, and never part of <see cref="ServerView"/>, which an account reads
/// (register details spec 3.4).
/// </param>
public sealed record AdminServerView(
    [property: JsonPropertyName("server")] ServerView Server,
    [property: JsonPropertyName("claimedBy")] string? ClaimedBy,
    [property: JsonPropertyName("ipAddress")] string? IpAddress,
    [property: JsonPropertyName("ownerEmail")] string? OwnerEmail);

public sealed record ServerReportView(
    [property: JsonPropertyName("reportedAt")] DateTimeOffset ReportedAt,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("hostPlatform")] string? HostPlatform,
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("discordConnected")] bool? DiscordConnected,
    [property: JsonPropertyName("termListsImported")] IReadOnlyList<string>? TermListsImported,
    [property: JsonPropertyName("rateLimitColdStops")] int? RateLimitColdStops,
    [property: JsonPropertyName("wafBlocks")] int? WafBlocks,
    [property: JsonPropertyName("aiModerationEnabled")] bool? AiModerationEnabled,
    [property: JsonPropertyName("ipAddress")] string? IpAddress);
