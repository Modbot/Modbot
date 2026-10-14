using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/term</c>: what a word the community uses means, from the terms staff type on the Terms and
/// rules page.
/// </summary>
/// <remarks>
/// <para>
/// For every member, with no Modbot account, and private like <c>/profile</c> and <c>/find</c>: a
/// member asking what a word means is not saying so to the channel. The word is picked from the
/// suggestions, which offer each term's name and its other names, or typed whole; a typed word
/// matches a name or an other name exactly, ignoring case. A word no term has is told so, in a few
/// words.
/// </para>
/// <para>
/// The answer is the name, its other names and the meaning as staff wrote them. The page keeps all
/// of that inside one Discord message (<see cref="GlossaryTerm"/>).
/// </para>
/// </remarks>
public sealed class TermCommand
{
    /// <summary>The key of the per-person limit, apart from the other commands'.</summary>
    public const string LimitsKey = "term";

    public const string TooFastMessage = "Slow down. Try again in a minute.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public TermCommand(
        ModbotContext db,
        IModbotClock clock,
        [FromKeyedServices(LimitsKey)] MemberCommandLimits limits)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);

        _db = db;
        _clock = clock;
        _limits = limits;
    }

    /// <summary>Counts one use for this person and says whether it is allowed. See <see cref="MemberCommandLimits"/>.</summary>
    public bool TryUse(string discordUserId) => _limits.TryUse(discordUserId, _clock.UtcNow);

    /// <summary>The reply for a word no term has.</summary>
    public static string NoTermMessage(string typed) => $"No term called {typed}.";

    /// <summary>The answer for a term: <c>**Regent** (also: Reggie)</c>, then the meaning.</summary>
    public static string Answer(GlossaryTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var heading = term.OtherNames.Length == 0
            ? $"**{term.Name}**"
            : $"**{term.Name}** (also: {string.Join(", ", term.OtherNames)})";

        return $"{heading}\n{term.Meaning}";
    }

    /// <summary>Runs <c>/term</c>. The caller has already checked the switch and counted the use.</summary>
    public async Task<DiscordReply> RunAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var typed = (call.Option(DiscordCommands.TermNameOption) ?? string.Empty).Trim();

        var terms = await _db.GlossaryTerms.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        var found = terms.FirstOrDefault(t =>
            string.Equals(t.Name, typed, StringComparison.OrdinalIgnoreCase)
            || t.OtherNames.Contains(typed, StringComparer.OrdinalIgnoreCase));

        return found is null
            ? DiscordReply.Say(NoTermMessage(typed))
            : DiscordReply.Say(Answer(found));
    }

    /// <summary>
    /// What to suggest under <c>name</c> while a word is typed: each term's name, and each other
    /// name with the term it belongs to. Names that start with what was typed come first. Nothing
    /// here is about a member.
    /// </summary>
    public async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(DiscordSuggestionAsk ask, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var word = ask.Typed.Trim();
        var terms = await _db.GlossaryTerms.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        var offered = terms
            .SelectMany(t => new[] { (Shown: t.Name, Value: t.Name) }
                .Concat(t.OtherNames.Select(o => (Shown: $"{o} (see {t.Name})", Value: o))))
            .Where(n => n.Value.Contains(word, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Value.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n.Value, StringComparer.CurrentCultureIgnoreCase)
            .Take(DiscordSuggestion.Most)
            .Select(n => new DiscordSuggestion(Shorten(n.Shown), n.Value));

        return [.. offered];
    }

    private static string Shorten(string text)
        => text.Length <= DiscordSuggestion.Longest ? text : text[..(DiscordSuggestion.Longest - 1)] + "…";
}
