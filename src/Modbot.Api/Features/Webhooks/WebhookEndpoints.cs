using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Webhooks;

/// <param name="CanEdit">Whether the caller may change it or send a test: its owner, or an administrator.</param>
/// <param name="State"><c>working</c>, <c>failing</c>, <c>stopped</c> (turned off by Modbot) or <c>off</c>.</param>
public sealed record WebhookView(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> SubjectIds,
    bool Enabled,
    Guid OwnerId,
    string? OwnerName,
    bool CanEdit,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? FailingSince,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? DisabledAt,
    string? DisabledReason,
    string State);

public sealed record WebhooksResponse(
    IReadOnlyList<WebhookView> Webhooks,
    bool AllowPrivateAddresses,
    bool CanChangeAllowPrivateAddresses);

public sealed record WebhookRequest(
    string Name,
    string Url,
    IReadOnlyList<string>? EventTypes,
    IReadOnlyList<string>? SubjectIds,
    bool Enabled);

/// <param name="Secret">The signing secret. Shown this once.</param>
public sealed record CreatedWebhook(WebhookView Webhook, string Secret);

public sealed record WebhookSecretResponse(string Secret);

public sealed record WebhookDeliveryView(
    long Id,
    string EventId,
    string EventType,
    DateTimeOffset AttemptedAt,
    int Attempt,
    int? StatusCode,
    int DurationMs,
    string? Error,
    bool Test,
    string Outcome);

public sealed record WebhookSettingsRequest(bool AllowPrivateAddresses);

public sealed record WebhookSettingsResponse(bool AllowPrivateAddresses);

/// <summary>
/// Settings → API → Webhooks (API keys design §6).
/// </summary>
/// <remarks>
/// <para>
/// Everyone holding <see cref="ModbotPermissions.ManageApiKeys"/> sees every webhook and may turn
/// one off or delete it. Changing a webhook's address, events or secret, or sending a test, is for
/// the account that set it up or an administrator: a webhook sends what its owner may see, so
/// repointing somebody else's would hand you their view of the log.
/// </para>
/// <para>
/// "Allow private addresses" is a setting, not a webhook property, and needs
/// <see cref="ModbotPermissions.ManageSettings"/>.
/// </para>
/// </remarks>
public static class WebhookEndpoints
{
    public const int MaxNameLength = 64;
    public const int MaxUrlLength = 2048;

    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/webhooks")
            .WithTags("Webhooks")
            .RequiresFlag(ModbotPermissions.ManageApiKeys);

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var hooks = await db.Webhooks.AsNoTracking().OrderBy(w => w.CreatedAt).ToListAsync(ct);
                var names = await OwnerNamesAsync(db, hooks.Select(h => h.CreatedByUserId), ct);
                var held = ModbotAuth.PermissionsOf(http.User);

                return Results.Ok(new WebhooksResponse(
                    hooks.Select(h => View(h, names, http)).ToList(),
                    await WebhookDispatcher.AllowPrivateAsync(db, ct),
                    ModbotAuth.Allows(held, ModbotPermissions.ManageSettings)));
            })
            .WithName("ListWebhooks")
            .WithSummary("Every webhook, with its delivery state. Never the secret.")
            .Produces<WebhooksResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] WebhookRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Actor.Of(http) is not { } actor)
                    return Results.Unauthorized();

                if (!EventVisibility.SeesAnything(ModbotAuth.PermissionsOf(http.User)))
                    return Results.BadRequest(new { error = "You cannot see any events to send." });

                var allowPrivate = await WebhookDispatcher.AllowPrivateAsync(db, ct);
                if (Validate(body, allowPrivate, out var filter) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var now = clock.UtcNow;
                var secret = WebhookSignature.NewSecret();

                var hook = new Webhook
                {
                    Name = body.Name.Trim(),
                    Url = body.Url.Trim(),
                    EventTypes = [.. filter.Types],
                    SubjectIds = [.. filter.Subjects],
                    Enabled = body.Enabled,
                    SecretEncrypted = protector.Protect(secret)!,
                    CreatedByUserId = actor.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                    // From now on: a new webhook does not replay history at its receiver.
                    DeliveredThrough = await new FactFeed(db).NewestIdAsync(ct),
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Webhooks.Add(hook);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(FactType.WebhookCreated, hook.Id.ToString(), actor, Describe(hook), ct);

                await transaction.CommitAsync(ct);

                var names = new Dictionary<Guid, string> { [actor.Id] = actor.Username };
                return Results.Ok(new CreatedWebhook(View(hook, names, http), secret));
            })
            .WithName("CreateWebhook")
            .WithSummary("Set up a webhook")
            .WithDescription("The response carries the signing secret. It is shown this once.")
            .Produces<CreatedWebhook>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] WebhookRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (hook is null)
                    return Results.NotFound();

                var changesMoreThanOnOff = hook.Name != body.Name?.Trim()
                    || hook.Url != body.Url?.Trim()
                    || !Same(hook.EventTypes, body.EventTypes)
                    || !Same(hook.SubjectIds, body.SubjectIds);

                // Turning it off is anyone's; anything else is its owner's or an administrator's.
                if ((changesMoreThanOnOff || body.Enabled) && !CanEdit(hook, http))
                    return Results.Forbid();

                var allowPrivate = await WebhookDispatcher.AllowPrivateAsync(db, ct);
                if (Validate(body, allowPrivate, out var filter) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var before = Describe(hook);
                var now = clock.UtcNow;

                if (body.Enabled && !hook.Enabled)
                {
                    // Turned back on: carry on from where it stopped, with a clean slate.
                    hook.FailedAttempts = 0;
                    hook.FailingSince = null;
                    hook.NextAttemptAt = null;
                    hook.DisabledAt = null;
                    hook.DisabledReason = null;
                }

                hook.Name = body.Name!.Trim();
                hook.Url = body.Url!.Trim();
                hook.EventTypes = [.. filter.Types];
                hook.SubjectIds = [.. filter.Subjects];
                hook.Enabled = body.Enabled;
                hook.UpdatedAt = now;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.WebhookChanged,
                    hook.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["before"] = before, ["after"] = Describe(hook) },
                    ct);

                await transaction.CommitAsync(ct);

                var names = await OwnerNamesAsync(db, [hook.CreatedByUserId], ct);
                return Results.Ok(View(hook, names, http));
            })
            .WithName("UpdateWebhook")
            .WithSummary("Change a webhook, or turn it on or off")
            .Produces<WebhookView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (hook is null)
                    return Results.NotFound();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Webhooks.Remove(hook);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(FactType.WebhookDeleted, hook.Id.ToString(), Actor.Of(http), Describe(hook), ct);

                await transaction.CommitAsync(ct);
                return Results.NoContent();
            })
            .WithName("DeleteWebhook")
            .WithSummary("Delete a webhook and its delivery log")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/secret", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (hook is null)
                    return Results.NotFound();

                if (!CanEdit(hook, http))
                    return Results.Forbid();

                var secret = WebhookSignature.NewSecret();
                hook.SecretEncrypted = protector.Protect(secret)!;
                hook.UpdatedAt = clock.UtcNow;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.WebhookSecretChanged, hook.Id.ToString(), Actor.Of(http), new JsonObject { ["name"] = hook.Name }, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(new WebhookSecretResponse(secret));
            })
            .WithName("RollWebhookSecret")
            .WithSummary("Make a new signing secret. The old one stops being used at once.")
            .Produces<WebhookSecretResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/test", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] WebhookSender sender,
                [FromServices] WebhookDispatcher dispatcher,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (hook is null)
                    return Results.NotFound();

                if (!CanEdit(hook, http) || Actor.Of(http) is not { } actor)
                    return Results.Forbid();

                if (!Uri.TryCreate(hook.Url, UriKind.Absolute, out var url))
                    return Results.BadRequest(new { error = "The address is not valid." });

                var now = clock.UtcNow;
                var envelope = EventEnvelopes.Test(hook.Id, Guid.CreateVersion7(), now, actor.Id.ToString(), actor.Username);
                var result = await sender.SendAsync(
                    url,
                    protector.Unprotect(hook.SecretEncrypted) ?? string.Empty,
                    envelope,
                    await WebhookDispatcher.AllowPrivateAsync(db, ct),
                    ct);

                await dispatcher.RecordAsync(hook, envelope.Id, envelope.Type, 1, result, test: true, now, ct);

                var logged = await db.WebhookDeliveries.AsNoTracking()
                    .Where(d => d.WebhookId == hook.Id)
                    .OrderByDescending(d => d.Id)
                    .FirstAsync(ct);

                return Results.Ok(DeliveryView(logged));
            })
            .WithName("TestWebhook")
            .WithSummary("Send a modbot.webhook.test event now and say what came back")
            .Produces<WebhookDeliveryView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/deliveries", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (!await db.Webhooks.AnyAsync(w => w.Id == id, ct))
                    return Results.NotFound();

                var rows = await db.WebhookDeliveries.AsNoTracking()
                    .Where(d => d.WebhookId == id)
                    .OrderByDescending(d => d.Id)
                    .Take(50)
                    .ToListAsync(ct);

                return Results.Ok(rows.Select(DeliveryView).ToList());
            })
            .WithName("ListWebhookDeliveries")
            .WithSummary("The last attempts to deliver to a webhook, newest first")
            .Produces<IReadOnlyList<WebhookDeliveryView>>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPut("/api/settings/webhooks", async (
                HttpContext http,
                [FromBody] WebhookSettingsRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.WebhooksAllowPrivateAddresses;

                if (before == body.AllowPrivateAddresses)
                    return Results.Ok(new WebhookSettingsResponse(before));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.WebhooksAllowPrivateAddresses = body.AllowPrivateAddresses;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "webhooksAllowPrivateAddresses",
                        ["before"] = before,
                        ["after"] = body.AllowPrivateAddresses,
                    },
                    ct);

                await transaction.CommitAsync(ct);
                return Results.Ok(new WebhookSettingsResponse(body.AllowPrivateAddresses));
            })
            .WithTags("Settings")
            .WithName("SetWebhookSettings")
            .WithSummary("Allow or refuse webhooks to private addresses")
            .Produces<WebhookSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    /// <summary>The address, name and event rules for a webhook. Null when they are fine.</summary>
    public static string? Validate(WebhookRequest body, bool allowPrivate, out EventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(body);
        filter = EventFilter.Everything;

        var name = body.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "A webhook needs a name.";

        if (name.Length > MaxNameLength)
            return $"That name is longer than {MaxNameLength} characters.";

        if (CheckUrl(body.Url, allowPrivate) is { } urlProblem)
            return urlProblem;

        if (body.EventTypes is null || body.EventTypes.Count == 0)
            return "Choose at least one event type.";

        return EventFilter.TryCreate(body.EventTypes, body.SubjectIds, out filter, out var error) ? null : error;
    }

    public static string? CheckUrl(string? value, bool allowPrivate)
    {
        var text = value?.Trim() ?? string.Empty;

        if (text.Length == 0)
            return "Enter the address.";

        if (text.Length > MaxUrlLength)
            return $"That address is longer than {MaxUrlLength} characters.";

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return "That is not a web address.";
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
            return "Leave the user name and password out of the address.";

        if (!allowPrivate && url.Scheme != Uri.UriSchemeHttps)
            return "The address must start with https://.";

        if (!allowPrivate && WebhookAddressGuard.IsBlockedHost(url.Host))
            return "That address is private.";

        return null;
    }

    private static bool CanEdit(Webhook hook, HttpContext http)
        => ModbotAuth.UserIdOf(http.User) == hook.CreatedByUserId
           || ModbotAuth.PermissionsOf(http.User).HasFlag(ModbotPermissions.Administrator);

    private static bool Same(List<string> stored, IReadOnlyList<string>? asked)
        => stored.SequenceEqual((asked ?? []).Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.Ordinal);

    private static JsonObject Describe(Webhook hook) => new()
    {
        ["name"] = hook.Name,
        ["url"] = hook.Url,
        ["eventTypes"] = string.Join(", ", hook.EventTypes),
        ["subjects"] = hook.SubjectIds.Count,
        ["enabled"] = hook.Enabled,
    };

    private static WebhookView View(Webhook hook, IReadOnlyDictionary<Guid, string> names, HttpContext http) => new(
        hook.Id,
        hook.Name,
        hook.Url,
        hook.EventTypes,
        hook.SubjectIds,
        hook.Enabled,
        hook.CreatedByUserId,
        names.GetValueOrDefault(hook.CreatedByUserId),
        CanEdit(hook, http),
        hook.CreatedAt,
        hook.LastSuccessAt,
        hook.FailingSince,
        hook.LastError,
        hook.NextAttemptAt,
        hook.DisabledAt,
        hook.DisabledReason,
        hook.Enabled
            ? hook.FailingSince is null ? "working" : "failing"
            : hook.DisabledReason is null ? "off" : "stopped");

    private static WebhookDeliveryView DeliveryView(WebhookDelivery d) => new(
        d.Id, d.EventId, d.EventType, d.AttemptedAt, d.Attempt, d.StatusCode, d.DurationMs, d.Error, d.Test, d.Outcome);

    private static async Task<Dictionary<Guid, string>> OwnerNamesAsync(
        ModbotContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();

        return await db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }
}
