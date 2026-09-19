using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.ApiKeys;

/// <summary>One key as the list shows it. Never the key.</summary>
/// <param name="Start">The first characters of the key, so it can be recognised.</param>
/// <param name="State"><c>active</c>, <c>expired</c> or <c>revoked</c>.</param>
public sealed record ApiKeyView(
    Guid Id,
    string Name,
    string Start,
    IReadOnlyList<string> PermissionNames,
    Guid OwnerId,
    string? OwnerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string State);

/// <param name="Grantable">The permissions the caller may put on a new key: the ones they hold.</param>
public sealed record ApiKeysResponse(IReadOnlyList<ApiKeyView> Keys, IReadOnlyList<PermissionInfo> Grantable);

/// <param name="Permissions">Permission names from the catalogue.</param>
/// <param name="ExpiresAt">Null for a key that does not expire.</param>
public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string> Permissions, DateTimeOffset? ExpiresAt);

/// <param name="Key">The key. Shown this once; the server keeps only its hash.</param>
public sealed record CreatedApiKey(ApiKeyView ApiKey, string Key);

/// <summary>
/// Settings → API → Keys (API keys design §3.6).
/// </summary>
/// <remarks>
/// <para>
/// Everyone holding <see cref="ModbotPermissions.ManageApiKeys"/> sees every key and may revoke
/// any of them: revoking only ever removes access. A key is made only for the caller, and carries
/// no permission the caller does not hold -- the same rule roles follow, so managing keys is never
/// a way to become an administrator.
/// </para>
/// <para>
/// The caller may itself be a key: a key given <c>ManageApiKeys</c> can make keys, each capped by
/// that key's own permissions and owned by the same account.
/// </para>
/// </remarks>
public static class ApiKeyEndpoints
{
    public const int MaxNameLength = 64;

    public static IEndpointRouteBuilder MapApiKeys(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/api-keys")
            .WithTags("API keys")
            .RequiresFlag(ModbotPermissions.ManageApiKeys);

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var keys = await db.ApiKeys.AsNoTracking()
                    .OrderByDescending(k => k.CreatedAt)
                    .ToListAsync(ct);

                var names = await OwnerNamesAsync(db, keys.Select(k => k.CreatedByUserId), ct);
                var now = clock.UtcNow;
                var held = ModbotAuth.PermissionsOf(http.User);

                return Results.Ok(new ApiKeysResponse(
                    keys.Select(k => View(k, names, now)).ToList(),
                    PermissionCatalog.All.Where(p => ModbotAuth.Allows(held, (ModbotPermissions)p.Value)).ToList()));
            })
            .WithName("ListApiKeys")
            .WithSummary("List API keys")
            .WithDescription("Every API key, with its owner and state. Never the key itself.")
            .Produces<ApiKeysResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] CreateApiKeyRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Actor.Of(http) is not { } actor)
                    return Results.Unauthorized();

                var name = body.Name?.Trim() ?? string.Empty;
                if (name.Length == 0)
                    return Results.BadRequest(new { error = "A key needs a name." });

                if (name.Length > MaxNameLength)
                    return Results.BadRequest(new { error = $"That name is longer than {MaxNameLength} characters." });

                var permissions = PermissionCatalog.Parse(body.Permissions ?? [], out var problem);
                if (problem is not null)
                    return Results.BadRequest(new { error = problem });

                if (permissions == ModbotPermissions.None)
                    return Results.BadRequest(new { error = "Choose at least one permission." });

                if (!ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), permissions))
                    return Results.BadRequest(new { error = "You can only give a key permissions that you have yourself." });

                var now = clock.UtcNow;
                if (body.ExpiresAt is { } expires && expires <= now)
                    return Results.BadRequest(new { error = "The expiry date is in the past." });

                var secret = ApiKeySecrets.NewKey();
                var key = new ApiKey
                {
                    Name = name,
                    Start = ApiKeySecrets.StartOf(secret),
                    KeyHash = ApiKeySecrets.Hash(secret),
                    Permissions = permissions,
                    CreatedByUserId = actor.Id,
                    CreatedAt = now,
                    ExpiresAt = body.ExpiresAt?.ToUniversalTime(),
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.ApiKeys.Add(key);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.ApiKeyCreated,
                    key.Id.ToString(),
                    actor,
                    new JsonObject
                    {
                        ["name"] = key.Name,
                        ["start"] = key.Start,
                        ["permissions"] = string.Join(", ", PermissionCatalog.NamesOf(key.Permissions)),
                        ["expiresAt"] = key.ExpiresAt?.ToString("o"),
                    },
                    ct);

                await transaction.CommitAsync(ct);

                var names = new Dictionary<Guid, string> { [actor.Id] = actor.Username };
                return Results.Ok(new CreatedApiKey(View(key, names, now), secret));
            })
            .WithName("CreateApiKey")
            .WithSummary("Add API key")
            .WithDescription(
                "Make a key for yourself. "
                + "The response carries the key. It is shown this once: only its hash is stored. The "
                + "key can hold no permission you do not hold, and on every request it is narrowed "
                + "again to what your account holds at that moment.")
            .Produces<CreatedApiKey>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (Actor.Of(http) is not { } actor)
                    return Results.Unauthorized();

                var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
                if (key is null)
                    return Results.NotFound();

                // Revoking twice is not two events.
                if (key.RevokedAt is not null)
                    return Results.NoContent();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                key.RevokedAt = clock.UtcNow;
                key.RevokedByUserId = actor.Id;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.ApiKeyRevoked,
                    key.Id.ToString(),
                    actor,
                    new JsonObject { ["name"] = key.Name, ["start"] = key.Start },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("RevokeApiKey")
            .WithSummary("Revoke API key")
            .WithDescription("Revoke a key. It stops working on its next request.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static ApiKeyView View(ApiKey key, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now) => new(
        key.Id,
        key.Name,
        key.Start,
        PermissionCatalog.NamesOf(key.Permissions),
        key.CreatedByUserId,
        names.GetValueOrDefault(key.CreatedByUserId),
        key.CreatedAt,
        key.ExpiresAt,
        key.LastUsedAt,
        key.RevokedAt,
        key.RevokedAt is not null ? "revoked" : key.IsUsable(now) ? "active" : "expired");

    private static async Task<Dictionary<Guid, string>> OwnerNamesAsync(
        ModbotContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();

        return await db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }
}
