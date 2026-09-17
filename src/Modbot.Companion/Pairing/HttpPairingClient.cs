using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Pairing;

/// <summary>Turns a server address and a one-time code into a stored pairing.</summary>
public interface IPairingClient
{
    Task<PairingResult> PairAsync(PairingAttempt attempt, CancellationToken cancellationToken);
}

/// <param name="BaseUri">
/// Where the group's Modbot lives, as the pairing token named it. HTTPS, or plain HTTP only to
/// this machine (<see cref="ServerAddresses"/>).
/// </param>
/// <param name="Code">
/// The short, single-use, expiring code the pairing page put in the token. It is used once and
/// never stored; what is stored is the device token it is exchanged for.
/// </param>
/// <param name="ServerId">A local label, for the moderator's benefit. Never sent anywhere.</param>
public readonly record struct PairingAttempt(Uri BaseUri, string Code, string ServerId);

/// <summary>
/// The pairing exchange: <c>GET /api/version</c> to agree a version, then
/// <c>POST /api/v{n}/companion/pair</c> to trade a one-time code for a device token.
/// </summary>
/// <remarks>
/// <para><strong>What this sends, and where.</strong> Two requests, both to the one address the
/// pairing token named. The first is unauthenticated and carries nothing at all. The second
/// carries three things and no more: the pairing code, the client's own version, and the string
/// <c>windows</c>. It does not send the machine name, a name for this device, the Windows account
/// name, a hardware identifier, the VRChat account, a list of the moderator's other paired
/// servers, or anything read from the log.</para>
/// <para><strong>What comes back</strong> is a device token, the group the server manages, and the
/// server's current time. The token is ingest-scoped — stolen, it can submit presence facts and
/// nothing else; it cannot read the member list, read a profile, or ban anybody. The group id is
/// what lets the client decide locally which events this server may hear about, so it never has to
/// ask a server "do you own this instance?" — asking is itself the leak that routing exists to
/// prevent.</para>
/// <para><strong>The code is never written to disk.</strong> Only the token it is exchanged for
/// is, and that is encrypted to the current Windows user.</para>
/// </remarks>
public sealed class HttpPairingClient : IPairingClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ApiVersionRange _supported;

    public HttpPairingClient(HttpClient http, ApiVersionRange? supported = null)
    {
        _http = http;
        _supported = supported ?? ApiVersionRange.Client;
    }

    public async Task<PairingResult> PairAsync(PairingAttempt attempt, CancellationToken cancellationToken)
    {
        if (!ServerAddresses.IsAllowed(attempt.BaseUri))
        {
            // Refused rather than warned about. A warning that can be clicked past is a warning
            // that will be, and presence data in clear text over a café network is not a checkbox.
            return PairingResult.Failed(PairingOutcome.NotAModbotServer, ServerAddresses.Refusal(attempt.BaseUri));
        }

        var negotiated = await NegotiateAsync(attempt.BaseUri, cancellationToken).ConfigureAwait(false);
        if (negotiated.Outcome is not PairingOutcome.Paired)
            return negotiated;

        var apiVersion = negotiated.Pairing!.ApiVersion;
        var endpoint = new Uri(attempt.BaseUri, $"/api/v{apiVersion}/companion/pair");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new PairRequestBody(attempt.Code, Core.ModbotVersion.Release, "windows"),
                options: Json),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return PairingResult.Failed(PairingOutcome.NetworkFailure, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PairingResult.Failed(PairingOutcome.NetworkFailure, "The server did not answer in time.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Rejected(response.StatusCode, body);

            PairResponseBody? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<PairResponseBody>(body, Json);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is not { DeviceToken.Length: > 0, ManagedGroupId.Length: > 0 })
            {
                return PairingResult.Failed(
                    PairingOutcome.NotAModbotServer,
                    "The server accepted the code but did not return a device token and a managed group.");
            }

            var pairing = new ServerPairing(
                attempt.ServerId,
                attempt.BaseUri,
                parsed.DeviceToken,
                parsed.ManagedGroupId,
                apiVersion)
            {
                ManagedGroupName = string.IsNullOrWhiteSpace(parsed.ManagedGroupName) ? null : parsed.ManagedGroupName.Trim(),
                ManagedGroupIconUrl = string.IsNullOrWhiteSpace(parsed.ManagedGroupIconUrl) ? null : parsed.ManagedGroupIconUrl.Trim(),
            };

            return new PairingResult(PairingOutcome.Paired, pairing, parsed.ServerTime);
        }
    }

    /// <summary>
    /// Reads the server's supported range from its unauthenticated version endpoint and picks the
    /// highest version both speak. Carries the result in a throwaway pairing so the caller has one
    /// shape to branch on; only <see cref="ServerPairing.ApiVersion"/> is meaningful here.
    /// </summary>
    private async Task<PairingResult> NegotiateAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(baseUri, "/api/version");

        try
        {
            using var response = await _http.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return PairingResult.Failed(
                    PairingOutcome.NotAModbotServer,
                    $"{baseUri} answered {(int)response.StatusCode} when asked for its API version.");
            }

            var reported = JsonSerializer.Deserialize<VersionBody>(body, Json);
            if (reported is not { ApiVersion: > 0 })
            {
                return PairingResult.Failed(
                    PairingOutcome.NotAModbotServer,
                    $"{baseUri} did not answer with a Modbot version document.");
            }

            var server = new ApiVersionRange(reported.ApiVersionMinimum, reported.ApiVersion);
            if (_supported.HighestInCommonWith(server) is not { } agreed)
            {
                // Both numbers, always. "Update Modbot" and "update your client" are opposite
                // instructions and the moderator cannot guess which one they need.
                return PairingResult.Failed(
                    PairingOutcome.VersionUnsupported,
                    $"This client speaks API {_supported} and {baseUri} speaks {server}. "
                    + (server.Maximum > _supported.Maximum
                        ? "Update the client."
                        : "The server is older than this client supports; the group's operator must update Modbot."));
            }

            return new PairingResult(
                PairingOutcome.Paired,
                new ServerPairing("negotiation", baseUri, "unused", "unused", agreed));
        }
        catch (HttpRequestException ex)
        {
            return PairingResult.Failed(PairingOutcome.NetworkFailure, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PairingResult.Failed(PairingOutcome.NetworkFailure, "The server did not answer in time.");
        }
        catch (JsonException)
        {
            return PairingResult.Failed(
                PairingOutcome.NotAModbotServer,
                $"{baseUri} answered with something that is not a Modbot version document.");
        }
    }

    private static PairingResult Rejected(HttpStatusCode status, string body)
    {
        var code = ReadCode(body);

        var outcome = status switch
        {
            HttpStatusCode.Unauthorized => PairingOutcome.Unauthorised,
            HttpStatusCode.Forbidden => PairingOutcome.Unauthorised,
            HttpStatusCode.Conflict => PairingOutcome.VersionUnsupported,
            HttpStatusCode.BadRequest => PairingOutcome.CodeRejected,
            HttpStatusCode.NotFound => PairingOutcome.CodeRejected,
            HttpStatusCode.Gone => PairingOutcome.CodeRejected,
            _ => PairingOutcome.NetworkFailure,
        };

        return PairingResult.Failed(outcome, code is null ? $"The server answered {(int)status}." : $"{code} ({(int)status})");
    }

    private static string? ReadCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PairRequestBody(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("companionVersion")] string CompanionVersion,
        [property: JsonPropertyName("platform")] string Platform);

    private sealed record PairResponseBody(
        [property: JsonPropertyName("deviceToken")] string DeviceToken,
        [property: JsonPropertyName("managedGroupId")] string ManagedGroupId,
        [property: JsonPropertyName("managedGroupName")] string? ManagedGroupName,
        [property: JsonPropertyName("managedGroupIconUrl")] string? ManagedGroupIconUrl,
        [property: JsonPropertyName("serverTime")] DateTimeOffset? ServerTime);

    private sealed record VersionBody(
        [property: JsonPropertyName("apiVersion")] int ApiVersion,
        [property: JsonPropertyName("apiVersionMinimum")] int ApiVersionMinimum);
}
