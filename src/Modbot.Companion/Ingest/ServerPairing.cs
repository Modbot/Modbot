using Modbot.Companion.Pairing;

namespace Modbot.Companion.Ingest;

/// <summary>
/// One paired Modbot server: where it is, the token that talks to it, and the one group it manages.
/// </summary>
/// <remarks>
/// <para><strong>One of these per server, per device.</strong> A moderator staffing two groups
/// pairs the same client twice and holds two unrelated tokens; neither group's operator learns of
/// the other.</para>
/// <para><strong>The token is ingest-scoped.</strong> Stolen, it can submit presence facts and
/// nothing else — it cannot read the member list, cannot read a profile, and cannot ban anyone. A
/// moderator acting from the overlay goes through the normal API as themselves.</para>
/// <para><strong>Nothing here is transmitted except the token</strong>, as a bearer header, to the
/// one server it belongs to.</para>
/// </remarks>
public sealed record ServerPairing
{
    public ServerPairing(
        string serverId,
        Uri baseUri,
        string deviceToken,
        string managedGroupId,
        int apiVersion = 1)
    {
        // Plain HTTP is refused rather than warned about, except to this machine itself. Presence
        // data crossing a home network, a café, or a captive portal in clear text is not a risk
        // worth a checkbox, and a warning that can be clicked past is a warning that will be.
        if (!ServerAddresses.IsAllowed(baseUri))
            throw new ArgumentException(ServerAddresses.Refusal(baseUri), nameof(baseUri));

        ServerId = serverId;
        BaseUri = baseUri;
        DeviceToken = deviceToken;
        ManagedGroupId = managedGroupId;
        ApiVersion = apiVersion;
    }

    /// <summary>A local label for the moderator's benefit. Never sent anywhere.</summary>
    public string ServerId { get; }

    public Uri BaseUri { get; }

    /// <summary>
    /// Held in memory here; stored at rest with DPAPI under the current user, never in plaintext.
    /// Revocation is server-side and immediate — a 401 stops this pairing visibly rather than
    /// being retried, because a revoked moderator's client must stop, and be seen to stop.
    /// </summary>
    public string DeviceToken { get; }

    /// <summary>
    /// The group this server declared at pairing. Routing compares against it locally, which is why
    /// the client never has to ask anybody which group an instance belongs to.
    /// </summary>
    public string ManagedGroupId { get; }

    /// <summary>The group's name as the server gave it at pairing, or null for a pairing made before servers said.</summary>
    public string? ManagedGroupName { get; init; }

    /// <summary>The group's icon, as the server gave it at pairing, or null.</summary>
    public string? ManagedGroupIconUrl { get; init; }

    /// <summary>What to call this server on a screen: the group's name, or its id until a name is known.</summary>
    public string GroupLabel => string.IsNullOrWhiteSpace(ManagedGroupName) ? ManagedGroupId : ManagedGroupName;

    /// <summary>
    /// What the overlay calls this community: the group's name, falling back to the server's
    /// address.
    /// </summary>
    /// <remarks>
    /// A moderator knows their community by its name, not by the address of the machine their
    /// server runs on and not by <c>grp_</c> and thirty characters. The overlay used to be handed
    /// <see cref="ServerId"/>, which is whatever local label the pairing was saved under and is in
    /// practice the address — so the panel over VRChat said a hostname where it should have said
    /// the group. The address is what is left when a pairing was made before servers gave their
    /// group's name, and it is at least something a moderator can recognise.
    /// </remarks>
    public string OverlayLabel => string.IsNullOrWhiteSpace(ManagedGroupName) ? BaseUri.Authority : ManagedGroupName;

    /// <summary>Negotiated once, at pairing. A property of the pairing, not of each request.</summary>
    public int ApiVersion { get; init; }

    public Uri EventsEndpoint => new(BaseUri, $"/api/v{ApiVersion}/companion/events");

    public Uri TimeEndpoint => new(BaseUri, $"/api/v{ApiVersion}/companion/time");

    /// <summary>
    /// The roster-with-context read the overlay's local cache is filled from. Read-only and small.
    /// </summary>
    public Uri ContextEndpoint(string instanceId)
        => new(BaseUri, $"/api/v{ApiVersion}/companion/context?instanceId={Uri.EscapeDataString(instanceId)}");

    /// <summary>One person's profile summary — prior actions, roles, join date, current flags.</summary>
    public Uri UserEndpoint(string subjectId)
        => new(BaseUri, $"/api/v{ApiVersion}/companion/user/{Uri.EscapeDataString(subjectId)}");

    /// <summary>
    /// Live updates over a WebSocket: who joins and leaves the instance the moderator is standing
    /// in, flagged joins included. <paramref name="after"/> is the cursor to carry on from.
    /// </summary>
    public Uri LiveSocketEndpoint(string instanceId, string? after)
    {
        var scheme = BaseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var builder = new UriBuilder(BaseUri) { Scheme = scheme, Path = $"/api/v{ApiVersion}/companion/ws", Query = Query(instanceId, after) };
        return builder.Uri;
    }

    /// <summary>The same updates by long polling, the backup for the WebSocket.</summary>
    public Uri LivePollEndpoint(string instanceId, string? after, int waitSeconds)
        => new(BaseUri, $"/api/v{ApiVersion}/companion/poll?{Query(instanceId, after)}&wait={waitSeconds}");

    private static string Query(string instanceId, string? after)
        => $"instanceId={Uri.EscapeDataString(instanceId)}" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");

    public override string ToString() => $"{ServerId} ({BaseUri})";
}
