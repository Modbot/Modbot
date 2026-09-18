using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Pairing;
using Modbot.Core.Time;

namespace Modbot.Companion.Credits;

/// <summary>Somebody the project thanks: a sponsor, or a group that used Modbot early.</summary>
/// <param name="Name">What to call them.</param>
/// <param name="Link">Where their name goes when it is pressed. Empty when there is nowhere.</param>
/// <param name="PictureUrl">Their picture. Empty when there is none.</param>
/// <param name="VRChatGroupId">Their VRChat group, when they have one.</param>
/// <param name="GroupIconUrl">The group's icon.</param>
/// <param name="GroupBannerUrl">The group's banner.</param>
public sealed record CreditPerson(
    string Name,
    string Link,
    string PictureUrl,
    string? VRChatGroupId,
    string? GroupIconUrl,
    string? GroupBannerUrl)
{
    /// <summary>The group's page on vrchat.com, or null when they have no group.</summary>
    public string? GroupPage => string.IsNullOrWhiteSpace(VRChatGroupId)
        ? null
        : $"https://vrchat.com/home/group/{Uri.EscapeDataString(VRChatGroupId)}";
}

/// <summary>Somebody who has written some of Modbot, as GitHub lists them.</summary>
/// <param name="Name">Their GitHub username.</param>
/// <param name="ProfileUrl">Their GitHub profile.</param>
/// <param name="PictureUrl">Their picture.</param>
public sealed record CreditContributor(string Name, string ProfileUrl, string PictureUrl);

/// <summary>The three lists the Credits page shows.</summary>
public sealed record CreditsList(
    IReadOnlyList<CreditPerson> Sponsors,
    IReadOnlyList<CreditPerson> EarlyAdopters,
    IReadOnlyList<CreditContributor> Contributors)
{
    public static CreditsList Empty { get; } = new([], [], []);

    public bool IsEmpty => Sponsors.Count == 0 && EarlyAdopters.Count == 0 && Contributors.Count == 0;
}

/// <summary>
/// The sponsors, early adopters and contributors the client's Credits page shows, read from Modbot
/// Cloud and kept on this PC so the page still has something to show with no network.
/// </summary>
/// <remarks>
/// <para><strong>What this reads, and what is sent.</strong> Three GETs to Modbot Cloud —
/// <c>/api/v1/sponsors</c>, <c>/api/v1/early-adopters</c> and <c>/api/v1/contributors</c> — with no
/// credential, no identity and no body. Nothing about you, your PC, your VRChat account or the
/// servers you are paired with leaves the machine for this; the requests are the same three any
/// stranger could make, and the answer is a list of names the project publishes anyway.</para>
/// <para><strong>Which Cloud, and whether to ask at all, is decided on this PC only</strong> — the
/// <c>cloud</c> object in <c>settings.json</c> and the <c>MODBOT_CLOUD_ENDPOINT</c> and
/// <c>MODBOT_CLOUD_DISABLED</c> environment variables (<see cref="CloudSettings"/>). A paired Modbot
/// server has no say in it and is never asked; with Cloud turned off, this makes no request and the
/// page shows nothing.</para>
/// <para><strong>What is written to your disk.</strong> One file, <c>credits.json</c>, in Modbot's
/// own folder under your user profile: the three lists exactly as Cloud sent them, and when they
/// were read. It exists so the page shows the same names offline that it showed online, and so
/// opening the page does not ask Cloud again every time. Delete it and the page simply asks again.
/// Nothing in it is private and none of it is sent anywhere.</para>
/// <para>A Cloud that cannot be reached is not an error anybody can act on: the page keeps whatever
/// was last read, or shows nothing.</para>
/// </remarks>
public sealed class CloudCredits
{
    /// <summary>The file's name in Modbot's own folder.</summary>
    public const string FileName = "credits.json";

    /// <summary>
    /// How long a read is good for. The same six hours a Modbot server uses: the lists change when
    /// somebody types a row into Cloud admin, which is a few times a year.
    /// </summary>
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly CloudSettings _cloud;
    private readonly string _path;
    private readonly IModbotClock _clock;

    private bool _readTheFile;
    private DateTimeOffset? _readAt;

    /// <param name="path">Where the kept copy lives. <see cref="DefaultPath"/> in the client.</param>
    public CloudCredits(HttpClient http, CloudSettings cloud, string path, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _http = http;
        _cloud = cloud;
        _path = path;
        _clock = clock;
    }

    /// <summary><c>%APPDATA%\Modbot\credits.json</c>.</summary>
    public static string DefaultPath(string applicationData)
        => Path.Combine(applicationData, "Modbot", FileName);

    /// <summary>What the Credits page draws right now. Empty until something has been read.</summary>
    public CreditsList Current { get; private set; } = CreditsList.Empty;

    /// <summary>False when this PC has turned Modbot Cloud off: the page shows nothing.</summary>
    public bool Allowed => !_cloud.Disabled;

    /// <summary>
    /// Shows the kept copy, then reads Cloud if the kept copy is older than
    /// <see cref="AskAgainAfter"/>. Safe to call every time the page is opened.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_cloud.Disabled)
        {
            Current = CreditsList.Empty;
            return;
        }

        if (!_readTheFile)
        {
            _readTheFile = true;

            if (ReadFile() is { } kept)
            {
                Current = kept.Lists;
                _readAt = kept.ReadAt;
            }
        }

        if (_readAt is { } when && _clock.UtcNow - when < AskAgainAfter)
            return;

        if (await FetchAsync(ct).ConfigureAwait(false) is not { } fetched)
            return;

        Current = fetched;
        _readAt = _clock.UtcNow;
        WriteFile(fetched, _readAt.Value);
    }

    private async Task<CreditsList?> FetchAsync(CancellationToken ct)
    {
        if (!ServerAddresses.IsAllowed(_cloud.Endpoint))
            return null;

        try
        {
            var sponsors = await ReadAsync<PersonShape>("/api/v1/sponsors", ct).ConfigureAwait(false);
            var early = await ReadAsync<PersonShape>("/api/v1/early-adopters", ct).ConfigureAwait(false);
            var contributors = await ReadAsync<ContributorShape>("/api/v1/contributors", ct).ConfigureAwait(false);

            if (sponsors is null || early is null || contributors is null)
                return null;

            return new CreditsList(
                [.. sponsors.Select(Person)],
                [.. early.Select(Person)],
                [.. contributors.Select(Contributor)]);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                  && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<List<T>?> ReadAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_cloud.Endpoint, path), ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var page = await response.Content.ReadFromJsonAsync<Page<T>>(Json, ct).ConfigureAwait(false);

        return page?.Items;
    }

    private static CreditPerson Person(PersonShape p) => new(
        p.Name ?? "",
        p.Link ?? "",
        p.ImageUrl ?? "",
        Blank(p.VRChatGroupId),
        Blank(p.GroupImageUrl),
        Blank(p.GroupBannerUrl));

    private static CreditContributor Contributor(ContributorShape c) => new(
        c.Login ?? "", c.Url ?? "", c.AvatarUrl ?? "");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The kept copy, or null when there is none or it cannot be read.</summary>
    private (CreditsList Lists, DateTimeOffset ReadAt)? ReadFile()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var kept = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json);

            if (kept is null)
                return null;

            var lists = new CreditsList(
                [.. (kept.Sponsors ?? []).Select(Person)],
                [.. (kept.EarlyAdopters ?? []).Select(Person)],
                [.. (kept.Contributors ?? []).Select(Contributor)]);

            return (lists, kept.ReadAt ?? DateTimeOffset.MinValue);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file that will not read is not worth telling anybody about: Cloud is asked again
            // and the page fills itself in.
            return null;
        }
    }

    private void WriteFile(CreditsList lists, DateTimeOffset readAt)
    {
        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            var shape = new FileShape(
                readAt,
                [.. lists.Sponsors.Select(Shape)],
                [.. lists.EarlyAdopters.Select(Shape)],
                [.. lists.Contributors.Select(Shape)]);

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(shape, new JsonSerializerOptions(Json) { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only profile costs the offline copy and nothing else.
        }
    }

    private static PersonShape Shape(CreditPerson p) => new(
        p.Name, p.Link, p.PictureUrl, p.VRChatGroupId, p.GroupIconUrl, p.GroupBannerUrl);

    private static ContributorShape Shape(CreditContributor c) => new(c.Name, c.ProfileUrl, c.PictureUrl);

    private sealed record Page<T>(List<T>? Items);

    private sealed record PersonShape(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("link")] string? Link,
        [property: JsonPropertyName("imageUrl")] string? ImageUrl,
        [property: JsonPropertyName("vrChatGroupId")] string? VRChatGroupId,
        [property: JsonPropertyName("groupImageUrl")] string? GroupImageUrl,
        [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl);

    private sealed record ContributorShape(
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("avatarUrl")] string? AvatarUrl);

    private sealed record FileShape(
        [property: JsonPropertyName("readAt")] DateTimeOffset? ReadAt,
        [property: JsonPropertyName("sponsors")] List<PersonShape>? Sponsors,
        [property: JsonPropertyName("earlyAdopters")] List<PersonShape>? EarlyAdopters,
        [property: JsonPropertyName("contributors")] List<ContributorShape>? Contributors);
}
