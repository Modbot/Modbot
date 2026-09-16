using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Core.Configuration;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Credits;

/// <param name="Login">Their GitHub username.</param>
/// <param name="Url">Their GitHub profile.</param>
/// <param name="AvatarUrl">Their picture.</param>
/// <param name="Contributions">Commits GitHub counted.</param>
public sealed record Contributor(string Login, string Url, string AvatarUrl, int Contributions);

/// <param name="Name">What to call them.</param>
/// <param name="Link">Where clicking the name goes. Empty when there is nowhere.</param>
/// <param name="ImageUrl">Their picture. Empty when there is none.</param>
/// <param name="VRChatGroupId">Their VRChat group, when they have one.</param>
/// <param name="GroupImageUrl">The group's icon.</param>
/// <param name="GroupBannerUrl">The group's banner.</param>
public sealed record ShowcasePerson(
    string Name,
    string Link,
    string ImageUrl,
    string? VRChatGroupId,
    string? GroupImageUrl,
    string? GroupBannerUrl);

/// <param name="Available">False when Modbot could not reach Modbot Cloud, or is not allowed to.</param>
public sealed record Showcase(
    bool Available,
    IReadOnlyList<Contributor> Contributors,
    IReadOnlyList<ShowcasePerson> Sponsors,
    IReadOnlyList<ShowcasePerson> EarlyAdopters);

/// <summary>
/// The people the project wants to thank, read from Modbot Cloud for the Credits page.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read, cached, and quietly absent.</strong> Cloud being slow or unreachable costs a
/// section of one page and nothing else: the answer says <c>available: false</c> and the page draws
/// nothing. Nothing about Modbot behaves differently for it.
/// </para>
/// <para>
/// <strong>Honours <c>MODBOT_CLOUD_DISABLED</c></strong>, like every other thing a server asks Cloud
/// for (central services spec 1.1). A deployment that has turned Cloud off shows no showcase, which
/// is the right answer rather than a missing one.
/// </para>
/// <para>
/// Cached for six hours in this process. The list changes when somebody types a row into Cloud
/// admin, which is a few times a year; asking on every page load would be one request per moderator
/// per visit for a list that never moves.
/// </para>
/// </remarks>
public sealed class CloudShowcase
{
    public const string HttpClientName = "modbot-cloud-showcase";

    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>How long a failure is remembered, so a Cloud that is down is not asked on every load.</summary>
    public static readonly TimeSpan CacheFailureFor = TimeSpan.FromMinutes(10);

    private static readonly Showcase Nothing = new(false, [], [], []);

    private readonly IHttpClientFactory _http;
    private readonly ModbotCloudAddress _cloud;
    private readonly IModbotClock _clock;
    private readonly SemaphoreSlim _one = new(1, 1);

    private Showcase _cached = Nothing;
    private DateTimeOffset _staleAt = DateTimeOffset.MinValue;

    public CloudShowcase(IHttpClientFactory http, ModbotCloudAddress cloud, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(clock);

        _http = http;
        _cloud = cloud;
        _clock = clock;
    }

    public async Task<Showcase> ReadAsync(CancellationToken ct = default)
    {
        if (_cloud.Disabled)
            return Nothing;

        if (_clock.UtcNow < _staleAt)
            return _cached;

        await _one.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var now = _clock.UtcNow;

            // Somebody else may have refreshed it while this call waited.
            if (now < _staleAt)
                return _cached;

            var fetched = await FetchAsync(ct).ConfigureAwait(false);

            _cached = fetched ?? Nothing;
            _staleAt = now + (fetched is null ? CacheFailureFor : CacheFor);

            return _cached;
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<Showcase?> FetchAsync(CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);

        try
        {
            var contributors = await GetAsync<Contributor>(client, "/api/v1/contributors", ct).ConfigureAwait(false);
            var sponsors = await GetAsync<ShowcasePerson>(client, "/api/v1/sponsors", ct).ConfigureAwait(false);
            var early = await GetAsync<ShowcasePerson>(client, "/api/v1/early-adopters", ct).ConfigureAwait(false);

            if (contributors is null || sponsors is null || early is null)
                return null;

            return new Showcase(true, contributors, sponsors, early);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                  && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<List<T>?> GetAsync<T>(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri(_cloud.Endpoint, path), ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadFromJsonAsync<Page<T>>(ct).ConfigureAwait(false);

        return body?.Items;
    }

    private sealed record Page<T>(List<T>? Items);
}
