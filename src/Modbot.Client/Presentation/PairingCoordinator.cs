using Modbot.Client.Ingest;
using Modbot.Client.Pairing;

namespace Modbot.Client.Presentation;

/// <summary>What the window shows after a pairing attempt.</summary>
/// <param name="Succeeded">Whether a token was obtained and stored.</param>
/// <param name="Message">
/// One sentence for the moderator. Every failure gets one: a pairing that fails quietly and
/// retries in the background is exactly the behaviour this program promises not to have.
/// </param>
public sealed record PairingAttemptResult(bool Succeeded, string Message, ServerPairing? Pairing = null);

/// <summary>
/// Pairing, from the window's point of view: a pairing token arrives, and either a server is added
/// or the moderator is told why not.
/// </summary>
/// <remarks>
/// <para><strong>Where tokens come from.</strong> The moderator presses "Open in Modbot" on their
/// group's pairing page and the browser hands this program a <c>modbot-client://</c> link; or they
/// copy the token from that page and paste it into the window. Both are the same bytes and both
/// arrive here, through <see cref="PairAsync(string, CancellationToken)"/>. Nothing has to be
/// typed, which is the point: the old flow asked somebody already in a headset to copy a server
/// address and an eight-character code from one screen to another.</para>
/// <para><strong>Separate from the window so the wording can be tested.</strong> What a moderator
/// is told when pairing fails is the whole of their experience of this feature, and "that did not
/// work" is not an answer anybody can act on.</para>
/// <para><strong>One attempt per token. Nothing retries by itself.</strong> Codes are single-use,
/// so a background retry would burn the moderator's code against a server that already refused it,
/// and a <c>401</c> means the operator revoked them — which must stop, visibly, rather than
/// becoming a loop nobody sees.</para>
/// <para><strong>Nothing is stored until a token comes back.</strong> A half-written pairing would
/// leave the client reporting to a server it never actually joined.</para>
/// </remarks>
public sealed class PairingCoordinator
{
    private readonly IPairingClient _client;
    private readonly IPairingStore _store;

    public PairingCoordinator(IPairingClient client, IPairingStore store)
    {
        _client = client;
        _store = store;
    }

    /// <summary>
    /// Pairs from a pairing link or a pasted pairing token. Anything wrong with the text is
    /// explained without a request being made anywhere.
    /// </summary>
    public Task<PairingAttemptResult> PairAsync(string linkOrToken, CancellationToken cancellationToken = default)
    {
        if (!PairingToken.TryParse(linkOrToken, out var token, out var problem))
            return Task.FromResult(new PairingAttemptResult(false, problem));

        return PairAsync(token!, cancellationToken);
    }

    /// <summary>Pairs one server: trades the token's code for a device token, and stores it.</summary>
    public async Task<PairingAttemptResult> PairAsync(PairingToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        // The local label is the server's host (and port, when it is not the default), which is
        // what the moderator will recognise in the list. It is never sent anywhere.
        var serverId = token.Server.Authority;

        var result = await _client.PairAsync(
            new PairingAttempt(token.Server, token.Code, serverId),
            cancellationToken).ConfigureAwait(false);

        if (result is { Outcome: PairingOutcome.Paired, Pairing: { } pairing })
        {
            // Stored only now, once there is something worth storing.
            _store.Save(pairing);
            return new PairingAttemptResult(
                true,
                $"Paired with {serverId}. Modbot will report presence for that group's instances "
                + "and nothing else.",
                pairing);
        }

        return new PairingAttemptResult(false, Explain(result, token.Server));
    }

    /// <summary>Removes a pairing, and with it the token and anything queued for that server.</summary>
    /// <remarks>
    /// Uninstalling or unpairing stops reporting without the group's operator having to do
    /// anything, which is what makes consent revocable from the moderator's side too.
    /// </remarks>
    public void Unpair(string serverId) => _store.Remove(serverId);

    public IReadOnlyList<LoadedPairing> Load() => _store.Load();

    /// <summary>
    /// What to tell the moderator, in a sentence that names the next thing to do.
    /// </summary>
    /// <remarks>
    /// The distinctions here are the ones a moderator can act on, and they are genuinely different
    /// actions: get a new link, update something, check where the link came from, or wait.
    /// Collapsing them into one message would make three of the four unfixable.
    /// </remarks>
    private static string Explain(PairingResult result, Uri server) => result.Outcome switch
    {
        PairingOutcome.CodeRejected =>
            "This pairing link has expired or was already used. Pairing links work once, for a "
            + "few minutes — open the pairing page again and use the new one.",

        PairingOutcome.Unauthorised =>
            $"{server.Authority} refused this pairing. If you have been removed from that group's "
            + "staff, that is expected and there is nothing to fix here.",

        PairingOutcome.VersionUnsupported =>
            result.Detail
            ?? "This client and that server do not speak a common API version. One of them needs updating.",

        PairingOutcome.NotAModbotServer =>
            $"{server} did not answer like a Modbot server. Check the address — the pairing page "
            + "should be opened at the same address you use to open Modbot in a browser.",

        _ => $"Could not reach {server.Authority}. Check your connection and try again; nothing has "
            + "been changed.",
    };
}
