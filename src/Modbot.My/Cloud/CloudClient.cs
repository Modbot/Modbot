using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.My.Configuration;

namespace Modbot.My.Cloud;

/// <param name="GroupName">The group, when a registered Modbot reports this address.</param>
public sealed record KnownServer(
    [property: JsonPropertyName("serverUrl")] string ServerUrl,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("visits")] int Visits,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl)
{
    /// <summary>
    /// The same address under the name it had until 2026-09-26, when "instance" became "server".
    /// A browser still running a page built before then reads only this field.
    /// </summary>
    /// <remarks>
    /// Compatibility, added 2026-09-26. Remove once no browser can still be running a page built
    /// before that date.
    /// </remarks>
    [JsonPropertyName("instanceUrl")]
    public string InstanceUrl => ServerUrl;
}

public sealed record KnownServers(
    [property: JsonPropertyName("items")] IReadOnlyList<KnownServer> Items);

/// <summary>One entry of Cloud's <c>GET /api/v1/site/visits</c>, as Cloud sends it.</summary>
/// <param name="InstanceUrl">
/// Compatibility, added 2026-09-26: Cloud named the address <c>instanceUrl</c> until then, and Cloud
/// and my.modbot.co are deployed separately, so either may go first. Remove once every Cloud
/// deployment sends <c>serverUrl</c>.
/// </param>
internal sealed record CloudVisit(
    [property: JsonPropertyName("serverUrl")] string? ServerUrl,
    [property: JsonPropertyName("instanceUrl")] string? InstanceUrl,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("visits")] int Visits,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl);

internal sealed record CloudVisits(
    [property: JsonPropertyName("items")] IReadOnlyList<CloudVisit>? Items);

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
/// unreachable means a page showing the servers the browser saved for itself, which is what the
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
    /// Passes on what a Modbot address said about itself when my.modbot.co asked it. Returns whether
    /// Cloud took it.
    /// </summary>
    /// <remarks>
    /// Only ever what the address answered. The group details in a register link are hints for the
    /// page and never reach this call (register details spec 2.2).
    /// </remarks>
    public async Task<bool> RecordServerAsync(string url, Features.Visits.ServerDetails details, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);

        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/site/servers",
            new
            {
                url,
                details.GroupId,
                details.GroupName,
                details.GroupIconUrl,
                details.GroupBannerUrl,
                details.OwnerEmail,
            },
            ct);

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
    /// The Modbot servers Cloud has seen from one address. Null when Cloud could not be asked or did
    /// not answer properly; an empty list only when Cloud said there are none.
    /// </summary>
    public async Task<KnownServers?> KnownServersAsync(string? address, CancellationToken ct)
    {
        if (address is null)
            return new KnownServers([]);

        using var response = await SendAsync(
            HttpMethod.Get, $"api/v1/site/visits?address={Uri.EscapeDataString(address)}", null, ct);

        if (response is null || !Ok(response))
            return null;

        CloudVisits? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<CloudVisits>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            answer = null;
        }

        if (answer?.Items is null)
        {
            log.LogWarning("Modbot Cloud answered with something this page could not read.");
            return null;
        }

        var servers = new List<KnownServer>(answer.Items.Count);
        foreach (var visit in answer.Items)
        {
            // serverUrl first, and instanceUrl from a Cloud older than 2026-09-26 (see CloudVisit).
            var url = visit.ServerUrl ?? visit.InstanceUrl;
            if (url is null)
                continue;

            servers.Add(new KnownServer(
                url, visit.FirstSeenAt, visit.LastSeenAt, visit.Visits, visit.GroupName, visit.GroupIconUrl));
        }

        return new KnownServers(servers);
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
