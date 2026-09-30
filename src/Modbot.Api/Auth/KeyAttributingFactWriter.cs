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
    private const string InnerKey = "modbot:fact-writer:inner";

    private sealed class Marker;

    /// <summary>
    /// Puts <see cref="KeyAttributingFactWriter"/> in front of the fact writer registered so far.
    /// Does nothing when it has been done already.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No fact writer is registered yet. The fact writer belongs to <c>AddModbotAnalytics</c>, which
    /// must come first: left to carry on, every fact made through a key would be written unmarked,
    /// and nothing would say so.
    /// </exception>
    /// <remarks>
    /// The writer already registered is moved to a keyed registration of its own, so the container
    /// still makes it, owns it and disposes it with its scope.
    /// </remarks>
    public static IServiceCollection AddKeyAttribution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(Marker)))
            return services;

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IFactWriter) && !d.IsKeyedService)
            ?? throw new InvalidOperationException(
                "AddModbotApi needs the fact writer registered first: call AddModbotAnalytics before it.");

        services.AddSingleton<Marker>();
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.Remove(inner);
        services.Add(Keyed(inner));

        services.Add(new ServiceDescriptor(
            typeof(IFactWriter),
            provider => new KeyAttributingFactWriter(
                provider.GetRequiredKeyedService<IFactWriter>(InnerKey),
                provider.GetRequiredService<IHttpContextAccessor>()),
            inner.Lifetime));

        return services;
    }

    private static ServiceDescriptor Keyed(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
            return new ServiceDescriptor(typeof(IFactWriter), InnerKey, instance);

        if (descriptor.ImplementationFactory is { } factory)
            return new ServiceDescriptor(typeof(IFactWriter), InnerKey, (provider, _) => factory(provider), descriptor.Lifetime);

        return new ServiceDescriptor(typeof(IFactWriter), InnerKey, descriptor.ImplementationType!, descriptor.Lifetime);
    }
}
