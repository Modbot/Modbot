using Modbot.Cloud.Common;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>
/// The limits on what an unauthenticated caller can make Cloud do with an account: create one, send
/// mail to an address, or guess a password.
/// </summary>
/// <remarks>
/// Each is per IP address, held in memory, one per process. They exist so that nobody can use Cloud
/// as a way to mail strangers or to work through a password list, not for accounting.
/// </remarks>
public sealed class AccountLimits(TimeProvider time)
{
    public const int RegistrationsPerHour = 5;

    /// <summary>Covers every endpoint that sends mail: verification, resend, and forgotten password.</summary>
    public const int MailsPerHour = 10;

    public const int SignInsPerHour = 20;

    public WindowLimit Registrations { get; } = new(RegistrationsPerHour, TimeSpan.FromHours(1), time);

    public WindowLimit Mails { get; } = new(MailsPerHour, TimeSpan.FromHours(1), time);

    public WindowLimit SignIns { get; } = new(SignInsPerHour, TimeSpan.FromHours(1), time);
}
