using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.Core.Machine;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Health;

/// <param name="SampleSeconds">Seconds between readings.</param>
/// <param name="WindowMinutes">How far back <paramref name="Points"/> reaches.</param>
/// <param name="Processors">How many processors 100% is measured against.</param>
/// <param name="MemoryLimitBytes">The most memory this server may use, or null when nothing sets one.</param>
/// <param name="Now">The server's clock, so ages are worked out against it rather than the browser's.</param>
/// <param name="Points">The window, oldest first. Empty until the second reading has been taken.</param>
public sealed record MachineUsage(
    int SampleSeconds,
    int WindowMinutes,
    int Processors,
    long? MemoryLimitBytes,
    DateTimeOffset Now,
    IReadOnlyList<MachineUsagePoint> Points);

/// <summary>
/// How hard the machine Modbot is running on has been working lately.
/// </summary>
/// <remarks>
/// <para>
/// Behind <c>ViewOperationalLog</c>, the same permission the sync health detail takes. It is the
/// same kind of answer — Modbot's operational record about itself, useful for deciding whether the
/// server needs more memory and meaningless to a moderator — and a figure like this should not come
/// with a permission of its own for somebody to have to grant separately.
/// </para>
/// <para>
/// The sampler is resolved optionally, like <c>SyncDiagnostics</c> on the sync endpoint: a host
/// that maps the API without registering the background services still answers, with an empty
/// window, rather than failing to resolve a service at request time.
/// </para>
/// </remarks>
public static class MachineUsageEndpoints
{
    public static IEndpointRouteBuilder MapMachineUsage(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/health").WithTags("Health").RequireAuthorization();

        group.MapGet("/machine", (
                [FromServices] IModbotClock clock,
                [FromServices] MachineUsageSampler? sampler) =>
                Results.Ok(new MachineUsage(
                    (int)MachineUsageSampler.Every.TotalSeconds,
                    (int)MachineUsageSampler.Window.TotalMinutes,
                    sampler?.Processors ?? Environment.ProcessorCount,
                    sampler?.MemoryLimitBytes,
                    clock.UtcNow,
                    sampler?.Points ?? [])))
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetMachineUsage")
            .WithSummary("Get machine usage")
            .WithDescription(
                "Processor use, memory and disk activity over the last half hour. "
                + "Every figure is this server's own use of the machine, not the whole machine's: "
                + "in a container that is what the operator sized and pays for, and it is the only "
                + "thing Modbot can measure without being told about its host.\n\n"
                + "A figure is null on a host where it cannot be read honestly — the disk counters "
                + "are Linux-only, and a memory limit exists only where something sets one. Null "
                + "means unavailable, never zero.\n\n"
                + "Readings are kept in memory only. Nothing here is stored, and the window is "
                + "empty for the first few seconds after a restart: processor use and disk "
                + "activity are rates, and a rate needs two readings.")
            .Produces<MachineUsage>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
