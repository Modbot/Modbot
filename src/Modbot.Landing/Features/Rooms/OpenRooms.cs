using System.Text.Json;
using Modbot.Landing.Configuration;

namespace Modbot.Landing.Features.Rooms;

/// <summary>
/// The groups using Modbot and the rooms they have open, read from Modbot Cloud and held for a
/// short while.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key never leaves this server.</strong> The browser asks this site for
/// <c>/api/rooms</c>; this site asks Cloud with <c>MODBOT_CLOUD_API_KEY</c> and hands back only
/// what came out. There is no other way the page could get the list without putting the key in
/// front of everyone who opens it.
/// </para>
/// <para>
/// One read is shared by every visitor for <see cref="Freshness"/>. A group's rooms change on the
/// scale of an evening, and a page that is a minute behind is not wrong in any way a visitor would
/// notice — while a read per visitor would make Cloud's load the landing page's popularity.
/// </para>
/// <para>
/// When Cloud cannot be reached the last good answer keeps being served up to
/// <see cref="KeepStaleFor"/>, because an empty page during a thirty-second blip is worse than a
/// page that is a few minutes old. After that the page says nothing is open, which is what this
/// site actually knows.
/// </para>
/// </remarks>
public sealed class OpenRooms(LandingEnvironment environment, IHttpClientFactory clients, ILogger<OpenRooms> log)
{
    /// <summary>How long one read is served to everyone.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

    /// <summary>How long the last good answer is served after Cloud stops answering.</summary>
    public static readonly TimeSpan KeepStaleFor = TimeSpan.FromMinutes(15);

    /// <summary>The name of the <see cref="HttpClient"/> the read is made on.</summary>
    public const string HttpClientName = "modbot-cloud";

    /// <summary>Cloud's feed, under the Cloud address.</summary>
    public const string CloudPath = "/api/v1/public-rooms";

    /// <summary>What the browser is given when nothing is known.</summary>
    public const string Empty = """{"groups":[]}""";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string _json = Empty;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private DateTimeOffset _goodAt = DateTimeOffset.MinValue;

    /// <summary>The feed as JSON, read again when what is held has gone stale.</summary>
    public async Task<string> ReadAsync(TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(time);

        if (!environment.CanReadRooms)
            return Empty;

        var now = time.GetUtcNow();

        if (now - _readAt < Freshness)
            return _json;

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Checked again inside the gate: while a visitor waited, another visitor's read
            // finished, and asking Cloud twice for the same second would be the thing this exists
            // to avoid.
            now = time.GetUtcNow();

            if (now - _readAt < Freshness)
                return _json;

            var fetched = await FetchAsync(ct).ConfigureAwait(false);

            // A failed read still counts as an attempt, so a Cloud that is down is asked once a
            // minute rather than once a visitor.
            _readAt = now;

            if (fetched is not null)
            {
                _json = fetched;
                _goodAt = now;
            }
            else if (now - _goodAt > KeepStaleFor)
            {
                _json = Empty;
            }

            return _json;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FetchAsync(CancellationToken ct)
    {
        try
        {
            var client = clients.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(environment.CloudUrl!, CloudPath));
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {environment.CloudApiKey}");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                log.LogInformation(
                    "Modbot Cloud did not give the open rooms: {Status}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Parsed to be sure it is JSON and to put a ceiling on it. What is written back is
            // Cloud's own bytes, so this page stays right when Cloud grows a field.
            using var parsed = JsonDocument.Parse(body);

            return parsed.RootElement.ValueKind == JsonValueKind.Object ? body : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogInformation("Could not read the open rooms from Modbot Cloud: {Reason}", ex.Message);
            return null;
        }
    }
}
