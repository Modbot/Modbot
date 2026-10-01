using VRChat.API.Client;
using VRChat.API.Model;
using VRChatFileModel = VRChat.API.Model.File;

namespace Modbot.VRChat.Files;

/// <summary>
/// Uploading a picture to VRChat, so a calendar event can carry it on VRChat's calendar.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One request per picture, never retried.</strong> <c>POST /file/image</c> takes the picture
/// as one multipart request and answers with the new file, whose <c>file_…</c> id is what VRChat's
/// calendar wants as <c>imageId</c>. The call is on <see cref="VRChatEndpointClass.FilesUpload"/>,
/// one a minute and not measured; a 429 is a cold stop of that class and the person who chose the
/// picture is told (spec 4.3.1). Uploaded with <see cref="ImagePurpose.Gallery"/>, VRChat's purpose
/// for an ordinary picture.
/// </para>
/// <para>
/// <strong>Checked before anything is sent.</strong> Only PNG and JPEG, told apart by their first
/// bytes rather than by what the browser claims, and no larger than <see cref="MaxBytes"/>. Anything
/// else is refused here and VRChat is never asked.
/// </para>
/// <para>
/// The picture goes to the VRChat account Modbot signs in as for the group: VRChat files belong to
/// an account, not to a group. Modbot keeps none of the bytes; only the id VRChat answers with is
/// stored, on the event.
/// </para>
/// </remarks>
public sealed class VRChatPictureUploads(IVRChatGate gate)
{
    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>
    /// The largest picture Modbot sends: 10 MB.
    /// </summary>
    /// <remarks>
    /// VRChat publishes no limit for <c>POST /file/image</c>; its API description says nothing about
    /// size. Ten megabytes is chosen to be safely under anything VRChat is likely to take, and far
    /// more than an event picture needs. A larger file is refused before VRChat is asked.
    /// </remarks>
    public const long MaxBytes = 10L * 1024 * 1024;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    /// <summary>The types an upload may be, in the order the form's picker lists them.</summary>
    public static IReadOnlyList<string> Types { get; } = [Png, Jpeg];

    private static ReadOnlySpan<byte> PngStart => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];

    /// <summary>
    /// What the bytes are, from their first bytes: <see cref="Png"/>, <see cref="Jpeg"/>, or null for
    /// anything else.
    /// </summary>
    public static string? TypeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngStart))
            return Png;

        if (bytes.StartsWith(JpegStart))
            return Jpeg;

        return null;
    }

    /// <summary>
    /// Why these bytes cannot be uploaded, as a sentence for the form, or null when they can.
    /// </summary>
    public static string? Problem(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return "Choose a picture to upload.";

        if (bytes.Length > MaxBytes)
            return $"The picture is larger than {MaxBytes / (1024 * 1024)} MB.";

        return TypeOf(bytes) is null ? "The picture must be a PNG or JPEG." : null;
    }

    /// <summary>
    /// Uploads one picture, once. The answer's <c>Value.Id</c> is the <c>file_…</c> id.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The bytes fail <see cref="Problem"/>. Callers check first; this is the last line, so nothing
    /// that fails the check can ever reach VRChat.
    /// </exception>
    public Task<VRChatResult<VRChatFileModel>> UploadAsync(byte[] bytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (Problem(bytes) is { } problem)
            throw new ArgumentException(problem, nameof(bytes));

        var type = TypeOf(bytes)!;
        var name = type == Png ? "picture.png" : "picture.jpg";

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.FilesUpload, null, "UploadImage"),
            (client, token) => SendAsync(client, bytes, name, type, token),
            VRChatCallPriority.Interactive,
            ct);
    }

    /// <summary>
    /// The SDK call. A new stream every time it runs, because the gate sends a call a second time
    /// after renewing a session that came back 401, and a stream read once is empty the second time.
    /// </summary>
    private static async Task<ApiResponse<VRChatFileModel>> SendAsync(
        IVRChat client, byte[] bytes, string name, string type, CancellationToken ct)
    {
        using var content = new MemoryStream(bytes, writable: false);

        return await client.Files.UploadImageWithHttpInfoAsync(
            new FileParameter(name, type, content),
            ImagePurpose.Gallery,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
