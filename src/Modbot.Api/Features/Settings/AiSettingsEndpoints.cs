using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.AI;
using Modbot.AI.Usage;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;

namespace Modbot.Api.Features.Settings;

/// <summary>One preset on the provider list.</summary>
public sealed record AiProviderView(string Id, string Label, string Endpoint);

/// <summary>Settings → AI → Base, as stored.</summary>
/// <param name="Endpoint">Null when nothing has been saved; the page fills it from the preset.</param>
/// <param name="ApiKeyStored">Whether a key is stored. The key itself is never returned.</param>
public sealed record AiSettingsResponse(
    bool Enabled,
    string Provider,
    string? Endpoint,
    string? Model,
    bool ApiKeyStored,
    IReadOnlyList<AiProviderView> Providers);

/// <param name="ApiKey">A new key. Null or empty keeps the stored one, unless the endpoint changed.</param>
/// <param name="RemoveApiKey">Forget the stored key.</param>
public sealed record AiSettingsUpdate(
    bool Enabled,
    string? Provider,
    string? Endpoint,
    string? Model,
    string? ApiKey,
    bool RemoveApiKey = false);

/// <summary>The values on the form, for the Test button and the model list. Nothing is saved.</summary>
/// <param name="ApiKey">
/// The key typed into the form. When empty the stored key is used, but only if the endpoint is the
/// one it was saved with.
/// </param>
public sealed record AiConnectionCheck(string? Provider, string? Endpoint, string? Model, string? ApiKey);

public sealed record AiTestResponse(bool Worked, string Message);

public sealed record AiModelsResponse(IReadOnlyList<string> Models, string? Error);

/// <summary>
/// Settings → AI → Base: where AI requests go, with which key, to which model, and whether they
/// go at all (M8 section 4).
/// </summary>
/// <remarks>
/// <para>
/// The Test button and the model list answer 200 with a verdict, the way the email and evidence
/// checks do: an endpoint that refused the key has been successfully diagnosed, and the provider's
/// sentence is the useful part. Input that cannot even be tried is a 400.
/// </para>
/// <para>
/// Both work from the values on the form rather than the saved row, so a key can be checked before
/// it replaces one that works.
/// </para>
/// </remarks>
public static class AiSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAiSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai").WithTags("Settings");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(View(settings));
            })
            .WithName("GetAiSettings")
            .WithSummary("The AI endpoint, model and on/off switch. The API key is never returned.")
            .Produces<AiSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                [FromBody] AiSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var check = AiSettingsRules.Check(body.Enabled, body.Provider, body.Endpoint, body.Model, body.ApiKey);
                if (!check.Ok)
                    return Results.BadRequest(new { error = check.Error });

                var settings = await db.GetSettingsAsync(ct);
                var endpoint = check.Endpoint?.OriginalString;
                var newKey = string.IsNullOrWhiteSpace(body.ApiKey) ? null : body.ApiKey.Trim();

                if (newKey is not null)
                    settings.AiApiKeyEncrypted = protector.Protect(newKey);
                else if (body.RemoveApiKey || !AiSettingsRules.SameEndpoint(settings.AiEndpoint, endpoint))
                    settings.AiApiKeyEncrypted = null;

                if (body.Enabled && settings.AiApiKeyEncrypted is null && !check.Provider!.AllowsHttp)
                    return Results.BadRequest(new { error = "Enter the API key." });

                settings.AiEnabled = body.Enabled;
                settings.AiProvider = check.Provider!.Id;
                settings.AiEndpoint = endpoint;
                settings.AiModel = check.Model;

                await db.SaveChangesAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetAiSettings")
            .WithSummary("Save the AI endpoint, key, model and on/off switch")
            .Produces<AiSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/test", async (
                HttpContext http,
                [FromBody] AiConnectionCheck body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] IAiClients ai,
                [FromServices] IAiUsage usage,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var (connection, error) = await ConnectionAsync(body, requireModel: true, db, protector, ct);
                if (connection is null)
                    return Results.BadRequest(new { error });

                // A test is a real call, so it is counted and limited like any feature (AI chat design §10).
                if (await usage.LimitReachedAsync(AiFeatures.Test, ct) is { } reached)
                    return Results.Ok(new AiTestResponse(false, reached.Message));

                var result = await ai.TestAsync(connection, ct);
                await usage.RecordAsync(AiFeatures.Test, ModbotAuth.UserIdOf(http.User), connection.Model, connection.Provider, result.Usage, ct);

                return Results.Ok(new AiTestResponse(result.Worked, result.Message));
            })
            .WithName("TestAiSettings")
            .WithSummary("Send one short chat message through the endpoint on the form")
            .Produces<AiTestResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/models", async (
                [FromBody] AiConnectionCheck body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] IAiClients ai,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var (connection, error) = await ConnectionAsync(body, requireModel: false, db, protector, ct);
                if (connection is null)
                    return Results.BadRequest(new { error });

                var result = await ai.ListModelsAsync(connection, ct);
                return Results.Ok(new AiModelsResponse(result.Models, result.Error));
            })
            .WithName("ListAiModels")
            .WithSummary("The models the endpoint on the form lists")
            .Produces<AiModelsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static AiSettingsResponse View(Core.Data.Entities.Settings settings) => new(
        settings.AiEnabled,
        AiProviders.Find(settings.AiProvider)?.Id ?? AiProviders.Default.Id,
        settings.AiEndpoint,
        settings.AiModel,
        settings.AiApiKeyEncrypted is not null,
        [.. AiProviders.All.Select(p => new AiProviderView(p.Id, p.Label, p.Endpoint))]);

    private static async Task<(AiConnection? Connection, string? Error)> ConnectionAsync(
        AiConnectionCheck body, bool requireModel, ModbotContext db, ISecretProtector protector, CancellationToken ct)
    {
        // Checked as though AI were on: trying the endpoint is using it.
        var check = AiSettingsRules.Check(true, body.Provider, body.Endpoint, body.Model, body.ApiKey, requireModel);
        if (!check.Ok)
            return (null, check.Error);

        var key = string.IsNullOrWhiteSpace(body.ApiKey) ? null : body.ApiKey.Trim();

        if (key is null)
        {
            var settings = await db.GetSettingsAsync(ct);
            if (AiSettingsRules.SameEndpoint(settings.AiEndpoint, check.Endpoint!.OriginalString))
                key = protector.Unprotect(settings.AiApiKeyEncrypted);
        }

        return (new AiConnection(check.Provider!.Id, check.Endpoint!, key, check.Model ?? ""), null);
    }
}
