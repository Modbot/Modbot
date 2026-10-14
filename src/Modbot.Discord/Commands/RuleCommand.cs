using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/rule</c>: one of the community's rules, from the rules staff type on the Terms and rules
/// page.
/// </summary>
/// <remarks>
/// <para>
/// For every member, with no Modbot account, and private like <c>/term</c>. The rule is picked from
/// the suggestions, which list each rule as its number and title, or typed as a number or as its
/// title. The number is the rule's place on the page, so moving a rule changes it.
/// </para>
/// <para>
/// The answer is the number, the title and the text, which the page keeps inside one Discord
/// message (<see cref="ServerRule"/>).
/// </para>
/// </remarks>
public sealed class RuleCommand
{
    /// <summary>The key of the per-person limit, apart from the other commands'.</summary>
    public const string LimitsKey = "rule";

    public const string TooFastMessage = "Slow down. Try again in a minute.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public RuleCommand(
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

    /// <summary>The reply for a rule that is not there: a number past the last, or a title no rule has.</summary>
    public static string NoRuleMessage(string typed)
        => int.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? $"There is no rule {typed}."
            : $"No rule called {typed}.";

    /// <summary>The answer for a rule: <c>**Rule 3: Be kind**</c>, then the text.</summary>
    public static string Answer(int number, ServerRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return $"**Rule {number.ToString(CultureInfo.InvariantCulture)}: {rule.Title}**\n{rule.Text}";
    }

    private Task<List<ServerRule>> InOrderAsync(CancellationToken ct)
        => _db.ServerRules.AsNoTracking()
            .OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);

    /// <summary>Runs <c>/rule</c>. The caller has already checked the switch and counted the use.</summary>
    public async Task<DiscordReply> RunAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var typed = (call.Option(DiscordCommands.RuleWhichOption) ?? string.Empty).Trim();
        var rules = await InOrderAsync(ct).ConfigureAwait(false);

        var at = int.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number - 1
            : rules.FindIndex(r => string.Equals(r.Title, typed, StringComparison.OrdinalIgnoreCase));

        return at < 0 || at >= rules.Count
            ? DiscordReply.Say(NoRuleMessage(typed))
            : DiscordReply.Say(Answer(at + 1, rules[at]));
    }

    /// <summary>
    /// What to suggest under <c>rule</c> while a number or a title is typed: each rule as its number
    /// and title, in order. The value is the number.
    /// </summary>
    public async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(DiscordSuggestionAsk ask, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var word = ask.Typed.Trim();
        var rules = await InOrderAsync(ct).ConfigureAwait(false);

        var offered = rules
            .Select((r, at) => (Number: (at + 1).ToString(CultureInfo.InvariantCulture), r.Title))
            .Where(r => r.Number.StartsWith(word, StringComparison.Ordinal)
                        || r.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
            .Take(DiscordSuggestion.Most)
            .Select(r => new DiscordSuggestion(Shorten($"{r.Number}. {r.Title}"), r.Number));

        return [.. offered];
    }

    private static string Shorten(string text)
        => text.Length <= DiscordSuggestion.Longest ? text : text[..(DiscordSuggestion.Longest - 1)] + "…";
}
