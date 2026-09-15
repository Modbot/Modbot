using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.AI.Usage;

/// <summary>One model's price as OpenRouter lists it, per million tokens.</summary>
public sealed record OpenRouterPrice(string Model, decimal InputPerMillion, decimal? CachedInputPerMillion, decimal OutputPerMillion);

/// <summary>What a fetch did.</summary>
/// <param name="Saved">How many prices were saved.</param>
/// <param name="Error">What went wrong, as a sentence, or null.</param>
/// <param name="RateLimited">OpenRouter answered 429.</param>
public sealed record AiPriceFetchResult(int Saved, string? Error, bool RateLimited = false);

/// <summary>
/// Fetches model prices from OpenRouter's public model list (AI chat design §10.1).
/// </summary>
/// <remarks>
/// <para>
/// <c>GET https://openrouter.ai/api/v1/models</c> needs no key and lists every model with
/// <c>pricing.prompt</c>, <c>pricing.completion</c> and, for most, <c>pricing.input_cache_read</c>:
/// US dollars per token, as strings. Checked against the live list in September 2026, when it had
/// 446 models. A price of <c>-1</c> marks a router whose price depends on the model it picks; those
/// are left out, as are time-of-day <c>overrides</c>, which only a few models have.
/// </para>
/// <para>
/// Its own <see cref="HttpClient"/>, never the VRChat one or its proxy, and no retries: a failure,
/// including a 429, waits for the next scheduled fetch.
/// </para>
/// </remarks>
public sealed class OpenRouterPrices
{
    public const string HttpClientName = "Modbot.AI.Prices";

    public static readonly Uri ModelList = new("https://openrouter.ai/api/v1/models");

    private const long MaxBytes = 32 * 1024 * 1024;
    private const decimal Million = 1_000_000m;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ModbotContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IModbotClock _clock;

    public OpenRouterPrices(ModbotContext db, IHttpClientFactory http, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _http = http;
        _clock = clock;
    }

    /// <summary>Fetches the list and saves every price in it. Prices of models it no longer lists are kept.</summary>
    public async Task<AiPriceFetchResult> FetchAsync(CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = Timeout;

        byte[] body;
        try
        {
            using var response = await client.GetAsync(ModelList, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new AiPriceFetchResult(0, "openrouter.ai answered 429.", RateLimited: true);

            if (!response.IsSuccessStatusCode)
                return new AiPriceFetchResult(0, $"openrouter.ai answered {(int)response.StatusCode}.");

            if (response.Content.Headers.ContentLength > MaxBytes)
                return new AiPriceFetchResult(0, "openrouter.ai sent a model list too large to read.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return new AiPriceFetchResult(0, "openrouter.ai sent a model list too large to read.");
                buffer.Write(chunk, 0, read);
            }

            body = buffer.ToArray();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiPriceFetchResult(0, "openrouter.ai did not answer in time.");
        }
        catch (HttpRequestException e)
        {
            return new AiPriceFetchResult(0, $"Could not reach openrouter.ai: {e.Message}");
        }

        IReadOnlyList<OpenRouterPrice> prices;
        try
        {
            prices = Read(body);
        }
        catch (JsonException)
        {
            return new AiPriceFetchResult(0, "openrouter.ai answered with something that is not a model list.");
        }

        if (prices.Count == 0)
            return new AiPriceFetchResult(0, "openrouter.ai listed no prices.");

        await SaveAsync(prices, ct).ConfigureAwait(false);
        return new AiPriceFetchResult(prices.Count, null);
    }

    /// <summary>The prices in a model list. Models without a usable price are left out.</summary>
    /// <exception cref="JsonException">The body is not JSON.</exception>
    public static IReadOnlyList<OpenRouterPrice> Read(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        using var document = JsonDocument.ParseValue(ref reader);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var prices = new List<OpenRouterPrice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { Length: > 0 and <= AiSettingsRules.MaxModelLength } id
                || !model.TryGetProperty("pricing", out var pricing)
                || pricing.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var input = PerMillion(pricing, "prompt");
            var output = PerMillion(pricing, "completion");
            if (input is null || output is null || !seen.Add(id))
                continue;

            prices.Add(new OpenRouterPrice(id, input.Value, PerMillion(pricing, "input_cache_read"), output.Value));
        }

        return prices;
    }

    /// <summary>A per-token price turned into a price per million, or null when missing or not a real price.</summary>
    private static decimal? PerMillion(JsonElement pricing, string name)
    {
        if (!pricing.TryGetProperty(name, out var value))
            return null;

        decimal perToken;
        switch (value.ValueKind)
        {
            case JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                perToken = parsed;
                break;
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                perToken = number;
                break;
            default:
                return null;
        }

        if (perToken < 0)
            return null;

        var perMillion = Math.Round(perToken * Million, 6, MidpointRounding.AwayFromZero);
        return perMillion > AiPriceRules.MaxAmount ? null : perMillion;
    }

    private async Task SaveAsync(IReadOnlyList<OpenRouterPrice> prices, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var existing = await _db.AiFetchedPrices.ToDictionaryAsync(p => p.Model, StringComparer.Ordinal, ct).ConfigureAwait(false);

        foreach (var price in prices)
        {
            if (!existing.TryGetValue(price.Model, out var row))
            {
                row = new AiFetchedPrice { Model = price.Model };
                _db.AiFetchedPrices.Add(row);
            }

            row.InputPerMillion = price.InputPerMillion;
            row.CachedInputPerMillion = price.CachedInputPerMillion;
            row.OutputPerMillion = price.OutputPerMillion;
            row.FetchedAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Limits on what an amount of money or a price may be.</summary>
public static class AiPriceRules
{
    /// <summary>The largest amount a limit or a price may be. Anything bigger is a typing mistake.</summary>
    public const decimal MaxAmount = 1_000_000_000m;
}

/// <summary>
/// Fetches OpenRouter's prices once a day while AI is on and set to OpenRouter, and changes token
/// limits into money limits once a model has a price (AI chat design §10.1, §10.5).
/// </summary>
/// <remarks>
/// Only for OpenRouter on the schedule, so a deployment on a local model server makes no call to
/// openrouter.ai unless somebody presses Fetch prices. A failed fetch is tried again at the next
/// hourly pass; after a 429 it waits a day.
/// </remarks>
public sealed class AiPriceFetchService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public static readonly TimeSpan FetchEvery = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    public AiPriceFetchService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        ILogger<AiPriceFetchService>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _clock = clock;
        _log = log ?? NullLogger<AiPriceFetchService>.Instance;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>One pass: fetch if due, then change any token limit that now has a price. Public for the tests.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var now = _clock.UtcNow;

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.AiEnabled, s.AiProvider })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (settings is { AiEnabled: true } && settings.AiProvider == AiProviders.OpenRouter.Id && now >= _notBefore)
        {
            var last = await db.AiFetchedPrices.AsNoTracking().MaxAsync(p => (DateTimeOffset?)p.FetchedAt, ct).ConfigureAwait(false);

            if (last is null || now - last.Value >= FetchEvery)
            {
                var result = await scope.ServiceProvider.GetRequiredService<OpenRouterPrices>().FetchAsync(ct).ConfigureAwait(false);

                if (result.Error is null)
                {
                    _log.LogInformation("Fetched {Count} model prices from OpenRouter.", result.Saved);
                }
                else
                {
                    _notBefore = result.RateLimited ? now + FetchEvery : now + Interval;
                    _log.LogWarning("Could not fetch model prices: {Error}", result.Error);
                }
            }
        }

        await scope.ServiceProvider.GetRequiredService<AiTokenLimits>().ChangeToMoneyAsync(ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "The model price pass failed; trying again in an hour.");
            }

            try
            {
                await _delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
