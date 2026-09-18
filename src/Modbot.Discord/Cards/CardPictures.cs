using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Modbot.Core.Files;
using Modbot.Core.Logging;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Cards;

/// <summary>The addresses one card puts in its picture slots, already resolved.</summary>
/// <remarks>
/// A plain pair rather than the pictures themselves, so the card builders stay what they are: a
/// row in, an embed out, no I/O. Fetching and uploading happen in the poster, which is the only
/// part that knows about a message.
/// </remarks>
/// <param name="Thumbnail">The small picture in the card's top corner.</param>
/// <param name="Image">The large picture across the bottom of the card.</param>
/// <param name="AuthorIcon">The round picture beside the line above the title.</param>
public readonly record struct CardPicture(
    string? Thumbnail = null,
    string? Image = null,
    string? AuthorIcon = null)
{
    /// <summary>A card with no pictures, which is what every card falls back to.</summary>
    public static CardPicture None => default;
}

/// <summary>
/// Fetches the pictures a card wants and hands back the file to send with the message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Anything that goes wrong means no picture.</strong> A missing file, a VRChat with no
/// session, a picture too large for Discord, a deployment whose operator turned picture fetching
/// off: all of them come back as null and the card is posted without it. A broken picture and a
/// post that did not happen are both worse than a card with one less thing on it.
/// </para>
/// <para>
/// <strong>What is fetched is remembered.</strong> A singleton with a small bounded store, because
/// the same faces come round again: ten moderation cards in one message are often about the same
/// person, and an instance card is rewritten every minute for hours. Failures are remembered too,
/// which matters more -- somebody with no picture would otherwise be asked for on every pass for
/// as long as their card exists.
/// </para>
/// </remarks>
public sealed class CardPictures
{
    /// <summary>
    /// The most Modbot will send Discord for one picture. Discord's own limit on a message from an
    /// unboosted server is ten megabytes for everything on it; a profile picture is a few hundred
    /// kilobytes, so anything past this is not a picture for a card.
    /// </summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>How many addresses are remembered before the oldest is forgotten.</summary>
    public const int Remembered = 64;

    /// <summary>Bytes past this are fetched but not remembered: the big ones are rarely reused.</summary>
    private const int RememberUpTo = 1024 * 1024;

    private readonly IPictures? _pictures;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DiscordPicture?> _held = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <param name="pictures">
    /// Where the bytes come from. Null in a deployment with no VRChat side wired up at all, and in
    /// the tests that do not care about pictures; every card then goes without one.
    /// </param>
    public CardPictures(IPictures? pictures = null, ILogger? log = null)
    {
        _pictures = pictures;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>
    /// Starts collecting the files for one message.
    /// </summary>
    /// <param name="on">
    /// The operator's switch for fetching VRChat pictures through this server
    /// (<c>Settings.VRChatImagesProxied</c>). Off means every card on this message goes without a
    /// picture, the same way the web app shows no faces.
    /// </param>
    public CardPictureMessage ForMessage(bool on = true) => new(on ? this : null);

    /// <summary>One picture as a file to send, or null when there is none to send.</summary>
    internal async Task<DiscordPicture?> FetchAsync(string url, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(url, out var remembered))
                return remembered;
        }

        DiscordPicture? picture = null;

        try
        {
            if (_pictures is not null && await _pictures.FetchAsync(url, ct).ConfigureAwait(false) is { } bytes)
                picture = Named(url, bytes);
        }
        catch (OperationCanceledException)
        {
            // A cancelled pass is not a picture that does not exist, so it is not remembered.
            throw;
        }
#pragma warning disable CA1031 // A picture is never worth losing a post over.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning(ex, "Could not fetch a picture for a Discord card; the card goes without it.");
        }

        Remember(url, picture);
        return picture;
    }

    /// <summary>
    /// The file name for one address: the same address always gives the same name, which is what
    /// lets ten cards about one person share a single upload.
    /// </summary>
    /// <remarks>
    /// A hash of the address rather than anything from it. VRChat file addresses are long, carry
    /// query strings and are not guaranteed to end in anything, and Discord decides what a file is
    /// from its extension -- so the extension comes from what the host said the bytes are.
    /// </remarks>
    public static DiscordPicture? Named(string url, PictureBytes bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(bytes);

        if (Extension(bytes.ContentType) is not { } extension)
            return null;

        if (bytes.Bytes.Length == 0 || bytes.Bytes.Length > MaxBytes)
            return null;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        var name = new StringBuilder("p", 18);

        for (var i = 0; i < 8; i++)
            name.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));

        return new DiscordPicture(name.Append(extension).ToString(), bytes.Bytes);
    }

    /// <summary>
    /// The file extension for a content type, or null for bytes a card cannot show.
    /// </summary>
    /// <remarks>
    /// Pictures only. VRChat serves video from the same addresses and Modbot's file route passes it
    /// on for the web app, but an embed has nowhere to put one, and a message with a video hanging
    /// off the bottom of a card is not the card anybody asked for.
    /// </remarks>
    public static string? Extension(string? contentType)
    {
        var type = contentType is null ? string.Empty : contentType.Split(';')[0].Trim();

        return type.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => null,
        };
    }

    private void Remember(string url, DiscordPicture? picture)
    {
        if (picture is { Bytes.Length: > RememberUpTo })
            return;

        lock (_gate)
        {
            if (!_held.TryAdd(url, picture))
                return;

            _order.Enqueue(url);

            while (_order.Count > Remembered)
                _held.Remove(_order.Dequeue());
        }
    }
}

/// <summary>
/// The pictures for one Discord message: what to send, and what each card calls them.
/// </summary>
/// <remarks>
/// Held per message rather than per card because Discord counts files by the message. The
/// moderation log puts ten cards in one message, and two of those cards being about the same
/// person costs one file, not two.
/// </remarks>
public sealed class CardPictureMessage
{
    private readonly CardPictures? _source;
    private readonly List<DiscordPicture> _files = [];

    internal CardPictureMessage(CardPictures? source) => _source = source;

    /// <summary>The files to send with the message. Empty when no card wanted a picture.</summary>
    public IReadOnlyList<DiscordPicture> Files => _files;

    /// <summary>
    /// Fetches one picture and gives back the address a card refers to it by, or null when the
    /// card should show none -- including when the message is already carrying as many files as
    /// Discord accepts.
    /// </summary>
    public async Task<string?> AddAsync(string? url, CancellationToken ct)
    {
        if (_source is null || string.IsNullOrWhiteSpace(url))
            return null;

        var picture = await _source.FetchAsync(url, ct).ConfigureAwait(false);

        if (picture is null)
            return null;

        foreach (var held in _files)
        {
            if (string.Equals(held.Name, picture.Name, StringComparison.Ordinal))
                return picture.Reference;
        }

        if (_files.Count >= DiscordPicture.PerMessage)
            return null;

        _files.Add(picture);
        return picture.Reference;
    }

    /// <summary>
    /// The icon and the image for one card, in one step, since most cards want both or neither.
    /// </summary>
    public async Task<CardPicture> ForCardAsync(string? thumbnailUrl, string? imageUrl, CancellationToken ct)
        => new(
            await AddAsync(thumbnailUrl, ct).ConfigureAwait(false),
            await AddAsync(imageUrl, ct).ConfigureAwait(false));

    /// <summary>
    /// What a picture is called on a message that already carries it, without sending it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a card that is rewritten: the instance announcer edits one message every minute for
    /// as long as an instance is open, and the file it posted the first time is still attached.
    /// The name is worked out from the address, so the same address always names the same file --
    /// the edit keeps pointing at it and nothing goes over the wire.
    /// </para>
    /// <para>
    /// A picture whose address has changed since the message was posted names a file that is not
    /// there, and Discord draws the card without it. That is the same thing the card does when a
    /// picture cannot be fetched at all, and the next post carries the new one.
    /// </para>
    /// </remarks>
    public async Task<string?> ReferenceAsync(string? url, CancellationToken ct)
    {
        if (_source is null || string.IsNullOrWhiteSpace(url))
            return null;

        var picture = await _source.FetchAsync(url, ct).ConfigureAwait(false);
        return picture?.Reference;
    }
}
