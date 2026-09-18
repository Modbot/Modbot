using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat.Session;

namespace Modbot.Api.Auth;

/// <summary>
/// The administrator's contact email, for the User-Agent the gate sends to VRChat.
/// </summary>
/// <remarks>
/// <para>
/// VRChat wants a person to write to before it blocks, and the person who runs this Modbot is the
/// oldest enabled account holding Administrator that has an email address -- the rule
/// <see cref="OwnerAccount"/> owns. The address is given at onboarding (the create-administrator
/// step requires it) and can change afterwards, so it is read from the account rather than fixed
/// at startup.
/// </para>
/// <para>
/// Cached for a minute: the factory reads this every time it builds a client, and a client
/// rebuild should not cost a database read. Anything that changes an account's email calls
/// <see cref="Invalidate"/>, so the next build sees the new address at once.
/// </para>
/// <para>
/// The read is synchronous because <see cref="IOperatorContact.Email"/> is a property read from
/// inside the factory's synchronous build. It is one indexed query, once a minute at most.
/// </para>
/// </remarks>
public sealed class AdministratorContact : IOperatorContact
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly object _gate = new();

    private string? _email;
    private DateTimeOffset? _readAt;

    public AdministratorContact(IServiceScopeFactory scopes, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _clock = clock;
    }

    public string? Email
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                if (_readAt is { } at && now - at < CacheFor)
                    return _email;

                _email = Read();
                _readAt = now;
                return _email;
            }
        }
    }

    /// <summary>Forget the cached address. Called whenever an account's email changes.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _readAt = null;
        }
    }

    private string? Read()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // The owner rule, shared with GET /api/server so the address VRChat is given and the
        // address my.modbot.co shows are the same person (accounts and access design §4.5).
        return OwnerAccount.Email(db);
    }
}
