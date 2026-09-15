using System.Text.Json;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Insights;

/// <summary>One number for the stretch an insight covers and the same number for the stretch before.</summary>
/// <param name="Name">Plain words, as the model and the reader both see it.</param>
/// <param name="Now">Null when Modbot has nothing recorded for it, which is different from zero.</param>
public sealed record InsightFigure(string Name, decimal? Now, decimal? Before);

public sealed record InsightListItem(string Name, decimal Value);

/// <summary>A short ranked list for the stretch an insight covers, such as the busiest worlds.</summary>
public sealed record InsightList(string Name, IReadOnlyList<InsightListItem> Items);

/// <summary>
/// Everything the model is given for one insight, and exactly what is stored beside its text.
/// </summary>
/// <remarks>
/// Counts and world names only. Nothing here may name, number or describe a person (design §1.1),
/// which is why there is no per-moderator figure even on the moderation team kind.
/// </remarks>
public sealed record InsightFigures(
    string Kind,
    DateOnly FirstDay,
    DateOnly LastDay,
    DateOnly BeforeFirstDay,
    DateOnly BeforeLastDay,
    IReadOnlyList<InsightFigure> Figures,
    IReadOnlyList<InsightList> Lists)
{
    /// <summary>camelCase, the same shape the API returns, so the stored JSON and the page agree.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null for a row whose figures cannot be read back, rather than failing the whole list.</summary>
    public static InsightFigures? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<InsightFigures>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The UTC days one insight covers, and the stretch of the same length before them.</summary>
public sealed record InsightPeriod(DateOnly FirstDay, DateOnly LastDay, DateOnly BeforeFirstDay, DateOnly BeforeLastDay)
{
    /// <summary>The whole UTC days ending yesterday: one day, or seven.</summary>
    /// <param name="today">The UTC day the insight is written on. Never included, because it is not over.</param>
    public static InsightPeriod Ending(DateOnly today, string every)
    {
        var days = InsightKinds.Days(every);
        var last = today.AddDays(-1);
        var first = last.AddDays(-(days - 1));
        var beforeLast = first.AddDays(-1);
        return new InsightPeriod(first, last, beforeLast.AddDays(-(days - 1)), beforeLast);
    }

    public static DateTimeOffset DayStart(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
}
