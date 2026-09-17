using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Core.Data;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Companion.Pair;

/// <param name="Code">
/// The short, single-use code the moderator's browser handed to the client through the pairing
/// link, or that they pasted in as a pairing token.
/// </param>
/// <remarks>
/// There is no device name. Older clients still send one and it is ignored: a label the moderator
/// typed for their own machine told the operator nothing they could act on, and every question a
/// settings list is asked is answered by whose device it is, its platform, its version and when
/// it last reported.
/// </remarks>
public sealed record PairRequest(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("companionVersion")] string? CompanionVersion,
    [property: JsonPropertyName("platform")] string? Platform);

/// <param name="DeviceToken">
/// Long, and shown exactly once — here, to the client that asked for it. It is never displayed to
/// a human and never retrievable again; a lost token is re-paired, not recovered.
/// </param>
/// <param name="ManagedGroupId">
/// Sent so the client can decide locally which events this server may hear about. Without it the
/// client would have to ask some server "do you own this instance?", and asking the wrong one is
/// exactly the cross-group leak that local routing exists to prevent.
/// </param>
/// <param name="ServerTime">Seeds the clock offset, so the client's first report is already corrected.</param>
public sealed record PairResponse(
    [property: JsonPropertyName("deviceToken")] string DeviceToken,
    [property: JsonPropertyName("managedGroupId")] string ManagedGroupId,
    [property: JsonPropertyName("managedGroupName")] string ManagedGroupName,
    [property: JsonPropertyName("managedGroupIconUrl")] string? ManagedGroupIconUrl,
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime);

/// <summary>
/// Trades a pairing code for a device token.
/// </summary>
/// <remarks>
/// <para><strong>Unauthenticated, because the client has no credential yet.</strong> The code is
/// the credential: single-use, minutes-long, and issued only to a signed-in staff account. Every
/// way it can fail returns the same answer, because distinguishing "expired" from "already used"
/// from "never existed" helps only somebody guessing.</para>
/// <para><strong>The device inherits the moderator who issued the code</strong>, which is how
/// "whose client is this" is answered, and what revoking a moderator's access cascades from.</para>
/// </remarks>
public static class PairHandler
{
    /// <summary>Long enough for any real version or platform string, short enough not to be a storage surface.</summary>
    public const int MaxFieldLength = 32;

    public static async Task<IResult> HandleAsync(
        int apiVersion,
        PairRequest? request,
        ICompanionDeviceStore devices,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        // A missing code answers exactly like a wrong one. Every way this can fail looks the same
        // from outside, which is what stops the endpoint being useful to somebody guessing.
        var typed = request?.Code ?? string.Empty;

        var settings = await database.GetSettingsAsync(ct);
        if (settings.ManagedGroupId is not { Length: > 0 } managedGroupId)
            return CompanionApiErrors.NotReady();

        var code = typed.Length > 0
            ? await devices.TryRedeemCodeAsync(
                DeviceTokens.Hash(DeviceTokens.NormaliseCode(typed)), clock.UtcNow, ct)
            : null;

        if (code is null)
        {
            // One answer for expired, already-redeemed and never-existed. The client shows it as
            // "that code was not accepted"; the moderator generates another one, which costs them
            // a few seconds and costs a guesser everything.
            return Results.Json(
                new CompanionError(
                    CompanionApiErrors.PairingCodeInvalid,
                    "That pairing code is not valid. Generate a new one in Modbot's settings."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var token = DeviceTokens.NewToken();
        await devices.AddDeviceAsync(
            new CompanionDevice(
                Guid.NewGuid(),
                DeviceTokens.Hash(token),
                Clean(request?.CompanionVersion) ?? "unknown",
                Clean(request?.Platform) ?? "unknown",
                code.IssuedToUserId,
                clock.UtcNow),
            ct);

        // The group's name and icon, so the companion can show the moderator which community
        // this server is rather than its address. Both come from what VRChat last said about the
        // group; the id is still what decides which events this server hears.
        return Results.Ok(new PairResponse(
            token,
            managedGroupId,
            settings.ManagedGroupName ?? managedGroupId,
            settings.ManagedGroupIconUrl,
            clock.UtcNow));
    }

    /// <summary>
    /// Trims and bounds a caller-supplied string.
    /// </summary>
    /// <remarks>
    /// The version and platform are sent by a client and displayed to an operator in a settings
    /// list, so they are hostile input like any other. Control characters go because they break
    /// the list; <em>format</em> characters go because a right-to-left override makes one row
    /// render as another, which is how a compromised client would hide behind a colleague's entry
    /// when somebody went looking for which one to revoke. Bounded to the column width so a
    /// malicious value is truncated rather than turned into a database error.
    /// </remarks>
    private static string? Clean(string? value)
    {
        if (value is null)
            return null;

        var cleaned = new string([.. value.Where(c =>
            !char.IsControl(c)
            && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.Format)]).Trim();

        return cleaned.Length switch
        {
            0 => null,
            > MaxFieldLength => cleaned[..MaxFieldLength],
            _ => cleaned,
        };
    }
}
