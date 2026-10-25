using Modbot.Companion.Ingest;

namespace Modbot.Companion.Clips;

/// <summary>How an upload or an answer ended.</summary>
public enum ClipUploadResult
{
    /// <summary>The server took it.</summary>
    Accepted,

    /// <summary>The server says the request is not open (closed, answered, or not this device's). Nothing more to do.</summary>
    NotOpen,

    /// <summary>It did not get through: no connection, a server error or a refusal.</summary>
    Failed,
}

/// <summary>Sends the clip for a request, or the word that there is none.</summary>
public interface IClipUploader
{
    /// <summary>Sends the file at <paramref name="path"/> as the answer to one request.</summary>
    Task<ClipUploadResult> UploadAsync(ServerPairing pairing, Guid requestId, string path, CancellationToken ct);

    /// <summary>Answers one request with a word instead of a file (see <c>ClipRequestWire</c>).</summary>
    Task<ClipUploadResult> AnswerAsync(ServerPairing pairing, Guid requestId, string outcome, CancellationToken ct);
}
