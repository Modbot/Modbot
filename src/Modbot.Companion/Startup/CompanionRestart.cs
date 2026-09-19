using Modbot.Companion.Pairing;

namespace Modbot.Companion.Startup;

/// <summary>
/// "Restart Modbot Companion": how a fresh copy is started, and how it waits for the copy it is
/// replacing to go.
/// </summary>
/// <remarks>
/// <para><strong>The client never launches a program.</strong> That ban is one of the things that
/// separates this client from the software it is shaped like, and the source guard fails the build
/// if it is broken. So the restart does what pairing already does: it asks Windows to open a
/// <c>modbot-companion://</c> address, and Windows — reading its own registration, which this client
/// wrote for itself — starts the program. The client starts nothing, inspects nothing, attaches to
/// nothing. On a machine where the registration was refused there is simply no handler, the open
/// fails, and the running client says so and carries on reporting.</para>
/// <para><strong>Why the fresh copy waits.</strong> One copy runs per Windows account: a second copy
/// that finds the single-copy lock taken hands its link to the running one and exits. A copy started
/// by the restart address would do exactly that, and nothing would have restarted. So the address is
/// recognised before the single-copy check and the fresh copy waits for the lock instead.</para>
/// <para>Waiting on the lock waits for the old copy's process to end, which matters for more than
/// the lock itself: the pairing link inbox is a named pipe with room for one server, and a copy that
/// started while the old pipe was still open would run for the rest of its life with no inbox —
/// pairing from a browser would quietly stop working. Both are freed by the same exit.</para>
/// <para>Nothing here reads the disk or the network. It compares a string and waits on a handle.</para>
/// </remarks>
public static class CompanionRestart
{
    /// <summary>
    /// What the Settings page's Restart button asks Windows to open. Not a pairing link: it carries
    /// no token and means only "start a copy, and wait for me to go".
    /// </summary>
    public const string Link = PairingToken.Scheme + "://restart";

    /// <summary>
    /// How long a fresh copy waits for the old one. Generous, because the old copy is closing a
    /// SteamVR overlay and finishing whatever it was sending; short enough that a copy which is
    /// never going to exit does not sit there for the rest of the day.
    /// </summary>
    public static readonly TimeSpan HowLongToWait = TimeSpan.FromSeconds(30);

    /// <summary>Whether this copy was started to replace one that is quitting.</summary>
    public static bool IsRestartLink(string? argument)
        => argument is not null
           && string.Equals(argument.Trim().TrimEnd('/'), Link, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Waits for the copy that is quitting to let go of the single-copy lock.
    /// </summary>
    /// <remarks>
    /// A lock the old copy left behind by dying rather than releasing counts as the old copy being
    /// gone, because it is; .NET hands ownership over with that exception either way.
    /// </remarks>
    /// <returns>True when this copy now holds the lock and should start normally.</returns>
    public static bool WaitForTheOldCopyToGo(WaitHandle singleCopyLock, TimeSpan? howLong = null)
    {
        ArgumentNullException.ThrowIfNull(singleCopyLock);

        try
        {
            return singleCopyLock.WaitOne(howLong ?? HowLongToWait);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
