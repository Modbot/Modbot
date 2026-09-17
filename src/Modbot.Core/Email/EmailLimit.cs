using Modbot.Core.Data.Entities;

namespace Modbot.Core.Email;

/// <summary>
/// The daily email limit's rules, with no database and no clock (accounts and access design §4.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A rolling 24 hours, not a calendar day.</strong> Relays that cap sending count that
/// way, and a calendar day would let a deployment send its whole limit at 23:59 and again at
/// 00:01.
/// </para>
/// <para>
/// <strong>Room is kept for account email.</strong> Other email may go out only while fewer than
/// <c>limit − <see cref="KeptForAccountEmails"/></c> emails of any kind have gone out in the
/// window; account email may go out while fewer than the limit have. So however much other email
/// is asked for, the last <see cref="KeptForAccountEmails"/> are always there for somebody locked
/// out of their account.
/// </para>
/// </remarks>
public static class EmailLimit
{
    public const int Default = 100;

    /// <summary>The lowest limit that can be saved. Equal to the room kept for account email.</summary>
    public const int Minimum = 20;

    public const int KeptForAccountEmails = 20;

    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>How many sends in the window a message of this kind may find and still go out.</summary>
    public static int RoomFor(EmailKind kind, int limit)
    {
        var clamped = Math.Max(Minimum, limit);
        return kind == EmailKind.Account ? clamped : clamped - KeptForAccountEmails;
    }

    public static string NameOf(EmailKind kind) => kind == EmailKind.Account ? EmailKinds.Account : EmailKinds.Other;

    public static EmailKind KindOf(string? name) =>
        string.Equals(name, EmailKinds.Account, StringComparison.Ordinal) ? EmailKind.Account : EmailKind.Other;

    /// <summary>
    /// When a message would go out, given what has been sent and what is waiting ahead of it.
    /// </summary>
    /// <param name="sentInWindow">When each email in the last 24 hours went out, any order.</param>
    /// <param name="ahead">The kinds of the queued messages that go before this one, in queue order.</param>
    /// <returns>
    /// <c>(false, now)</c> when it can go now: nothing is ahead and there is room. Otherwise
    /// <c>(true, time)</c>, with a null time when the limit leaves its kind no room at all.
    /// </returns>
    /// <remarks>
    /// Walks the queue one message at a time: each goes out at the first moment the window has
    /// room for its kind, which is the moment the send that filled it turns 24 hours old. An
    /// estimate, because account email asked for later jumps ahead of other email.
    /// </remarks>
    public static (bool Queue, DateTimeOffset? At) WhenCanSend(
        IEnumerable<DateTimeOffset> sentInWindow,
        IReadOnlyList<EmailKind> ahead,
        EmailKind kind,
        int limit,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sentInWindow);
        ArgumentNullException.ThrowIfNull(ahead);

        var times = sentInWindow.Where(t => t > now - Window).Order().ToList();

        if (ahead.Count == 0 && times.Count < RoomFor(kind, limit))
            return (false, now);

        var at = now;

        foreach (var next in ahead.Append(kind))
        {
            var instance = RoomFor(next, limit);
            if (instance <= 0)
                return (true, null);

            // Everything in the list is at or before `at`, so the count in the window at `at` is
            // the number after at − 24h, and the send that has to age out is the instance-th newest.
            var inWindow = times.Count(t => t > at - Window);
            if (inWindow >= instance)
                at = times[times.Count - instance] + Window;

            times.Add(at);
        }

        return (true, at);
    }
}
