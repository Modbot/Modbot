using System.Text.Json.Serialization;

namespace Modbot.Core.Cloud;

/// <summary>
/// Everything this server tells Modbot Cloud about itself.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is absent matters more than what is present.</strong> There is no field for the
/// number of users, the number of group members, or the number of people in a VRChat instance, and
/// none for any member identity, moderation record, fact, profile text, credential, VRChat instance
/// id or log line. They are not filtered out on the way; there is nowhere to put them.
/// </para>
/// <para>
/// "We do not send X" is a promise somebody has to keep on every future change. "There is no field
/// for X" is checked by the compiler (central services spec 4.6, 5.2).
/// </para>
/// <para>
/// The group is the one thing a report is not anonymous about, and that is deliberate: without it
/// nobody can recognise their own server on my.modbot.co.
/// </para>
/// </remarks>
public sealed record ServerReport
{
    [JsonPropertyName("publicAddress")]
    public string? PublicAddress { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The operating system and architecture this process runs on.</summary>
    [JsonPropertyName("hostPlatform")]
    public string? HostPlatform { get; init; }

    [JsonPropertyName("groupId")]
    public string? GroupId { get; init; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; init; }

    [JsonPropertyName("groupDescription")]
    public string? GroupDescription { get; init; }

    [JsonPropertyName("groupIconUrl")]
    public string? GroupIconUrl { get; init; }

    [JsonPropertyName("groupBannerUrl")]
    public string? GroupBannerUrl { get; init; }

    [JsonPropertyName("discordConnected")]
    public bool DiscordConnected { get; init; }

    /// <summary>Which lists are imported — the ids, never their contents.</summary>
    [JsonPropertyName("termListsImported")]
    public IReadOnlyList<string>? TermListsImported { get; init; }

    /// <summary>
    /// Rate-limit cold stops since the last report.
    /// </summary>
    /// <remarks>
    /// The most valuable field here. Foundation §4.3's limiter is built on estimates about an
    /// undocumented system, and a spike across many deployments after a VRChat change is how the
    /// project discovers its numbers are wrong. No single operator can see that pattern.
    /// </remarks>
    [JsonPropertyName("rateLimitColdStops")]
    public int RateLimitColdStops { get; init; }

    [JsonPropertyName("wafBlocks")]
    public int WafBlocks { get; init; }

    [JsonPropertyName("aiModerationEnabled")]
    public bool AiModerationEnabled { get; init; }
}
