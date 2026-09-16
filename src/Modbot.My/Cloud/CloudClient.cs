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
/// Every call fails quietly. A Cloud that is slow or unreachable means a page showing the instances
/// the browser saved for itself, which is what the page did before any of this existed — never an
/// error in front of somebody trying to open their own Modbot.
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

    /// <summary>The instances Cloud has seen from one address. Empty when Cloud cannot be reached.</summary>
    public async Task<KnownInstances> KnownInstancesAsync(string? address, CancellationToken ct)
    {
        if (address is null)
            return new KnownInstances([]);

        using var response = await SendAsync(
            HttpMethod.Get, $"api/v1/site/visits?address={Uri.EscapeDataString(address)}", null, ct);

        if (response is null || !Ok(response))
            return new KnownInstances([]);

        try
        {
            return await response.Content.ReadFromJsonAsync<KnownInstances>(Json, ct).ConfigureAwait(false)
                   ?? new KnownInstances([]);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            log.LogWarning("Modbot Cloud answered with something this page could not read.");
            return new KnownInstances([]);
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
