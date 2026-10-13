using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Files;
using Modbot.Core.Logging;
using Modbot.Core.Net;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Gallery;

/// <summary>
/// Takes the pictures members post in the channel chosen for gallery pictures and puts each in the
/// queue on the Gallery page (<see cref="GallerySubmission"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only the one channel, only PNG and JPEG, only people.</strong> A message anywhere else, a
/// bot's message, and a file that is not a PNG or JPEG are left alone. VRChat takes nothing else, so
/// a picture it would refuse is not queued for staff to find out later.
/// </para>
/// <para>
/// <strong>The picture is copied at once.</strong> Discord's link to an attachment stops working
/// after a day, and staff may look days later. The copy is fetched the way every address Modbot is
/// given is (<see cref="PictureLinks"/>), checked by its first bytes rather than by what Discord
/// called it, and no larger than <see cref="PictureLinks.MaxBytes"/>.
/// </para>
/// <para>
/// A message seen twice (live, then again while catching up) adds each picture once. At
/// <see cref="GallerySubmission.MaxWaiting"/> pictures waiting, new ones are not kept.
/// </para>
/// </remarks>
public sealed class GallerySubmissionIntake
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly PictureLinks _links;
    private readonly ILogger _log;

    public GallerySubmissionIntake(ModbotContext db, IModbotClock clock, PictureLinks? links = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _links = links ?? PictureLinks.Shared;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>Queues the message's pictures, if it is in the gallery channel. Returns how many were queued.</summary>
    public async Task<int> TakeAsync(DiscordMessageSnapshot message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.AuthorIsBot || message.Attachments.Count == 0)
            return 0;

        var channelId = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGalleryChannelId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // A message in a thread under the channel counts: its channel is the thread's parent.
        if (string.IsNullOrEmpty(channelId) || !string.Equals(message.ChannelId, channelId, StringComparison.Ordinal))
            return 0;

        var waiting = await _db.GallerySubmissions
            .CountAsync(s => s.Status == GallerySubmissionStatus.Waiting, ct).ConfigureAwait(false);

        var queued = 0;

        for (var index = 0; index < message.Attachments.Count; index++)
        {
            var attachment = message.Attachments[index];

            if (!LooksLikeAPicture(attachment))
                continue;

            if (waiting + queued >= GallerySubmission.MaxWaiting)
            {
                _log.Warning(
                    "The gallery queue holds {Max} pictures, so {Author}'s picture in Discord message {MessageId} was not kept",
                    GallerySubmission.MaxWaiting, message.AuthorName, message.Id);
                break;
            }

            var taken = await _db.GallerySubmissions.AnyAsync(
                s => s.DiscordMessageId == message.Id && s.AttachmentIndex == index, ct).ConfigureAwait(false);

            if (taken)
                continue;

            var fetched = await _links.FetchAsync(attachment.Url, ct).ConfigureAwait(false);
            var type = fetched.Picture is { } picture ? PictureFormats.Sniff(picture.Bytes) : null;

            if (fetched.Picture is null || type is not ("image/png" or "image/jpeg"))
            {
                _log.Debug(
                    "Not queuing {File} from Discord message {MessageId}: {Reason}",
                    attachment.Name, message.Id, fetched.Problem ?? "it is not a PNG or JPEG");
                continue;
            }

            _db.GallerySubmissions.Add(new GallerySubmission
            {
                Id = Guid.NewGuid(),
                DiscordMessageId = message.Id,
                AttachmentIndex = index,
                DiscordChannelId = message.ReadFrom,
                MessageUrl = $"https://discord.com/channels/{message.GuildId}/{message.ReadFrom}/{message.Id}",
                AuthorDiscordId = message.AuthorId,
                AuthorName = Clip(message.AuthorName, 100),
                FileName = Clip(attachment.Name, 255),
                ContentType = type,
                Bytes = fetched.Picture.Bytes,
                Status = GallerySubmissionStatus.Waiting,
                SubmittedAt = message.SentAt,
            });

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            queued++;
        }

        return queued;
    }

    /// <summary>
    /// Whether Discord's description of the file is worth fetching: a PNG or JPEG by its type, or by
    /// its name when Discord gave no type, and no larger than Modbot sends to VRChat.
    /// </summary>
    internal static bool LooksLikeAPicture(DiscordAttachmentSnapshot attachment)
    {
        if (attachment.Size is <= 0 or > PictureLinks.MaxBytes)
            return false;

        if (!string.IsNullOrWhiteSpace(attachment.Type))
        {
            return attachment.Type.StartsWith("image/png", StringComparison.OrdinalIgnoreCase)
                || attachment.Type.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase);
        }

        return attachment.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || attachment.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || attachment.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
