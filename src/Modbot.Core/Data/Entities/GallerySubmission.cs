namespace Modbot.Core.Data.Entities;

/// <summary>What became of a picture a member posted for the group gallery.</summary>
public static class GallerySubmissionStatus
{
    /// <summary>Waiting for staff.</summary>
    public const string Waiting = "waiting";

    /// <summary>Staff approved it and Modbot added it to a gallery on VRChat.</summary>
    public const string Added = "added";

    /// <summary>Staff said no. Nothing was sent to VRChat or told to the member.</summary>
    public const string Declined = "declined";
}

/// <summary>
/// A picture a member posted in the Discord channel chosen for gallery pictures, waiting for staff
/// to add it to one of the group's galleries on VRChat. The table is <c>gallery_submission</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The picture is kept only while somebody has to look at it.</strong> Discord's links to
/// attachments stop working after a day, and staff may look days later, so Modbot copies the
/// picture when it is posted (<see cref="Bytes"/>) and drops the copy once staff have decided. What
/// stays is who posted it, the link to the Discord message and what was decided.
/// </para>
/// <para>
/// One row for each picture, not for each message: a message with three pictures is three rows,
/// told apart by <see cref="AttachmentIndex"/>. Seeing the same message twice (live, then again
/// while catching up) is harmless, because the pair is unique.
/// </para>
/// <para>
/// Names a Discord account, so a purge of that account deletes its rows.
/// </para>
/// </remarks>
public class GallerySubmission
{
    /// <summary>The most pictures kept waiting at once, so a flooded channel cannot fill the database.</summary>
    public const int MaxWaiting = 100;

    public Guid Id { get; set; }

    public string DiscordMessageId { get; set; } = string.Empty;

    /// <summary>Which picture of the message this is, counting from zero.</summary>
    public int AttachmentIndex { get; set; }

    /// <summary>The channel (or thread) the message is in: where Modbot reacts to it.</summary>
    public string DiscordChannelId { get; set; } = string.Empty;

    /// <summary>The link that opens the message in Discord.</summary>
    public string MessageUrl { get; set; } = string.Empty;

    public string AuthorDiscordId { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

    /// <summary>The picture's file name in Discord.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary><c>image/png</c> or <c>image/jpeg</c>, as the bytes say.</summary>
    public string ContentType { get; set; } = "image/png";

    /// <summary>The picture, while <see cref="Status"/> is <see cref="GallerySubmissionStatus.Waiting"/>. Empty once decided.</summary>
    public byte[] Bytes { get; set; } = [];

    public string Status { get; set; } = GallerySubmissionStatus.Waiting;

    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>
    /// VRChat's id for the picture once it has been uploaded, kept so adding it to a gallery can be
    /// tried again without uploading it a second time (uploads are one a minute).
    /// </summary>
    public string? VRChatFileId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The account that decided. Null while waiting, and once that account is gone.</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>The gallery the picture was added to.</summary>
    public string? GalleryId { get; set; }

    /// <summary>VRChat's id for the gallery entry, once added.</summary>
    public string? VRChatImageId { get; set; }
}
