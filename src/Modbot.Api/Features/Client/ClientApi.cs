using Microsoft.AspNetCore.Builder;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Configuration;
using Modbot.Api.Features.Client.Alerts;
using Modbot.Api.Features.Client.Context;
using Modbot.Api.Features.Client.Devices;
using Modbot.Api.Features.Client.Events;
using Modbot.Api.Features.Client.Pair;
using Modbot.Api.Features.Client.PairingCodes;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Client;

/// <summary>
/// The Windows client's whole surface: pairing, ingest, clock, and the overlay's reads.
/// </summary>
/// <remarks>
/// <para><strong>One folder, one credential type.</strong> Every endpoint here is reached with a
/// device token, which is ingest-scoped: it can submit presence facts and read one group's roster
/// context, and it can do nothing else anywhere in Modbot. A moderator acting on what the overlay
/// showed them goes through the normal authenticated API as themselves. The one exception is
/// issuing a pairing code, which is a staff action in a browser and is authenticated as one.</para>
/// <para><strong>There is no endpoint here that tells a client to do anything.</strong> The server
/// never commands the client; the client observes and reports. That is what keeps the client's
/// behaviour fully described by its own source, which is what its comments promise a suspicious
/// reader.</para>
/// <para><strong>The version is in the path and is checked per request.</strong> It is settled
/// once per pairing rather than negotiated per call, but a server upgraded past a client's range
/// still has to answer — with a <c>409</c> that means "renegotiate", not with failures the client
/// would read as transient and retry forever.</para>
/// </remarks>
public static class ClientApi
{
    public const string Tag = "Client";

    /// <summary>
    /// Registers what the client endpoints need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IClientDeviceStore"/> is <see cref="DatabaseClientDeviceStore"/>, backed by
    /// <c>client_device</c> and <c>client_pairing_code</c>. It replaced an in-memory store that
    /// was correct and deliberately not durable: with that one every redeploy silently unpaired
    /// every moderator, and since a client treats <c>401</c> as terminal and stops rather than
    /// retrying, the symptom was presence data quietly ceasing after each deploy.
    /// </para>
    /// <para>
    /// Scoped rather than singleton, because it holds a <c>ModbotContext</c> for the request.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddClientApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IClientDeviceStore, DatabaseClientDeviceStore>();

        // Singletons because a long poll opened against one request must be woken by an ingest
        // batch arriving on another, and because where each device is standing is shared routing
        // state for that channel rather than anything belonging to a request.
        services.AddSingleton<DeviceLocations>();
        services.AddSingleton<AlertHub>();
        services.AddScoped<DeviceAuthenticator>();

        // The host registers the address from MODBOT_CLOUD_ENDPOINT and MODBOT_CLOUD_DISABLED
        // first; anything that wires the client API without it gets the default.
        services.TryAddSingleton(ModbotCloudAddress.Default);

        return services;
    }

    /// <summary>
    /// Maps the client and overlay endpoints. Call from <c>ApiSurface.MapModbotApi</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapClientApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The desktop client's protocol, not an API for other programs: left out of the public API
        // reference, which documents what an API key can reach. Device tokens are not API keys.
        var client = app.MapGroup("/api/v{apiVersion:int}/client").WithTags(Tag).ExcludeFromDescription();

        client.MapPost("/pair", PairHandler.HandleAsync)
            .WithName("PairClient")
            .WithSummary("Trade a one-time pairing code for a device token")
            .WithDescription(
                "Unauthenticated because the client has no credential yet — the code is the "
                + "credential. It is single-use, expires in minutes, and is issued only to a "
                + "signed-in staff account.\n\n"
                + "Returns the device token once and never again, plus the group this deployment "
                + "manages, so the client can decide locally which events this server may hear "
                + "about, and the server's current time, so its first report is already "
                + "clock-corrected.\n\n"
                + "Expired, already-redeemed and never-existed all return the same 400: telling "
                + "them apart would help only somebody guessing.")
            .Produces<PairResponse>()
            .Produces<ClientError>(StatusCodes.Status400BadRequest)
            .Produces<ClientError>(StatusCodes.Status409Conflict)
            .Produces<ClientError>(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();

        client.MapGet("/time", ([FromRoute] int apiVersion, [FromServices] IModbotClock clock, [FromServices] ModbotCloudAddress cloud) =>
                ClientApiVersion.IsSupported(apiVersion)
                    ? Results.Ok(ServerTimeResponse.Create(clock.UtcNow, cloud))
                    : ClientApiErrors.VersionUnsupported(apiVersion))
            .WithName("GetClientServerTime")
            .WithSummary("The server's current instant, for the client's clock offset")
            .WithDescription(
                "Timed at both ends by the client, which halves the round trip to estimate its "
                + "own offset and reports presence already corrected.\n\n"
                + "This is what makes a five-second deduplication window viable: against raw "
                + "machine clocks the window would have to exceed worst-case skew, which would "
                + "swallow the fifteen-second genuine rejoin it has to preserve.\n\n"
                + "Deliberately anonymous and deliberately trivial. It discloses the time, which "
                + "every HTTP response header already does.\n\n"
                + "Also says where paired clients send their event backup: the Modbot Cloud named by "
                + "MODBOT_CLOUD_ENDPOINT, or nowhere when MODBOT_CLOUD_DISABLED is set. "
                + "instanceId is this deployment's id for Cloud to group clients by, and is null "
                + "until deployments have one.")
            .Produces<ServerTimeResponse>()
            .AllowAnonymous();

        client.MapPost("/events", EventsHandler.HandleAsync)
            .WithName("SubmitClientEvents")
            .WithSummary("Submit a batch of presence observations")
            .WithDescription(
                "Partial acceptance is the normal case, not an error: four to six moderators in "
                + "one instance all see the same join and all report it, so a batch where a "
                + "quarter of the events were already known is a completely successful "
                + "request.\n\n"
                + "Deduplication is a windowed range check across clients; the clientEventId "
                + "handles this client retrying. Neither substitutes for the other.\n\n"
                + "Events naming a group this deployment does not manage are rejected by index "
                + "rather than stored. The client is meant never to have sent them.")
            .Produces<EventBatchResponse>()
            .Produces<ClientError>(StatusCodes.Status400BadRequest)
            .Produces<ClientError>(StatusCodes.Status401Unauthorized)
            .Produces<ClientError>(StatusCodes.Status409Conflict)
            .Produces<ClientError>(StatusCodes.Status413PayloadTooLarge)
            .AllowAnonymous();

        client.MapGet("/context", ContextHandler.ContextAsync)
            .WithName("GetClientInstanceContext")
            .WithSummary("Roster for one instance, with flags and prior-action counts")
            .WithDescription(
                "Fills the overlay's local cache. The overlay renders from that cache and never "
                + "from a live request, so a slow or unreachable server produces stale data with "
                + "its age shown rather than a blank panel.\n\n"
                + "Derived entirely from this deployment's own fact log: no VRChat call is made "
                + "to answer it, so glancing at a roster cannot spend the group's shared API "
                + "budget.")
            .Produces<InstanceContextDto>()
            .Produces<ClientError>(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        client.MapGet("/user/{subjectId}", ContextHandler.UserAsync)
            .WithName("GetClientUserSummary")
            .WithSummary("Profile summary for one person, sized for a headset card")
            .WithDescription(
                "Deliberately not the full web profile: prior actions, roles, join date and "
                + "current flags, and nothing that needs scrolling in a headset.\n\n"
                + "Somebody with nothing on record is a successful, empty answer rather than a "
                + "404.")
            .Produces<UserSummaryDto>()
            .Produces<ClientError>(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        client.MapGet("/alerts", AlertsEndpoint.WaitAsync)
            .WithName("WaitForClientAlert")
            .WithSummary("Long poll: a flagged user joined this moderator's instance")
            .WithDescription(
                "The only push in the protocol, and the only case that earns it — by the time a "
                + "thirty-second poll notices, the moment has passed.\n\n"
                + "A long poll rather than a websocket: one endpoint, one concern, and it "
                + "degrades to a slow poll rather than to nothing when a proxy or captive portal "
                + "interferes.\n\n"
                + "Answers 204 when the wait expires with nothing to say, which is the ordinary "
                + "outcome by a long way. The client treats it as such and does not back off.")
            .Produces<FlaggedJoinAlertDto>()
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ClientError>(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        // The staff side of pairing: a signed-in moderator generating a code, seeing which of
        // their devices are reporting, and revoking one. Cookie-authenticated as a person, because
        // a device token must never be able to mint another device token.
        app.MapPairingCodes();

        return app;
    }
}

/// <param name="ServerTime">RFC 3339 with an explicit offset, like every timestamp in this API.</param>
/// <param name="Cloud">Where paired clients send their event backup (cloud event backup spec 3.1).</param>
/// <param name="InstanceId">
/// This deployment's id, which the client passes on to Cloud so installs can be grouped by server.
/// Null: Modbot deployments do not have one yet.
/// </param>
public sealed record ServerTimeResponse(
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("cloud")] ServerCloudResponse Cloud,
    [property: JsonPropertyName("instanceId")] string? InstanceId)
{
    public static ServerTimeResponse Create(DateTimeOffset now, ModbotCloudAddress cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        return new ServerTimeResponse(
            now,
            new ServerCloudResponse(cloud.Disabled ? null : cloud.Endpoint.ToString(), cloud.Disabled),
            InstanceId: null);
    }
}

/// <param name="Endpoint">The Cloud to send to, or null when <see cref="Disabled"/>.</param>
/// <param name="Disabled">Paired clients must send no event backup at all.</param>
public sealed record ServerCloudResponse(
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("disabled")] bool Disabled);
