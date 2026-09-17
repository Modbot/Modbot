using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.My.Configuration;

namespace Modbot.My.Cloud;

/// <param name="GroupName">The group, when a registered Modbot reports this address.</param>
public sealed record KnownInstance(
    [property: JsonPropertyName("instanceUrl")] string InstanceUrl,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("visits")] int Visits,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl);

public sealed record KnownInstances(
    [property: JsonPropertyName("items")] IReadOnlyList<KnownInstance> Items);

/// <summary>
/// Everything my.modbot.co reads from and writes to Modbot Cloud.
/// </summary>
/// <remarks>
/// <para>
/// my.modbot.co has no database (central services spec 2.1.1). This is the whole of its storage: two
/// calls to Cloud's <c>/api/v1/site</c> endpoints, with <c>MODBOT_CLOUD_API_KEY</c> as the bearer.
/// </para>
/// <para>
/// <strong>The key never leaves the server.</strong> It is not returned by any endpoint, not written
/// to a log line, and not rendered into the page. A failure logs the status code and nothing else.
/// </para>
/// <para>
/// Every call fails quietly here, and says so to its caller: a save answers false and a read
/// answers null when Cloud did not take part. The endpoints turn that into a 503, so the browser
/// keeps what it has and tries again, rather than being told an empty list is the truth or a save
/// went through when it did not. The page itself is never refused: a Cloud that is slow or
/// unreachable means a page showing the instances the browser saved for itself, which is what the
/// page did before any of this existed.
/// </para>
/// </remarks>
public sealed class CloudClient(CloudAddress cloud, IHttpClientFactory factory, ILogger<CloudClient> log)
{
    public const string HttpClientName = "Modbot.Cloud";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>False when no key is set, so nothing is even attempted.</summary>
    public bool IsConfigured => cloud.ApiKey is not null;

    /// <summary>Notes that a Modbot address was opened. Returns whether Cloud took it.</summary>
    public async Task<bool> RecordVisitAsync(string? address, string url, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/site/visits", new { address, url }, ct);
        return response is not null && Ok(response);
    }

    /// <summary>
    /// Notes a visit without making the caller wait for Cloud. For the page routes: a Cloud that
    /// hangs must not hold the page back, and the app sends the same address again once it has
    /// rendered, so nothing is lost if this one never lands.
    /// </summary>
    public void RecordVisitInBackground(string? address, string url)
    {
        // Not the request's token: the response is sent long before Cloud answers, and cancelling
        // then would throw the note away every time.
        _ = Task.Run(async () =>
        {
            try
            {
                await RecordVisitAsync(address, url, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not note a visit with Modbot Cloud.");
            }
        });
    }

    /// <summary>
    /// The instances Cloud has seen from one address. Null when Cloud could not be asked or did not
    /// answer properly; an empty list only when Cloud said there are none.
    /// </summary>
    public async Task<KnownInstances?> KnownInstancesAsync(string? address, CancellationToken ct)
    {
        if (address is null)
            return new KnownInstances([]);

        using var response = await SendAsync(
            HttpMethod.Get, $"api/v1/site/visits?address={Uri.EscapeDataString(address)}", null, ct);

        if (response is null || !Ok(response))
            return null;

        try
        {
            return await response.Content.ReadFromJsonAsync<KnownInstances>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            log.LogWarning("Modbot Cloud answered with something this page could not read.");
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (cloud.ApiKey is null)
            return null;

        try
        {
            using var client = factory.CreateClient(HttpClientName);
            client.BaseAddress = cloud.Endpoint;
            client.Timeout = Timeout;

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cloud.ApiKey);

            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);

            return await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Could not reach Modbot Cloud.");
            return null;
        }
    }

    private bool Ok(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return true;

        // The status only. A body from Cloud can repeat back what was sent.
        log.LogWarning("Modbot Cloud answered {Status}.", (int)response.StatusCode);
        return false;
    }
}
