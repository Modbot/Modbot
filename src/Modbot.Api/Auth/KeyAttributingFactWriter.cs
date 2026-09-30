using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Auth;

/// <summary>
/// Writes each fact a request made with an API key records, with the key's id in its data (API
/// keys design §3).
/// </summary>
/// <remarks>
/// <para>
/// A key acts as the account that made it, so a fact made through one names that account and
/// nothing else, and the audit log could not tell the key's doing from the person's own. This is
/// the one place that closes the gap: the roughly twenty-five writers of account facts, notes,
/// case files, bans and settings do not each have to remember to ask which key called them.
/// </para>
/// <para>
/// It marks only a fact whose actor is the account the request is signed in as, on a request that
/// carries a key. A background job, a browser session, and a fact about somebody else's doing
/// pass through unchanged.
/// </para>
/// </remarks>
public sealed class KeyAttributingFactWriter : IFactWriter
{
    /// <summary>The name of the key's id in a fact's data.</summary>
    public const string DataKey = "apiKeyId";

    private readonly IFactWriter _inner;
    private readonly IHttpContextAccessor _http;

    public KeyAttributingFactWriter(IFactWriter inner, IHttpContextAccessor http)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(http);

        _inner = inner;
        _http = http;
    }

    public Task<FactWriteResult> WriteAsync(FactRecord fact, CancellationToken ct = default)
        => _inner.WriteAsync(Mark(fact), ct);

    public Task<IReadOnlyList<FactWriteResult>> WriteManyAsync(IEnumerable<FactRecord> facts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return _inner.WriteManyAsync(facts.Select(Mark), ct);
    }

    public Task<long?> AlreadyRecordedAsync(FactRecord fact, TimeSpan within, CancellationToken ct = default)
        => _inner.AlreadyRecordedAsync(fact, within, ct);

    private FactRecord Mark(FactRecord fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        if (fact.ActorPlatform != FactPlatform.Modbot || fact.ActorId is null)
            return fact;

        var principal = _http.HttpContext?.User;
        if (ApiKeyAuthentication.KeyIdOf(principal) is not { } keyId
            || ModbotAuth.UserIdOf(principal)?.ToString() != fact.ActorId)
        {
            return fact;
        }

        // A copy, so the caller's own object is not changed under it.
        var data = fact.Data is null ? new JsonObject() : (JsonObject)fact.Data.DeepClone();
        data[DataKey] = keyId.ToString();

        return fact with { Data = data };
    }
}

public static class KeyAttributingFactWriterRegistration
{
    private sealed class Marker;

    /// <summary>
    /// Puts <see cref="KeyAttributingFactWriter"/> in front of the fact writer registered so far.
    /// Does nothing when none is, and when it has been done already.
    /// </summary>
    /// <remarks>
    /// The fact writer belongs to <c>AddModbotAnalytics</c>, which the host calls before
    /// <c>AddModbotApi</c>; every host and test host does.
    /// </remarks>
    public static IServiceCollection AddKeyAttribution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(Marker)))
            return services;

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IFactWriter));
        if (inner is null)
            return services;

        services.AddSingleton<Marker>();
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.Remove(inner);

        services.Add(new ServiceDescriptor(
            typeof(IFactWriter),
            provider => new KeyAttributingFactWriter(
                Resolve(provider, inner),
                provider.GetRequiredService<IHttpContextAccessor>()),
            inner.Lifetime));

        return services;
    }

    private static IFactWriter Resolve(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IFactWriter instance)
            return instance;

        if (descriptor.ImplementationFactory is { } factory)
            return (IFactWriter)factory(provider);

        return (IFactWriter)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }
}
