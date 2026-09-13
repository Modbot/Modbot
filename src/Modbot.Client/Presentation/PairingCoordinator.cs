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
/// Pairing, from the window's point of view: type a code, get a server, or get told why not.
/// </summary>
/// <remarks>
/// <para><strong>Separate from the window so the wording can be tested.</strong> What a moderator
/// is told when pairing fails is the whole of their experience of this feature, and "that did not
/// work" is not an answer anybody can act on.</para>
/// <para><strong>One attempt per press. Nothing retries by itself.</strong> Codes are single-use,
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
    /// Pairs one server. <paramref name="address"/> is what the moderator typed, which may be a
    /// bare host name.
    /// </summary>
    public async Task<PairingAttemptResult> PairAsync(
        string address,
        string code,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new PairingAttemptResult(false, "Enter the address of the Modbot server you are pairing with.");

        if (string.IsNullOrWhiteSpace(code))
            return new PairingAttemptResult(false, "Enter the pairing code from that group's Modbot settings page.");

        if (!TryNormaliseAddress(address, out var baseUri))
        {
            return new PairingAttemptResult(
                false,
                $"“{address.Trim()}” is not an address Modbot can reach. It should look like "
                + "modbot.example.com.");
        }

        var serverId = string.IsNullOrWhiteSpace(deviceName) ? baseUri.Host : baseUri.Host;

        var result = await _client.PairAsync(
            new PairingAttempt(baseUri, code.Trim(), serverId, DeviceLabel(deviceName)),
            cancellationToken).ConfigureAwait(false);

        if (result is { Outcome: PairingOutcome.Paired, Pairing: { } pairing })
        {
            // Stored only now, once there is something worth storing.
            _store.Save(pairing);
            return new PairingAttemptResult(
                true,
                $"Paired with {baseUri.Host}. Modbot will report presence for that group's instances "
                + "and nothing else.",
                pairing);
        }

        return new PairingAttemptResult(false, Explain(result, baseUri));
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
    /// actions: get a new code, update something, check the address, or wait. Collapsing them into
    /// one message would make three of the four unfixable.
    /// </remarks>
    private static string Explain(PairingResult result, Uri baseUri) => result.Outcome switch
    {
        PairingOutcome.CodeRejected =>
            "That pairing code was not accepted. Codes are single-use and expire after a few "
            + "minutes — generate a fresh one in that group's Modbot settings and try again.",

        PairingOutcome.Unauthorised =>
            $"{baseUri.Host} refused this pairing. If you have been removed from that group's "
            + "staff, that is expected and there is nothing to fix here.",

        PairingOutcome.VersionUnsupported =>
            result.Detail
            ?? "This client and that server do not speak a common API version. One of them needs updating.",

        PairingOutcome.NotAModbotServer =>
            $"{baseUri} did not answer like a Modbot server. Check the address — it should be the "
            + "same one you use to open Modbot in a browser.",

        _ => $"Could not reach {baseUri.Host}. Check your connection and try again; nothing has "
            + "been changed.",
    };

    /// <summary>
    /// Turns what a moderator typed into an address.
    /// </summary>
    /// <remarks>
    /// <para>A bare host name gets <c>https://</c>, because that is what people type and refusing
    /// it teaches nothing. Plain <c>http://</c> typed deliberately is refused rather than silently
    /// upgraded: presence data crossing a café network in clear text is not a thing to fix quietly,
    /// and a client that rewrote the scheme would be hiding the very decision worth showing.</para>
    /// </remarks>
    public static bool TryNormaliseAddress(string typed, out Uri baseUri)
    {
        baseUri = null!;
        var trimmed = typed.Trim().TrimEnd('/');

        if (trimmed.Length == 0)
            return false;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out baseUri!)
            && baseUri.Scheme == Uri.UriSchemeHttps
            && baseUri.Host.Length > 0;
    }

    /// <summary>
    /// The name this install is called in the operator's settings page.
    /// </summary>
    /// <remarks>
    /// The moderator types it. The client does not read the machine name and send it unasked:
    /// a hostname is often a person's real name, and it is not this program's to disclose.
    /// </remarks>
    private static string DeviceLabel(string deviceName)
        => string.IsNullOrWhiteSpace(deviceName) ? "Unnamed device" : deviceName.Trim();
}
