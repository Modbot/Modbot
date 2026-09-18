using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.My.Cloud;

namespace Modbot.My.Features.Visits;

/// <summary>What a Modbot server says about itself, once it has been asked and its answer cleaned.</summary>
public sealed record ServerDetails(
    string? GroupId,
    string? GroupName,
    string? GroupIconUrl,
    string? GroupBannerUrl,
    string? OwnerEmail)
{
    /// <summary>True when the server answered but said nothing worth saving.</summary>
    public bool IsEmpty =>
        GroupId is null && GroupName is null && GroupIconUrl is null && GroupBannerUrl is null && OwnerEmail is null;
}

/// <summary>
/// Asks a Modbot address what group it moderates, and tells Modbot Cloud the answer.
/// </summary>
/// <remarks>
/// <para>
/// A register link carries the group's name, icon and banner so that the page has something to show
/// the moment it opens. <strong>Those are hints and nothing more</strong>: anybody can write a link.
/// Before anything is saved, my.modbot.co asks the address itself — <c>GET &lt;url&gt;/api/server</c>,
/// which every Modbot answers without a key — and saves that, or saves nothing about the group at
/// all (register details spec 2).
/// </para>
/// <para>
/// The link's own parameters never leave the browser. They are not read here, not sent to Cloud and
/// not stored, so a forged link can mislead exactly one page for a moment and nothing else.
/// </para>
/// <para>
/// Asking costs a request to somewhere a stranger named, so: <see cref="Timeout"/> seconds at most,
/// no redirect followed, at most <see cref="MaxBytes"/> read, and only to addresses out on the
/// public internet (<see cref="Common.PublicAddresses"/>). An answer is remembered for a few
/// minutes, so the two saves of one page view ask once.
/// </para>
/// </remarks>
public sealed class ServerLookup(
    CloudClient cloud, IHttpClientFactory factory, TimeProvider time, ILogger<ServerLookup> log)
{
    public const string HttpClientName = "Modbot.Server";

    /// <summary>Short: a page view waits for this, and a server that is slow has already failed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>More than the few hundred bytes a real answer is, and small enough to be harmless.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>How long an answer stands before the address is asked again.</summary>
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);

    /// <summary>How long a failure stands. Shorter, so a Modbot that was down is asked again soon.</summary>
    public static readonly TimeSpan RememberFailure = TimeSpan.FromMinutes(1);

    private const int PruneAbove = 1_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, Answer> _answers = new(StringComparer.Ordinal);

    private sealed record Answer(DateTimeOffset At, ServerDetails? Details);

    /// <summary>
    /// Asks the address, or hands back the answer it last gave. Null when it did not answer, or
    /// answered with something this could not read.
    /// </summary>
    public async Task<ServerDetails?> AskAsync(string origin, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var now = time.GetUtcNow();

        if (_answers.TryGetValue(origin, out var remembered)
            && now - remembered.At < (remembered.Details is null ? RememberFailure : Remember))
        {
            return remembered.Details;
        }

        var details = await ReadAsync(origin, ct).ConfigureAwait(false);

        _answers[origin] = new Answer(now, details);

        if (_answers.Count > PruneAbove)
            Prune(now);

        return details;
    }

    /// <summary>
    /// Asks the address and passes what it said to Cloud. Does nothing when it did not answer: an
    /// address saved without a group is what my.modbot.co has always stored, and is better than a
    /// group somebody else made up.
    /// </summary>
    public async Task SendDetailsAsync(string origin, CancellationToken ct)
    {
        var details = await AskAsync(origin, ct).ConfigureAwait(false);

        if (details is null || details.IsEmpty)
            return;

        await cloud.RecordServerAsync(origin, details, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same, without making a caller wait. For the page routes: a page is never held back by a
    /// server or a Cloud that is slow, and the app asks again once it has rendered.
    /// </summary>
    public void SendDetailsInBackground(string origin)
    {
        // Not the request's token: the response is sent long before either call answers.
        _ = Task.Run(async () =>
        {
            try
            {
                await SendDetailsAsync(origin, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not learn what a Modbot address is.");
            }
        });
    }

    private async Task<ServerDetails?> ReadAsync(string origin, CancellationToken ct)
    {
        try
        {
            using var client = factory.CreateClient(HttpClientName);
            client.Timeout = Timeout;
            client.MaxResponseContentBufferSize = MaxBytes;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{origin}/api/server");
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // A redirect is not followed, so anything but a plain answer here is no answer.
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
                return null;

            var body = await ReadCappedAsync(response, ct).ConfigureAwait(false);
            if (body is null)
                return null;

            var answer = JsonSerializer.Deserialize<ServerAnswer>(body, Json);
            return answer is null ? null : Clean(answer);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                     && !ct.IsCancellationRequested)
        {
            // The address, never the answer: whatever is at that address wrote the answer.
            log.LogDebug("Could not ask {Address} what it is.", origin);
            return null;
        }
    }

    /// <summary>The body, or null when there is more of it than a real answer has.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var kept = new MemoryStream();

        var buffer = new byte[8192];
        int read;

        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (kept.Length + read > MaxBytes)
                return null;

            kept.Write(buffer, 0, read);
        }

        return kept.ToArray();
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (origin, answer) in _answers)
        {
            if (now - answer.At >= Remember)
                _answers.TryRemove(origin, out _);
        }
    }

    private static ServerDetails Clean(ServerAnswer answer) => new(
        Text(answer.GroupId, MaxGroupIdLength),
        Text(answer.Name, MaxGroupNameLength),
        Url(answer.IconUrl),
        Url(answer.BannerUrl),
        Email(answer.OwnerEmail));

    private const int MaxGroupIdLength = 128;
    private const int MaxGroupNameLength = 200;
    private const int MaxUrlLength = 1024;
    private const int MaxEmailLength = 254;

    /// <summary>
    /// Trims, removes control and format characters, and cuts to length. Null when nothing is left.
    /// </summary>
    /// <remarks>
    /// Format characters go because a right-to-left override makes one row in a list render as
    /// another. The same rule as Cloud's <c>ClientText</c>.
    /// </remarks>
    private static string? Text(string? value, int maxLength)
    {
        if (value is null)
            return null;

        var cleaned = new string([.. value.Where(c =>
            !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();

        return cleaned.Length switch
        {
            0 => null,
            _ when cleaned.Length > maxLength => cleaned[..maxLength],
            _ => cleaned,
        };
    }

    /// <summary>A picture, which VRChat serves over https. Anything else is left out.</summary>
    private static string? Url(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsoluteUri.Length <= MaxUrlLength
            ? uri.AbsoluteUri
            : null;

    /// <summary>
    /// An address, loosely: an <c>@</c> with something either side and a dot in the domain. Cloud
    /// applies the same rule again; nothing here is the authority on what an address is.
    /// </summary>
    private static string? Email(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxEmailLength
            || trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl))
        {
            return null;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
            return null;

        var domain = trimmed[(at + 1)..];

        return domain.Contains('.', StringComparison.Ordinal) && !domain.StartsWith('.') && !domain.EndsWith('.')
            ? trimmed.ToLowerInvariant()
            : null;
    }

    /// <summary>What <c>GET /api/server</c> answers. Every field may be missing.</summary>
    private sealed record ServerAnswer(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("groupId")] string? GroupId,
        [property: JsonPropertyName("iconUrl")] string? IconUrl,
        [property: JsonPropertyName("bannerUrl")] string? BannerUrl,
        [property: JsonPropertyName("ownerEmail")] string? OwnerEmail);
}
