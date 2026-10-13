using Newtonsoft.Json.Linq;
using VRChat.API.Model;

namespace Modbot.VRChat.GroupPage;

/// <summary>
/// The images in the managed group's galleries: one gallery's images, adding one, and removing one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every request here is one a person asked for.</strong> A gallery's images are read when
/// the Gallery tab opens, when another gallery is chosen, when a page is turned and when Refresh is
/// pressed; a removal or an addition is one press. The list of galleries is not read here at all:
/// it comes with the group poll Modbot already makes (<see cref="Sync.GroupInfoSync"/>).
/// </para>
/// <para>
/// VRChat takes a picture through its file upload first (<see cref="Files.VRChatPictureUploads"/>)
/// and a gallery entry second; <see cref="AddAsync"/> is the second half and is given the file's id.
/// </para>
/// <para>
/// Reads are on <see cref="VRChatEndpointClass.GroupsGallery"/>; removals and additions share
/// <see cref="VRChatEndpointClass.GroupsGalleryWrite"/>. A 429 is a cold stop of that class only,
/// never retried (spec 4.3.1).
/// </para>
/// </remarks>
public sealed class GroupGalleries(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>The most images one page asks VRChat for.</summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// One page of a gallery's images, approved and waiting alike, or only those waiting for approval
    /// when <paramref name="approved"/> is false. VRChat sends no total.
    /// </summary>
    /// <remarks>
    /// The SDK types this answer as a bare object, because VRChat's <c>v=2</c> wraps it in a page;
    /// <c>v</c> is left unset here, so the answer is the plain list, and it is read as one.
    /// </remarks>
    public async Task<VRChatResult<List<GroupGalleryImage>>> ListAsync(
        string groupId, string galleryId, int n, int offset, CancellationToken ct = default, bool? approved = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(galleryId);

        var size = Math.Clamp(n, 1, MaxPageSize);
        var from = Math.Max(0, offset);

        var answer = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsGallery, groupId, "GetGroupGalleryImages"),
            (client, token) => client.Groups.GetGroupGalleryImagesWithHttpInfoAsync(
                groupId, galleryId, size, from, approved: approved, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct).ConfigureAwait(false);

        if (!answer.Success)
            return VRChatResult<List<GroupGalleryImage>>.From(answer);

        return VRChatResult<List<GroupGalleryImage>>.Ok(ImagesOf(answer.Value, answer.RawResponse), answer.StatusCode, answer.RawResponse);
    }

    /// <summary>
    /// Adds a picture already uploaded to VRChat (<paramref name="fileId"/>, the <c>file_...</c> id
    /// the upload answered with) to a gallery. One request, never retried.
    /// </summary>
    public Task<VRChatResult<GroupGalleryImage>> AddAsync(
        string groupId, string galleryId, string fileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(galleryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsGalleryWrite, groupId, "AddGroupGalleryImage"),
            (client, token) => client.Groups.AddGroupGalleryImageWithHttpInfoAsync(
                groupId, galleryId, new AddGroupGalleryImageRequest(fileId), cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>Removes one image from a gallery.</summary>
    public Task<VRChatResult<Success>> RemoveAsync(
        string groupId, string galleryId, string imageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(galleryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsGalleryWrite, groupId, "DeleteGroupGalleryImage"),
            (client, token) => client.Groups.DeleteGroupGalleryImageWithHttpInfoAsync(
                groupId, galleryId, imageId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>
    /// The images in VRChat's answer: the plain list, or the list inside a page should VRChat send
    /// one anyway. Anything else is no images rather than an error, since the answer was a 200.
    /// </summary>
    internal static List<GroupGalleryImage> ImagesOf(object? value, string? raw)
    {
        JToken? token = value as JToken;

        if (token is null && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                token = JToken.Parse(raw);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return [];
            }
        }

        if (token is JObject page)
            token = page["images"] ?? page["results"] ?? page["data"];

        if (token is not JArray list)
            return [];

        var images = new List<GroupGalleryImage>();

        foreach (var item in list.OfType<JObject>())
        {
            try
            {
                if (item.ToObject<GroupGalleryImage>() is { } image && !string.IsNullOrWhiteSpace(image.Id))
                    images.Add(image);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // One entry VRChat wrote in a shape the SDK cannot read is left out, not the page.
            }
        }

        return images;
    }
}
