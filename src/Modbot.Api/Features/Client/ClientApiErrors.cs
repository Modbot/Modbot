using Microsoft.AspNetCore.Http;
using Modbot.Core;

namespace Modbot.Api.Features.Client;

/// <param name="Code">
/// Stable and machine-readable. The client branches on this and never on
/// <paramref name="Message"/>, which is for people and will be reworded.
/// </param>
public sealed record ClientError(string Code, string Message);

/// <summary>
/// The error table from the client protocol, in one place, so a status and a code cannot drift
/// apart between endpoints.
/// </summary>
/// <remarks>
/// Each status means one thing to the client, and they are not interchangeable: <c>400</c> tells
/// it to drop a batch it will never be able to send, <c>401</c> tells it to stop this pairing
/// visibly, <c>409</c> tells it to renegotiate, and <c>5xx</c> tells it to keep buffering. Getting
/// one wrong turns a permanent failure into an infinite retry or the reverse.
/// </remarks>
public static class ClientApiErrors
{
    public const string PairingCodeInvalid = "pairing_code_invalid";

    public const string DeviceTokenInvalid = "device_token_invalid";

    public const string ApiVersionUnsupported = "api_version_unsupported";

    public const string BatchMalformed = "batch_malformed";

    public const string BatchTooLarge = "batch_too_large";

    public const string NotConfigured = "not_configured";

    /// <summary>
    /// <c>400</c>. The batch is wrong and will be wrong every time, so the client drops it. A
    /// retry loop on a permanent error is how an offline buffer fills forever and stops reporting
    /// anything at all.
    /// </summary>
    public static IResult Malformed(string message)
        => Results.BadRequest(new ClientError(BatchMalformed, message));

    /// <summary>
    /// <c>401</c>. Terminal for this pairing: the client stops and tells the moderator rather than
    /// retrying, because a revoked moderator's client must stop and be seen to stop.
    /// </summary>
    public static IResult Unauthorised()
        => Results.Json(
            new ClientError(DeviceTokenInvalid, "This device token is not valid for this server."),
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// <c>409</c>, not a <c>4xx</c> the client would read as permanent and not a <c>5xx</c> it
    /// would read as transient. It means "renegotiate", and it is what a server returns after
    /// being upgraded past a client's range.
    /// </summary>
    public static IResult VersionUnsupported(int requested)
        => Results.Json(
            new ClientError(
                ApiVersionUnsupported,
                $"This server speaks API versions {ModbotVersion.ApiMinimum} to {ModbotVersion.Api}; "
                + $"the request asked for {requested}."),
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// <c>503</c>. Onboarding has not finished, so there is no managed group to pair against yet.
    /// Transient by nature — the operator is mid-setup — so the client backs off rather than
    /// treating the address as wrong.
    /// </summary>
    public static IResult NotReady()
        => Results.Json(
            new ClientError(
                NotConfigured,
                "This Modbot has not finished onboarding and does not yet manage a group."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
