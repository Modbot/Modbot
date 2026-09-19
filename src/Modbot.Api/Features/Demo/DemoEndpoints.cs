using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Core.Configuration;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Demo;

/// <param name="On">Whether this deployment is a demo.</param>
/// <param name="Busy">Whether the data is being filled in or put back right now.</param>
/// <param name="Step">What is happening, in plain words. Empty when nothing is.</param>
/// <param name="Done">How much of the current step is finished.</param>
/// <param name="Total">How much there is. Zero when it is not countable.</param>
/// <param name="SeededAt">When the data was last complete.</param>
/// <param name="NextResetAt">When the demo puts itself back next, or null for never.</param>
/// <param name="ResetHours">The hours between automatic resets. Zero means never.</param>
public sealed record DemoStatusResponse(
    bool On,
    bool Busy,
    string Step,
    int Done,
    int Total,
    DateTimeOffset? SeededAt,
    DateTimeOffset? NextResetAt,
    int ResetHours);

/// <summary>
/// The demo's own two endpoints: what it is doing, and put it back.
/// </summary>
/// <remarks>
/// Both are mapped on every deployment, because the web app asks the first one on every load to
/// decide whether to show the demo marker. On a deployment that is not a demo it answers
/// <c>on: false</c> and the reset refuses — there is nothing here to turn a real deployment into a
/// demo (demo mode design §6).
/// </remarks>
public static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemo(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/demo", (DemoMode demo, DemoState state) =>
            {
                var on = DemoAuthentication.MayServeEveryoneAsAdministrator(demo);
                var (done, total) = state.Progress;

                return Results.Ok(new DemoStatusResponse(
                    on,
                    on && state.Busy,
                    on ? state.Step : string.Empty,
                    done,
                    total,
                    on ? state.SeededAt : null,
                    on ? state.NextResetAt : null,
                    on ? demo.ResetHours : 0));
            })
            .WithTags("Demo")
            .ExcludeFromDescription()
            .WithName("GetDemoStatus")
            .WithSummary("Get demo status")
            .WithDescription("Whether this deployment is a demo, and what it is doing.")
            .Produces<DemoStatusResponse>()

            // Unauthenticated for the same reason the onboarding status is: the web app asks it
            // before it knows anything, and what it discloses is whether this is a demo, which is
            // written across the top of the page anyway.
            .AllowAnonymous();

        app.MapPost("/api/demo/reset", (
                DemoMode demo,
                DemoState state,
                IModbotClock clock,
                HttpContext http) =>
            {
                if (!DemoAuthentication.MayServeEveryoneAsAdministrator(demo))
                    return Results.NotFound();

                if (state.Busy || state.ResetAsked)
                    return Results.Ok(new { started = false });

                state.AskForReset(clock.UtcNow, ModbotAuth.UsernameOf(http.User) ?? DemoMode.AdministratorUsername);
                return Results.Ok(new { started = true });
            })
            .WithTags("Demo")
            .ExcludeFromDescription()
            .WithName("ResetDemo")
            .WithSummary("Reset the demo")
            .WithDescription("Wipe the demo and fill it in again.")
            .RequireAuthorization();

        return app;
    }
}

/// <summary>Turns away the requests a demo has no way to carry out.</summary>
public static class DemoRefusalMiddleware
{
    /// <summary>
    /// Answers signing in, invites, reset links, the test email and pairing with one short line.
    /// </summary>
    /// <remarks>
    /// Before authentication, so it costs nothing on a deployment that is not a demo: one reference
    /// comparison per request. VRChat and Discord are not listed here because they are stopped at
    /// their own chokepoints instead — see demo mode design §3.2.
    /// </remarks>
    public static IApplicationBuilder UseDemoRefusals(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var demo = context.RequestServices.GetService<DemoMode>();

            if (DemoAuthentication.MayServeEveryoneAsAdministrator(demo)
                && DemoRefusals.Refuses(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = DemoRefusals.Message });
                return;
            }

            await next(context);
        });
    }
}
