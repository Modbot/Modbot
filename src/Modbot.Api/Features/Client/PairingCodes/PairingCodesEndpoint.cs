using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Client.Alerts;
using Modbot.Api.Features.Client.Devices;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Client.PairingCodes;

/// <param name="Code">
/// Handed to the browser once and never stored in a form anybody can read back. The pairing page
/// wraps it, with this server's address, into a <c>modbot-companion://</c> link and a pairing token
/// the moderator's client redeems within minutes.
/// </param>
public sealed record IssuedPairingCode(string Code, DateTimeOffset ExpiresAt);

/// <param name="LastSeenAt">
/// Null means this install has never reported. Surfaced so an operator can see which moderators
/// are actually covering their instances and which installs have gone stale.
/// </param>
public sealed record PairedDevice(
    Guid Id,
    string ClientVersion,
    string Platform,
    DateTimeOffset IssuedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// The staff side of pairing: generate a code, see which devices are reporting, revoke one.
/// </summary>
/// <remarks>
/// <para><strong>Authenticated as a person, never as a device.</strong> A device token must not be
/// able to mint another device token, or a single compromised client could quietly grant itself
/// permanent, unrevocable access under a different name.</para>
/// <para><strong>Installing is a decision by the moderator, not a policy pushed at them.</strong>
/// A group owner can ask; they cannot silently enrol somebody. The code exists so the moderator
/// has to take a deliberate action on their own machine for anything to start reporting.</para>
/// <para><strong>Revocation is immediate and one-sided.</strong> Setting it stops that client at
/// the next request it makes, and the client surfaces the rejection rather than retrying — a
/// revoked moderator's client must stop, and be seen to stop.</para>
/// </remarks>
public static class PairingCodesEndpoint
{
    public static IEndpointRouteBuilder MapPairingCodes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Pairing belongs to the companion's protocol, and API keys are refused here: left out
        // of the public API reference.
        var codes = app.MapGroup("/api/client-devices").WithTags(ClientApi.Tag).ExcludeFromDescription();

        codes.MapPost("/pairing-code", async (
                HttpContext context,
                IClientDeviceStore devices,
                IModbotClock clock,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(context.User) is not { } userId)
                    return Results.Unauthorized();

                // The code carries the moderator who generated it, so the device it becomes has an
                // owner.
                var code = DeviceTokens.NewPairingCode();
                var issued = await devices.IssueCodeAsync(PairingCodeLifetime.Issue(clock, userId, code), ct);

                return Results.Ok(new IssuedPairingCode(code, issued.ExpiresAt));
            })
            .WithName("IssuePairingCode")
            .WithSummary("Generate a one-time code for pairing a Windows client")
            .WithDescription(
                "Short, single-use, and valid for five minutes — long enough to click through "
                + "from the pairing page to the client, short enough that a code left in a "
                + "browser history, a screenshot or a stream is worthless by the time anybody "
                + "sees it.\n\n"
                + "The pairing page wraps it, with this server's address, into a "
                + "modbot-companion:// link and a pairing token. The device token it becomes is long "
                + "and never displayed: a credential a human has to read out or retype ends up "
                + "pasted into a chat message.")
            .Produces<IssuedPairingCode>()
            .RequireAuthorization();

        codes.MapGet("/", async (IClientDeviceStore devices, CancellationToken ct) =>
            {
                var paired = await devices.ListDevicesAsync(ct);

                return Results.Ok(paired
                    .Select(d => new PairedDevice(
                        d.Id, d.ClientVersion, d.Platform, d.IssuedAt, d.LastSeenAt, d.RevokedAt))
                    .ToList());
            })
            .WithName("ListPairedDevices")
            .WithSummary("Which clients are paired, and when each last reported")
            .WithDescription(
                "Never returns a token or a token hash. The list answers two operational "
                + "questions: which moderators are actually reporting, and how much of the "
                + "group's coverage is running a stale build whose log parser may have stopped "
                + "recognising anything.")
            .Produces<IReadOnlyList<PairedDevice>>()
            .RequireAuthorization();

        codes.MapDelete("/{deviceId:guid}", async (
                Guid deviceId,
                IClientDeviceStore devices,
                AlertHub alerts,
                IModbotClock clock,
                CancellationToken ct) =>
            {
                if (!await devices.RevokeAsync(deviceId, clock.UtcNow, ct))
                    return Results.NotFound();

                // Drop anything queued for it. A revoked client will never collect, and holding
                // alerts for it is a small leak of who joined which instance.
                alerts.Forget(deviceId);

                return Results.NoContent();
            })
            .WithName("RevokeClientDevice")
            .WithSummary("Revoke one device token")
            .WithDescription(
                "Immediate: the client is refused at its next request and stops visibly rather "
                + "than retrying.\n\n"
                + "The device record is kept rather than deleted, because the facts it reported "
                + "still point at it — which is what makes a misbehaving client's contributions "
                + "identifiable as a set.\n\n"
                + "One moderator leaving never requires rotating anybody else's token.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        return app;
    }
}
