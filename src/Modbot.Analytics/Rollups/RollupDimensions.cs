using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Rollups;

/// <summary>
/// How a person is named in a rollup's <c>dimension</c> column.
/// </summary>
/// <remarks>
/// <para>
/// Platform-qualified, because a Discord snowflake and a VRChat id share one text column and must
/// never merge into one moderator (spec 5.3). The id itself is passed through untouched -- opaque,
/// never parsed, never validated (spec 3.1.1).
/// </para>
/// <para>
/// Anything counting per user through <see cref="IRollupCounter"/> should dimension with this, so
/// that a purge (spec 5.5) can find the rows again.
/// </para>
/// </remarks>
public static class RollupDimensions
{
    public static string ForUser(FactPlatform platform, string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return $"{Label(platform)}:{id}";
    }

    /// <summary>
    /// The platform's name in a dimension. <see cref="RollupJob"/> builds the same string in SQL;
    /// this is the definition both sides answer to.
    /// </summary>
    public static string Label(FactPlatform platform) => platform.ToString().ToLowerInvariant();
}
