using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Companion.Ingest;
using Modbot.Shared.Clips;

namespace Modbot.Companion.Clips;

/// <summary>
/// Sends the clip for one clip request to the server that asked for it, or the word that there is
/// none.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> For a request the server made, and only while Send clips
/// for my actions is ticked: one POST of the recorded clip file as it is (the picture and sound of
/// VRChat's window from just before the action to half a minute after, cut from the companion's own
/// recording), with the device token; or, when there is no clip to send, one POST of a single word
/// (<c>clips_off</c>, <c>could_not_cut</c> or <c>upload_failed</c>). Nothing is sent ahead of a
/// request, and never to any other server than the one that asked.</para>
/// <para><strong>What stays here.</strong> The file sent is a copy made for the request and is deleted
/// from this PC once it has been sent. What the server does with it is the server's.</para>
/// <para><strong>The time limit.</strong> A clip can be up to 100 MB, so this client has no 30 second
/// limit of its own; the caller cancels it when the request closes.</para>
/// </remarks>
public sealed class HttpClipUploader : IClipUploader
{
    private readonly HttpClient _http;

    public HttpClipUploader(HttpClient http) => _http = http;

    public async Task<ClipUploadResult> UploadAsync(ServerPairing pairing, Guid requestId, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            return await SendAsync(pairing, pairing.ClipUploadEndpoint(requestId), content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ClipUploadResult.Failed;
        }
    }

    public Task<ClipUploadResult> AnswerAsync(ServerPairing pairing, Guid requestId, string outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        return SendAsync(
            pairing,
            pairing.ClipAnswerEndpoint(requestId),
            JsonContent.Create(new ClipRequestAnswer(outcome), options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ct);
    }

    private async Task<ClipUploadResult> SendAsync(ServerPairing pairing, Uri endpoint, HttpContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return ClipUploadResult.Accepted;

            return response.StatusCode is HttpStatusCode.NotFound ? ClipUploadResult.NotOpen : ClipUploadResult.Failed;
        }
        catch (HttpRequestException)
        {
            return ClipUploadResult.Failed;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ClipUploadResult.Failed;
        }
    }
}
