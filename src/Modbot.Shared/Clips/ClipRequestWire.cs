namespace Modbot.Shared.Clips;

/// <summary>
/// What the server asks a companion for a clip with, and how the companion answers, kept where
/// both read them (clip delivery, 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// The request goes server to companion only: down the live connection as <see cref="Kind"/>, and in
/// the long-poll answer's <c>clipRequests</c> list. The same request can arrive more than once (at
/// connect, and again from a poll); the companion treats a repeated <c>requestId</c> as the one request.
/// </para>
/// <para>
/// The companion answers by <c>POST .../companion/clip-requests/{requestId}/clip</c> with the file as
/// the raw body, or by <c>POST .../companion/clip-requests/{requestId}/answer</c> with a
/// <see cref="ClipRequestAnswer"/> when it has no file to send. Either is accepted once, from the
/// device that was asked, inside <see cref="ClipRequestMessage.ExpiresAt"/>.
/// </para>
/// </remarks>
public static class ClipRequestWire
{
    /// <summary>The <c>kind</c> of the live message that carries a request.</summary>
    public const string Kind = "clip_request";

    public const string Ban = "ban";
    public const string Kick = "kick";
    public const string Warning = "warning";

    /// <summary>The companion's Clips was off, or "Send clips for my actions automatically" was not ticked.</summary>
    public const string ClipsOff = "clips_off";

    /// <summary>The companion could not cut a clip, and the whole recording was too big to send.</summary>
    public const string CouldNotCut = "could_not_cut";

    /// <summary>The companion had a clip and could not get it to the server.</summary>
    public const string UploadFailed = "upload_failed";
}

/// <summary>One request for a clip.</summary>
/// <param name="Kind">Always <see cref="ClipRequestWire.Kind"/>.</param>
/// <param name="RequestId">What the answer names.</param>
/// <param name="Action"><see cref="ClipRequestWire.Ban"/>, <see cref="ClipRequestWire.Kick"/> or <see cref="ClipRequestWire.Warning"/>.</param>
/// <param name="OccurredAt">The moment of the action: VRChat's time, or Modbot's own for one made in Modbot.</param>
/// <param name="ExpiresAt">After this the request is closed and an answer is refused.</param>
public sealed record ClipRequestMessage(
    string Kind,
    Guid RequestId,
    string Action,
    DateTimeOffset OccurredAt,
    DateTimeOffset ExpiresAt);

/// <summary>The body of an answer that carries no file. <c>Outcome</c> is one of the <see cref="ClipRequestWire"/> answers.</summary>
public sealed record ClipRequestAnswer(string Outcome);
