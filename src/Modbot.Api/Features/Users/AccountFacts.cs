using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Users;

/// <summary>Who did something: an account's id and the name to show for it.</summary>
public readonly record struct Actor(Guid Id, string Username)
{
    /// <summary>The signed-in account, or null on an anonymous request.</summary>
    public static Actor? Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var id = ModbotAuth.UserIdOf(http.User);
        return id is null ? null : new Actor(id.Value, http.User.Identity?.Name ?? string.Empty);
    }
}

/// <summary>
/// Writes the facts that record what happens to staff accounts (accounts and access design §6).
/// </summary>
/// <remarks>
/// <para>
/// One place, so every account fact has the same shape: the subject is the account on the Modbot
/// platform, the actor is whoever did it, and the payload carries <c>actorDisplayName</c> the way
/// VRChat's audit entries do, so the audit log shows a name without a join.
/// </para>
/// <para>
/// Callers run this inside the same transaction as the change it records. An account change with
/// no record of who made it is the failure §5.8 exists to prevent, so the two commit together or
/// not at all.
/// </para>
/// </remarks>
public sealed class AccountFacts
{
    /// <summary>The subject of a failed login that matched no account.</summary>
    public const string NoAccount = "unknown";

    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;

    public AccountFacts(IFactWriter facts, EventPartitionMaintainer partitions, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _facts = facts;
        _partitions = partitions;
        _clock = clock;
    }

    /// <summary>Records one account event about <paramref name="subjectId"/>, now.</summary>
    public async Task RecordAsync(
        string type,
        string subjectId,
        Actor? actor,
        JsonObject? data,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;

        // The hosted maintainer keeps partitions ahead in the host, but an account event must
        // never fail for want of one — and in a test host there is no maintainer.
        await _partitions.EnsureForAsync(now, ct);

        var payload = data ?? new JsonObject();
        if (actor is { } a && !payload.ContainsKey("actorDisplayName"))
            payload["actorDisplayName"] = a.Username;

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = subjectId,
                ActorPlatform = actor is null ? null : FactPlatform.Modbot,
                ActorId = actor?.Id.ToString(),
                Source = FactSource.Modbot,
                Data = payload,
            },
            ct);
    }

    /// <summary>Records one account event about an account.</summary>
    public Task RecordAsync(string type, ModbotUser subject, Actor? actor, JsonObject? data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var payload = data ?? new JsonObject();
        payload.TryAdd("username", subject.Username);

        return RecordAsync(type, subject.Id.ToString(), actor, payload, ct);
    }
}
