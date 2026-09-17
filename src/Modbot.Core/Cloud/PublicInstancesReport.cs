using System.Text.Json.Serialization;

namespace Modbot.Core.Cloud;

/// <summary>
/// What this server tells Modbot Cloud about the instances its group has open to everyone.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole payload. There is no field for a head count, a member count, a person's name
/// or a person's id, so the page on modbot.co cannot show any of them and Cloud cannot store any of
/// them — the group, the world, and a link anybody could have been given in Discord.
/// </para>
/// <para>
/// Every report is the complete list of open public instances. An instance that has closed is simply not in
/// the next one, and Cloud drops what is not reported, so there is no "closed" message to lose.
/// </para>
/// </remarks>
/// <param name="GroupId">The VRChat group id, so the page can link to the group.</param>
/// <param name="GroupName">The group's name, as VRChat gives it.</param>
/// <param name="GroupIconUrl">The group's icon, or null when VRChat has not given one.</param>
/// <param name="GroupBannerUrl">The group's banner, or null when VRChat has not given one.</param>
/// <param name="Instances">Every instance of the group's that anyone can join, right now.</param>
public sealed record PublicInstancesReport(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl,
    [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl,
    [property: JsonPropertyName("instances")] IReadOnlyList<PublicInstanceReport> Instances);

/// <param name="Location">
/// VRChat's own location string. The identity of the instance, so a second report about the same instance
/// updates it rather than adding another.
/// </param>
/// <param name="WorldId">The world the instance is in.</param>
/// <param name="WorldName">What the world is called, or null when Modbot has only ever seen its id.</param>
/// <param name="WorldImageUrl">The world's picture, or null.</param>
/// <param name="JoinLink">VRChat's launch page for the instance, or null when one could not be built.</param>
/// <param name="Region">VRChat's region for the instance — <c>us</c>, <c>eu</c>, <c>jp</c> — or null.</param>
/// <param name="OpenedAt">When Modbot first knew the instance was open.</param>
public sealed record PublicInstanceReport(
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("worldId")] string WorldId,
    [property: JsonPropertyName("worldName")] string? WorldName,
    [property: JsonPropertyName("worldImageUrl")] string? WorldImageUrl,
    [property: JsonPropertyName("joinLink")] string? JoinLink,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("openedAt")] DateTimeOffset OpenedAt);
