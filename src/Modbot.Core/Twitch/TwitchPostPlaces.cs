using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Twitch;

/// <summary>The Discord channel a "live" post goes to, and its own choices.</summary>
/// <param name="ChannelId">The channel. One per post.</param>
/// <param name="RoleId">One role mentioned on the first line, or null for none. Never @everyone.</param>
/// <param name="Publish">Publish to the channel's followers once it is in. Announcement channels only.</param>
public sealed record TwitchDiscordPlace(string? ChannelId = null, string? RoleId = null, bool Publish = false);

/// <summary>The VRChat group a "live" post goes to, and its own choices.</summary>
/// <param name="Visibility"><c>group</c> (the members) or <c>public</c> (everyone).</param>
/// <param name="RoleIds">With <c>group</c>, only these roles see it; empty is every member.</param>
/// <param name="Notify">VRChat tells the members when it goes.</param>
public sealed record TwitchVRChatPlace(
    string Visibility = VRChatPostVisibilities.Group,
    IReadOnlyList<string>? RoleIds = null,
    bool Notify = false);

/// <summary>
/// Where a "We're live on Twitch" post goes: the sites an operator ticked in Settings → Twitch, each
/// with its own choices. A site that is null is not ticked. Nothing is ticked to start. Kept as JSON
/// in <c>settings.twitch_post_places</c> (Twitch design, decision 4).
/// </summary>
/// <param name="Discord">Discord, when ticked.</param>
/// <param name="VRChat">The VRChat group's posts, when ticked.</param>
/// <param name="Bluesky">Bluesky, when ticked. It has no choices of its own.</param>
public sealed record TwitchPostPlaces(
    TwitchDiscordPlace? Discord = null,
    TwitchVRChatPlace? VRChat = null,
    bool Bluesky = false)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Whether any site is ticked.</summary>
    [JsonIgnore]
    public bool AnyTicked => Discord is not null || VRChat is not null || Bluesky;

    /// <summary>Reads the stored JSON. Anything that is not it reads as nothing ticked.</summary>
    public static TwitchPostPlaces Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TwitchPostPlaces();

        try
        {
            return JsonSerializer.Deserialize<TwitchPostPlaces>(json, Options) ?? new TwitchPostPlaces();
        }
        catch (JsonException)
        {
            return new TwitchPostPlaces();
        }
    }

    /// <summary>The JSON to store.</summary>
    public string Write() => JsonSerializer.Serialize(this, Options);
}
