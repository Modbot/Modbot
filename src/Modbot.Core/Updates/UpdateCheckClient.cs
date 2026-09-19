using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Core.Configuration;

namespace Modbot.Core.Updates;

/// <param name="Version">The newest release, <c>YYYY.M.PATCH</c>.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="NotesUrl">The page with its notes on it.</param>
/// <param name="Image">The image to pull, such as <c>modbot/modbot</c>.</param>
/// <param name="Tag">The tag to pull it with.</param>
public sealed record NewestRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("notesUrl")] string? NotesUrl,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("tag")] string? Tag);

/// <summary>
/// Asks what the newest Modbot server release is.
/// </summary>
/// <remarks>
/// <para>
/// One GET, nothing sent: no id, no version, no group, no account, not even a header that says
/// which deployment is asking. There is nothing to send, because the answer is the same for
/// everybody — which is also what lets the other end serve it all from one cached copy.
/// </para>
/// <para>
/// <strong>Not through the VRChat gate and not through the egress proxy.</strong> Same rule as
/// <c>CloudServerClient</c>: this is not VRChat, so it must not spend a VRChat rate-limit budget or
/// leave by an address set aside for VRChat.
/// </para>
/// </remarks>
public sealed class UpdateCheckClient(ModbotUpdateAddress address, IHttpClientFactory factory)
{
    public const string HttpClientName = "Modbot.Updates";

    /// <summary>Which of the things Modbot ships this server is asking about.</summary>
    public const string ServerPath = "api/v1/updates/server";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The newest release, or null with one short sentence saying why not.
    /// </summary>
    public async Task<(NewestRelease? Release, string? Problem)> ReadAsync(CancellationToken ct)
    {
        try
        {
            using var client = factory.CreateClient(HttpClientName);

            client.BaseAddress = address.Endpoint.AbsoluteUri.EndsWith('/')
                ? address.Endpoint
                : new Uri(address.Endpoint.AbsoluteUri + "/");
            client.Timeout = Timeout;

            using var response = await client.GetAsync(ServerPath, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, $"{address.Endpoint.Host} answered {(int)response.StatusCode}.");

            var release = await response.Content.ReadFromJsonAsync<NewestRelease>(Json, ct).ConfigureAwait(false);

            return release is { Version.Length: > 0 }
                ? (release, null)
                : (null, $"{address.Endpoint.Host} did not name a version.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                  && !ct.IsCancellationRequested)
        {
            return (null, $"Could not reach {address.Endpoint.Host}.");
        }
    }
}
