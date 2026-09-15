using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Retention;

/// <summary>
/// What an admin can change about Cloud. One row, in the main database; the table is
/// <c>settings</c>.
/// </summary>
/// <remarks>
/// A missing row means the defaults. Nothing creates the row until an admin saves.
/// </remarks>
public sealed class CloudSettings
{
    public const int SingleRowId = 1;

    /// <summary>Longest window an admin may set, so a typo cannot mean "centuries".</summary>
    public const int MaxKeepDays = 3650;

    /// <summary>
    /// Log lines are most of the storage and carry other players' names and private instance
    /// locations. Ninety days is enough to re-read a quarter's lines with a newer parser
    /// (cloud log backup spec 6).
    /// </summary>
    public const int DefaultLogLineKeepDays = 90;

    /// <summary>A year lets a trend compare a month with the same month last year.</summary>
    public const int DefaultLogEventKeepDays = 365;

    public int Id { get; set; } = SingleRowId;

    /// <summary>Days to keep log lines, counted from when Cloud received them. 0 keeps them forever.</summary>
    public int LogLineKeepDays { get; set; } = DefaultLogLineKeepDays;

    /// <summary>Days to keep parsed events, counted from when Cloud received them. 0 keeps them forever.</summary>
    public int LogEventKeepDays { get; set; } = DefaultLogEventKeepDays;

    public static bool IsValidKeepDays(int days) => days is >= 0 and <= MaxKeepDays;
}

internal sealed class CloudSettingsConfiguration : IEntityTypeConfiguration<CloudSettings>
{
    public void Configure(EntityTypeBuilder<CloudSettings> entity)
    {
        entity.ToTable("settings", t => t.HasCheckConstraint("ck_settings_single_row", "id = 1"));
        entity.HasKey(s => s.Id);
        entity.Property(s => s.Id).ValueGeneratedNever();

        // No database defaults on the day counts: 0 means "keep forever", and EF leaves a column
        // with a database default out of the insert when its value is 0, which would quietly turn
        // an admin's "forever" into the default.
    }
}
