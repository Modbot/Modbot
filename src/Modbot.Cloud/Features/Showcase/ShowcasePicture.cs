using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Showcase;

/// <summary>
/// One picture from a showcase row, kept by Cloud and served from Cloud's own address.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the bytes are here at all.</strong> A sponsor's picture and a VRChat group's icon and
/// banner are addresses on somebody else's host, and VRChat's refuses to serve its pictures to a page
/// that is not VRChat's own. A reader that simply used the typed address got an empty square, and the
/// only readers that could work around it were the ones behind a signed-in browser — which is neither
/// the companion nor anything the project adds later. Fetching once, on save, and serving the bytes
/// from Cloud gives every reader an address that works with nothing to arrange.
/// </para>
/// <para>
/// <strong>Bytes in the database rather than a file store.</strong> There are a handful of showcase
/// rows and three pictures each, capped at <see cref="MaxBytes"/>; the whole of it is a few megabytes
/// at the very worst, in a database that is already small and backed up as one unit. Adding an object
/// store to Cloud for this would be a second thing to run, configure and restore, for pictures that
/// change a few times a year.
/// </para>
/// <para>
/// <strong>A new row for every save.</strong> The id is the address, so an address never has to be
/// re-checked by a reader that cached it and Cloud can answer with a year-long cache header. The row
/// that was replaced is deleted in the same save, so nothing is left behind.
/// </para>
/// <para>
/// The address it came from is kept, so an administrator can see what was typed in and Cloud can
/// fetch it again.
/// </para>
/// </remarks>
public sealed class ShowcasePicture
{
    /// <summary>The most a picture may weigh. Bigger is refused rather than shrunk.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public const int MaxContentTypeLength = 64;

    /// <summary>The route Cloud serves these from.</summary>
    public const string Route = "/api/v1/showcase-pictures";

    /// <summary>
    /// What a reader is given for one of a row's three pictures: Cloud's own address when Cloud kept
    /// a copy, and otherwise the address an administrator typed in.
    /// </summary>
    public static string? Address(Uri publicAddress, Guid? saved, string? typed)
    {
        ArgumentNullException.ThrowIfNull(publicAddress);

        return saved is { } id ? new Uri(publicAddress, $"{Route}/{id}").ToString() : typed;
    }

    public Guid Id { get; set; }

    /// <summary>The address it was fetched from.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>What Cloud answers with: <c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c> or <c>image/gif</c>.</summary>
    public string ContentType { get; set; } = string.Empty;

    public byte[] Bytes { get; set; } = [];

    public DateTimeOffset SavedAt { get; set; }
}

internal sealed class ShowcasePictureConfiguration : IEntityTypeConfiguration<ShowcasePicture>
{
    public void Configure(EntityTypeBuilder<ShowcasePicture> entity)
    {
        entity.ToTable("showcase_picture");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedNever();

        entity.Property(e => e.SourceUrl).HasMaxLength(ShowcaseEntry.MaxUrlLength);
        entity.Property(e => e.ContentType).HasMaxLength(ShowcasePicture.MaxContentTypeLength);
    }
}
