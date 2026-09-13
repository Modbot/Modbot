using Modbot.Client.Pairing;

namespace Modbot.Client.Ingest;

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

    /// <summary>Negotiated once, at pairing. A property of the pairing, not of each request.</summary>
    public int ApiVersion { get; init; }

    public Uri EventsEndpoint => new(BaseUri, $"/api/v{ApiVersion}/client/events");

    public Uri TimeEndpoint => new(BaseUri, $"/api/v{ApiVersion}/client/time");

    /// <summary>
    /// The roster-with-context read the overlay's local cache is filled from. Read-only and small.
    /// </summary>
    public Uri ContextEndpoint(string instanceId)
        => new(BaseUri, $"/api/v{ApiVersion}/client/context?instanceId={Uri.EscapeDataString(instanceId)}");

    /// <summary>One person's profile summary — prior actions, roles, join date, current flags.</summary>
    public Uri UserEndpoint(string subjectId)
        => new(BaseUri, $"/api/v{ApiVersion}/client/user/{Uri.EscapeDataString(subjectId)}");

    /// <summary>
    /// The long poll. The one case that genuinely needs push: a flagged user joining the instance
    /// the moderator is standing in, where a thirty-second poll notices after the moment has gone.
    /// </summary>
    public Uri AlertsEndpoint(int waitSeconds)
        => new(BaseUri, $"/api/v{ApiVersion}/client/alerts?wait={waitSeconds}");

    public override string ToString() => $"{ServerId} ({BaseUri})";
}
