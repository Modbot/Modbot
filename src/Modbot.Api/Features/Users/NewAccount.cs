using Microsoft.AspNetCore.Http;
using Modbot.Core.Cloud;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Users;

/// <summary>
/// The two things every account creation does the same way: check the email, and honour the
/// updates checkbox.
/// </summary>
/// <remarks>
/// Four places make accounts -- the wizard's first administrator, the users page, an accepted
/// invite, and the demo -- and the rules are the same in all of them (server info and account
/// email design §4, §5). One file, so a fifth place cannot get them subtly wrong.
/// </remarks>
public static class NewAccount
{
    /// <summary>
    /// The address to store, or the answer to hand back. Checks the shape and that no other
    /// account already holds it.
    /// </summary>
    public static async Task<(string? Email, IResult? Problem)> ReadEmailAsync(
        UserAccountService accounts,
        string? typed,
        Guid? exceptUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var (email, problem) = EmailAddress.Read(typed);

        if (problem is not null)
            return (null, Results.BadRequest(new { error = problem }));

        if (await accounts.EmailTakenAsync(email!, exceptUserId, ct))
            return (null, Results.Conflict(new { error = EmailAddress.Taken }));

        return (email, null);
    }

    /// <summary>
    /// Records that this person asked for the project's news and asks Modbot Cloud to add them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after the account has been committed, never inside its transaction: the Cloud call is
    /// a network round trip, and holding a database transaction open across one is how a slow
    /// mailing list becomes a slow sign-up.
    /// </para>
    /// <para>
    /// Awaited rather than left running, because the person's request is what owns the scope this
    /// resolves its database context from. The call has its own five-second ceiling and swallows
    /// everything, so waiting on it cannot fail the account creation that has already happened.
    /// </para>
    /// </remarks>
    public static async Task SubscribeAsync(
        IUpdatesSubscriber? subscriber,
        AccountFacts facts,
        ModbotUser user,
        bool asked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(user);

        if (!asked || subscriber is not { Available: true } || user.Email is not { Length: > 0 } email)
            return;

        // The fact first: what the person agreed to is worth recording whether or not Cloud was
        // reachable a moment later.
        await facts.RecordAsync(
            FactType.UpdatesSubscribed,
            user,
            new Actor(user.Id, user.Username),
            null,
            ct);

        await subscriber.SubscribeAsync(email, ct);
    }

    /// <summary>The subscriber this host registered, or null when it registered none.</summary>
    public static IUpdatesSubscriber? SubscriberOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.RequestServices.GetService(typeof(IUpdatesSubscriber)) as IUpdatesSubscriber;
    }

    /// <summary>Whether the account forms should show the updates checkbox at all.</summary>
    public static bool CanSubscribe(HttpContext http) => SubscriberOf(http) is { Available: true };
}
