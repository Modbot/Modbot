using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;

namespace Modbot.Api.Conventions;

/// <summary>
/// Lets a web page on another address call the API, when the operator names that address in
/// <c>MODBOT_CORS_ORIGINS</c> (API conventions design §6). Off unless set.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keys only, never the session.</strong> Credentials are never allowed, so a browser
/// will not let another site read an answer to a request that rode on somebody's Modbot cookie.
/// A page elsewhere signs in the way a program does: <c>Authorization: Bearer mbk_...</c>, with
/// whatever that key may do.
/// </para>
/// <para>
/// <c>/api/server</c> keeps its own rule (any origin, always) and is left out here, because this
/// policy's answer to a preflight from an address it does not list would otherwise take the place
/// of that endpoint's own.
/// </para>
/// </remarks>
public static class ApiCors
{
    /// <summary>Headers a page may send.</summary>
    public static readonly string[] AllowedHeaders = ["Authorization", "Content-Type", "Accept", "Idempotency-Key"];

    /// <summary>Headers a page may read beside the body.</summary>
    public static readonly string[] ExposedHeaders = ["Retry-After", "Location", "ETag", "X-Modbot-Proxy-Account"];

    /// <summary>The policy for these origins, or null for none.</summary>
    public static CorsPolicy? PolicyFor(IReadOnlyList<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        if (origins.Count == 0)
            return null;

        var policy = new CorsPolicyBuilder();

        if (origins.Contains("*"))
            policy.AllowAnyOrigin();
        else
            policy.WithOrigins([.. origins]);

        return policy
            .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete)
            .WithHeaders(AllowedHeaders)
            .WithExposedHeaders(ExposedHeaders)
            .SetPreflightMaxAge(TimeSpan.FromHours(1))
            .Build();
    }

    /// <summary>The name the policy is registered under.</summary>
    public const string PolicyName = "modbot-api";

    /// <summary>Registers the policy, read from <c>MODBOT_CORS_ORIGINS</c> once the container is built.</summary>
    public static IServiceCollection AddApiCors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IServiceProvider>((options, provider) =>
        {
            if (PolicyFor(provider.GetService<ModbotEnvironment>()?.CorsOrigins ?? []) is { } policy)
                options.AddPolicy(PolicyName, policy);
        });

        return services;
    }

    /// <summary>
    /// Answers preflights and adds the headers, under <c>/api</c>, when <c>MODBOT_CORS_ORIGINS</c>
    /// names anything. After routing and before sign-in, so a preflight never meets a sign-in check.
    /// </summary>
    public static IApplicationBuilder UseApiCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var origins = app.ApplicationServices.GetService<ModbotEnvironment>()?.CorsOrigins ?? [];

        if (origins.Count == 0)
            return app;

        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api")
                && !context.Request.Path.StartsWithSegments("/api/server"),
            branch => branch.UseCors(PolicyName));
    }
}
