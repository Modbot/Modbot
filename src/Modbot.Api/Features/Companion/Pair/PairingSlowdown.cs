using Modbot.Api.Features.Auth.Login;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Companion.Pair;

/// <summary>
/// Wrong pairing codes, counted per address and slowed the way failed sign-ins are.
/// </summary>
/// <remarks>
/// <para>
/// A pairing code is about 38 bits and single-use, and it expires in minutes; what keeps guessing
/// out of reach is that nobody gets to try many. Until this existed <c>/pair</c> answered as fast
/// as it was asked. The wait doubles per wrong code from one address and stops at
/// <see cref="AttemptSlowdown.MaxWait"/>; a right code after the wait still pairs, so a slow
/// address is never shut out.
/// </para>
/// <para>
/// <strong>Keyed on the address alone.</strong> The shared counter keeps a name and an address;
/// pairing has no name, so the address stands in for both, and one address's wrong codes never
/// slow another's.
/// </para>
/// <para>
/// Behind a proxy that does not pass the caller's address on, every companion arrives from the
/// proxy's address and shares one count. That is a known limit of reading the address here, the
/// same one sign-in has.
/// </para>
/// </remarks>
public sealed class PairingSlowdown : AttemptSlowdown
{
    public PairingSlowdown(IModbotClock clock) : base(clock) { }

    /// <summary>How long this attempt should wait before its code is checked.</summary>
    public TimeSpan WaitFor(string? address) => WaitFor(address, address);

    public void RecordFailure(string? address) => RecordFailure(address, address);

    public void RecordSuccess(string? address) => RecordSuccess(address, address);
}
