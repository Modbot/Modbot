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
    /// A year. Events are small, so storage no longer argues for less; but each one names another
    /// player and where they were, including private instances, held by the project rather than by
    /// their group, so they should not be kept forever. A year is a full year of backup, and enough to
    /// rebuild the totals when a definition changes. The totals themselves are kept forever
    /// (cloud event backup spec 6).
    /// </summary>
    public const int DefaultEventKeepDays = 365;

    public int Id { get; set; } = SingleRowId;

    /// <summary>Days to keep events, counted from when Cloud received them. 0 keeps them forever.</summary>
    public int EventKeepDays { get; set; } = DefaultEventKeepDays;

    public static bool IsValidKeepDays(int days) => days is >= 0 and <= MaxKeepDays;
}

internal sealed class CloudSettingsConfiguration : IEntityTypeConfiguration<CloudSettings>
{
    public void Configure(EntityTypeBuilder<CloudSettings> entity)
    {
        entity.ToTable("settings", t => t.HasCheckConstraint("ck_settings_single_row", "id = 1"));
        entity.HasKey(s => s.Id);
        entity.Property(s => s.Id).ValueGeneratedNever();

        // No database default on the day count: 0 means "keep forever", and EF leaves a column with a
        // database default out of the insert when its value is 0, which would quietly turn an admin's
        // "forever" into the default.
    }
}
