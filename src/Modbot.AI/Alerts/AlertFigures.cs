using System.Text.Json;

namespace Modbot.AI.Alerts;

/// <summary>
/// Everything one alert is built from, and exactly what is stored beside it and shown.
/// </summary>
/// <remarks>
/// Counts, a stretch of time, and at most a world or instance name. Nothing here may name, number or
/// describe a person (AI insights design §8.3), which is also why the AI sentence is written from
/// this object and nothing else.
/// </remarks>
/// <param name="Counts">What the figure counts, in plain words: "joins", "flags".</param>
/// <param name="Earlier">The matching earlier windows, newest first.</param>
/// <param name="Where">The world or instance, for the two instance watchers. Null otherwise.</param>
/// <param name="Link">Where in Modbot to look, as a path. Null when there is nowhere.</param>
public sealed record AlertFigures(
    string Watcher,
    string Label,
    string Counts,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    decimal Now,
    decimal Normal,
    decimal Spread,
    decimal Score,
    string Sensitivity,
    IReadOnlyList<decimal> Earlier,
    string? Where = null,
    string? Link = null)
{
    /// <summary>camelCase, the same shape the API returns, so the stored JSON and the page agree.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null for a row whose figures cannot be read back, rather than failing the whole list.</summary>
    public static AlertFigures? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AlertFigures>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
