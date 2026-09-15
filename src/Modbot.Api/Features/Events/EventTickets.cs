using System.Collections.Concurrent;
using System.Security.Cryptography;
using Modbot.Api.Auth;

namespace Modbot.Api.Features.Events;

/// <summary>Who a ticket stands for.</summary>
public sealed record EventTicketHolder(Guid UserId, Guid? ApiKeyId);

/// <summary>
/// Short-lived, single-use tickets for opening the event WebSocket from a browser (API keys design
/// §5.1).
/// </summary>
/// <remarks>
/// <para>
/// A browser cannot put a header on a WebSocket, a key must never go in a query string, and a
/// cookie-only socket is open to cross-site WebSocket hijacking. A ticket is none of those: it is
/// made by a POST (which the session cookie's SameSite=Lax does not send cross-site), lasts a
/// minute, and works once.
/// </para>
/// <para>
/// Held in memory. A restart forgets outstanding tickets, which costs a browser one retry. Only the
/// hash is kept, so a memory dump yields no working tickets either.
/// </para>
/// </remarks>
public sealed class EventTickets
{
    private readonly ConcurrentDictionary<string, (EventTicketHolder Holder, DateTimeOffset ExpiresAt)> _tickets = new(StringComparer.Ordinal);

    public (string Ticket, DateTimeOffset ExpiresAt) Issue(EventTicketHolder holder, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(holder);

        Sweep(now);

        var ticket = ApiKeySecrets.Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = now + lifetime;
        _tickets[ApiKeySecrets.Hash(ticket)] = (holder, expires);

        return (ticket, expires);
    }

    /// <summary>The holder, once. Null for an unknown, used or expired ticket.</summary>
    public EventTicketHolder? Redeem(string? ticket, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128)
            return null;

        if (!_tickets.TryRemove(ApiKeySecrets.Hash(ticket), out var entry))
            return null;

        return now < entry.ExpiresAt ? entry.Holder : null;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (hash, entry) in _tickets)
        {
            if (entry.ExpiresAt <= now)
                _tickets.TryRemove(hash, out _);
        }
    }
}

/// <summary>Open event connections, counted per key or per account (API keys design §5.5).</summary>
public sealed class EventConnections
{
    private readonly Dictionary<string, int> _open = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public static string KeyFor(Guid userId, Guid? apiKeyId)
        => apiKeyId is { } key ? $"key:{key}" : $"user:{userId}";

    public bool TryOpen(string caller, int limit)
    {
        lock (_gate)
        {
            var count = _open.GetValueOrDefault(caller);
            if (count >= limit)
                return false;

            _open[caller] = count + 1;
            return true;
        }
    }

    public void Close(string caller)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(caller, out var count))
                return;

            if (count <= 1)
                _open.Remove(caller);
            else
                _open[caller] = count - 1;
        }
    }

    public int OpenFor(string caller)
    {
        lock (_gate)
            return _open.GetValueOrDefault(caller);
    }
}
