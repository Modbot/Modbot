using Microsoft.Extensions.Logging;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Security;

namespace Modbot.Core.Cloud;

/// <summary>
/// Signing somebody up for the project's news, when they asked while making their account.
/// </summary>
/// <remarks>
/// An interface so the account slices depend on the asking, not on Modbot Cloud: a host that never
/// registers a Cloud client gets <see cref="NoUpdatesSubscriber"/>, the checkbox is not shown, and
/// nothing in the account flows changes.
/// </remarks>
public interface IUpdatesSubscriber
{
    /// <summary>
    /// False when there is nowhere to send an address -- Cloud is switched off for this server, or
    /// this build has no Cloud client. The account forms hide the checkbox when this is false.
    /// </summary>
    bool Available { get; }

    /// <summary>
    /// Hands the address to Modbot Cloud. Never throws, never blocks anything the person is waiting
    /// on beyond its own short timeout, and answers nothing.
    /// </summary>
    Task SubscribeAsync(string email, CancellationToken ct);
}

/// <summary>What a deployment with no Modbot Cloud has instead.</summary>
public sealed class NoUpdatesSubscriber : IUpdatesSubscriber
{
    public bool Available => false;

    public Task SubscribeAsync(string email, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Sends the address to Modbot Cloud's subscriber list (server info and account email design §5).
/// </summary>
/// <remarks>
/// <para>
/// The address goes nowhere else and is not stored anywhere new: the account already holds it, and
/// what this records is only <em>that</em> the person asked (the caller writes that fact).
/// </para>
/// <para>
/// <strong>A failure is invisible to the person.</strong> Their account exists either way, and an
/// account creation that fails because a mailing list was unreachable would be the worst possible
/// trade. The log line carries the status code and never the address -- an address in a log is an
/// address in every copy of that log.
/// </para>
/// <para>
/// Unsubscribing is Cloud's job, not this server's: Cloud sends the mail, so Cloud owns the link at
/// the bottom of it. A Modbot server has no list to remove anybody from.
/// </para>
/// </remarks>
public sealed class CloudUpdatesSubscriber(
    ModbotContext db,
    CloudServerClient client,
    ModbotCloudAddress cloud,
    ISecretProtector protector,
    ILogger<CloudUpdatesSubscriber> log) : IUpdatesSubscriber
{
    public bool Available => !cloud.Disabled;

    public async Task SubscribeAsync(string email, CancellationToken ct)
    {
        if (!Available || string.IsNullOrWhiteSpace(email))
            return;

        // Null while this server has not registered with Cloud, which is the usual case during the
        // setup wizard. CloudServerClient.SubscribeAsync sends the address anyway.
        string? bearer = null;

        try
        {
            var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

            if (settings.CloudServerId is { Length: > 0 } id
                && settings.CloudServerSecretEncrypted is { } stored
                && protector.Unprotect(stored) is { Length: > 0 } secret)
            {
                bearer = $"{id}.{secret}";
            }

            var status = await client.SubscribeAsync(bearer, email, ct).ConfigureAwait(false);

            if (status is null)
                log.LogWarning("Could not reach Modbot Cloud to sign an account up for updates.");
            else if (status is < 200 or > 299)
                log.LogWarning("Modbot Cloud answered {Status} signing an account up for updates.", status);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nothing about making an account depends on this working, so nothing about making an
            // account may fail because it did not.
            log.LogWarning(e, "Could not sign an account up for updates.");
        }
    }
}
