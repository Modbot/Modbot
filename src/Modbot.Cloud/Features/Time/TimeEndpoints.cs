using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Modbot.Cloud.Features.Time;

/// <param name="ServerTime">RFC 3339 with an explicit offset.</param>
/// <param name="AcceptsMissingFields">
/// Always true: this Cloud takes an event with no world id or no instance id. A Cloud that predates
/// this answer lacks the field, which is how a client tells it to send the word <c>hidden</c> in
/// their place instead of leaving them out (a whole batch with one missing is refused there).
/// </param>
public sealed record CloudTimeResponse(
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("acceptsMissingFields")] bool AcceptsMissingFields = true);

/// <summary>
/// <c>GET /api/v1/time</c>: Cloud's clock, for the client's offset.
/// </summary>
/// <remarks>
/// The same endpoint the Modbot server offers its clients (protocol section 5). The client times the
/// request at both ends and halves the round trip, and sends the offset it finds with every batch.
/// Anonymous: it discloses the time, which every response's <c>Date</c> header already does.
/// </remarks>
public static class TimeEndpoints
{
    public static IEndpointRouteBuilder MapTime(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/time", ([FromServices] TimeProvider time) => Results.Ok(new CloudTimeResponse(time.GetUtcNow())));
        return app;
    }
}
