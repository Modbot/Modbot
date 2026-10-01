using Modbot.Api.Features.People;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// The accounts one person's timeline is read across. Any of the three can be missing.
/// </summary>
/// <param name="VRChat">Their VRChat user id. Never parsed (foundation §3.1.1).</param>
/// <param name="Discord">Their Discord user id.</param>
/// <param name="Account">Their Modbot account's id, as the log stores it.</param>
public sealed record PersonIds(string? VRChat, string? Discord, string? Account);

/// <summary>
/// Which accounts a person's timeline covers, worked out on the server from the one the caller
/// named (one view per person design §3 and §4).
/// </summary>
/// <remarks>
/// <para>
/// The person popup used to read five pages of the log -- what was done to and by each account --
/// and merge them in the browser, which meant a fixed cut of fifty and no way to page past it. One
/// query over all of a person's accounts can be paged by the log's own cursor, because the merge is
/// in the <c>WHERE</c> rather than after it.
/// </para>
/// <para>
/// <strong>Tied the way the popup ties them, and no further.</strong> <see cref="PersonLookup"/>
/// decides, with the caller's own <see cref="PersonSight"/>: a Discord account is only added for
/// somebody who may be told the two are linked, and a Modbot account only for somebody who may be
/// told who holds it. A caller who may not know the link gets the account they named and nothing
/// else, which is exactly what the log's <c>subject</c> and <c>actor</c> filters already give them.
/// The id that was named is always kept, whatever the lookup could tie to it.
/// </para>
/// </remarks>
public static class PersonTimeline
{
    public static async Task<PersonIds> ResolveAsync(
        ModbotContext db,
        ModbotPermissions held,
        string id,
        FactPlatform platform,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var ask = platform switch
        {
            FactPlatform.Discord => new PersonAsk(null, id, null),
            FactPlatform.Modbot => new PersonAsk(null, null, Guid.TryParse(id, out var account) ? account : null),
            _ => new PersonAsk(id, null, null),
        };

        // A Modbot account id that is not one cannot belong to an account, so there is nobody to
        // tie it to; it is still searched for as given, the way `account` would be.
        var view = ask == default
            ? new PersonView(null, null, null, false)
            : await new PersonLookup(db).ResolveAsync(ask, PersonSight.Of(held), ct);

        return new PersonIds(
            view.VRChat?.Id ?? (platform == FactPlatform.VRChat ? id : null),
            view.Discord?.Id ?? (platform == FactPlatform.Discord ? id : null),
            view.Account?.Id.ToString() ?? (platform == FactPlatform.Modbot ? id : null));
    }
}
