using System.Text.Json;
using System.Text.Json.Serialization;
using VRChat.API.Model;

namespace Modbot.VRChat.Sync;

/// <summary>One of the group's galleries, as the group poll last found it.</summary>
/// <param name="Id">VRChat's id for the gallery. Opaque (spec 3.1.1).</param>
/// <param name="MembersOnly">Whether only the group's members can see it.</param>
public sealed record GroupGallerySnapshot(string Id, string? Name, string? Description, bool MembersOnly)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The galleries in VRChat's answer, in its order, less any without an id.</summary>
    public static IReadOnlyList<GroupGallerySnapshot> From(IEnumerable<GroupGallery> galleries)
    {
        ArgumentNullException.ThrowIfNull(galleries);

        return galleries
            .Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id))
            .Select(g => new GroupGallerySnapshot(
                g.Id,
                string.IsNullOrWhiteSpace(g.Name) ? null : g.Name,
                string.IsNullOrWhiteSpace(g.Description) ? null : g.Description,
                g.MembersOnly))
            .ToList();
    }

    public static string ToJson(IReadOnlyList<GroupGallerySnapshot> galleries) => JsonSerializer.Serialize(galleries, Json);

    /// <summary>The stored galleries, or none when nothing is stored or it cannot be read.</summary>
    public static IReadOnlyList<GroupGallerySnapshot> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<GroupGallerySnapshot>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
