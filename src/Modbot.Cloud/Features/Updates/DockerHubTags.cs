using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Cloud.Features.Updates;

/// <param name="Name">The tag, such as <c>2026.9.1</c> or <c>latest</c>.</param>
/// <param name="PushedAt">When the image behind the tag was last pushed, when Docker Hub says.</param>
public sealed record ImageTag(string Name, DateTimeOffset? PushedAt);

/// <summary>
/// The tags the server image has on Docker Hub.
/// </summary>
/// <remarks>
/// <para>
/// GitHub says which version was released; this says whether an image an operator can actually
/// pull is there under that version, and when it was pushed. The two are published by different
/// workflows and can be minutes apart, so Cloud asks both rather than assuming one from the other.
/// </para>
/// <para>
/// Read once per refresh, like the releases, and a failure is null. When Docker Hub cannot be read,
/// the answer still names the image and the release version as its tag — which is what an operator
/// would type anyway — and simply does not say when it was pushed.
/// </para>
/// <para>
/// Unauthenticated: the tag list of a public repository is public, and Docker Hub's anonymous limit
/// is counted in pulls rather than in reads of this endpoint. One read per refresh is nothing
/// against either.
/// </para>
/// </remarks>
public sealed class DockerHubTags(HttpClient client, string image)
{
    public const string HttpClientName = "docker-hub";

    /// <summary>The image read when <c>DOCKER_IMAGE</c> is not set.</summary>
    public const string DefaultImage = "modbot/modbot";

    /// <summary>How many tags are read. Newest first, so this is a window on the recent ones.</summary>
    public const int MostTags = 50;

    public string Image { get; } = string.IsNullOrWhiteSpace(image) ? DefaultImage : image.Trim();

    /// <summary>
    /// The newest tags, or null when Docker Hub could not be read.
    /// </summary>
    public async Task<IReadOnlyList<ImageTag>?> ListAsync(CancellationToken ct = default)
    {
        // An official image is "library/<name>" to the API even though nobody writes it that way.
        var path = Image.Contains('/', StringComparison.Ordinal) ? Image : $"library/{Image}";
        var url = $"https://hub.docker.com/v2/repositories/{path}/tags?page_size={MostTags}&ordering=last_updated";

        try
        {
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var page = await response.Content.ReadFromJsonAsync<TagPage>(ct).ConfigureAwait(false);

            if (page?.Results is null)
                return null;

            return
            [
                .. page.Results
                    .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                    .Select(t => new ImageTag(t.Name!, t.LastUpdated)),
            ];
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                  && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record TagPage(List<TagRow>? Results);

    private sealed record TagRow(
        string? Name,
        [property: JsonPropertyName("last_updated")] DateTimeOffset? LastUpdated);
}
