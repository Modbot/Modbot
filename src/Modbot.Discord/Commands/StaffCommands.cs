using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Live;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.Instances;
using Modbot.Discord.Interactions;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Commands;

/// <summary>What one of the staff commands did, for the reply and for the access record.</summary>
/// <param name="Outcome">
/// <c>answered</c> when it did what was asked; <c>refused</c> when Modbot's own rules said no (an
/// empty note, a person already watched); <c>invalid</c> when the options did not name exactly one
/// person Modbot knows.
/// </param>
/// <param name="Target">The VRChat account the command was about.</param>
/// <param name="DiscordTarget">The Discord account the command was about.</param>
public sealed record StaffCommandAnswer(DiscordReply Reply, string Outcome, string? Target = null, string? DiscordTarget = null);

/// <summary>
/// <c>/note</c>, <c>/watch</c> and <c>/live</c>: the staff commands that are thin wrappers over what
/// the web app already does (Discord commands design §4, step 2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The caller is already known.</strong> <see cref="DiscordCommandHandler"/> has found the
/// Modbot account behind the Discord account, refused one that is disabled, has no VRChat link
/// (for a command that writes) or lacks the permission, and writes the
/// <c>modbot.discord.command</c> fact when this returns. What is left is the work.
/// </para>
/// <para>
/// <strong>A note and a watch are the web app's.</strong> They go through
/// <see cref="IStaffActions"/>, which the API implements with <c>NoteService</c> and
/// <c>WatchService</c>, so the facts, the limits and the permission check are the ones a press in
/// the web app makes. The service checks the permission again.
/// </para>
/// <para>
/// <strong>One person, named one way.</strong> <c>member</c> is a Discord member picked from
/// Discord's list; <c>vrchat</c> is a VRChat name or id. Discord cannot require "one of", so
/// <see cref="PersonAsync"/> refuses none and both.
/// </para>
/// <para>
/// <strong><c>/live</c> shows what the Live page shows, less.</strong> Every open group instance with
/// its world, head count and opening time. Names appear only while a moderator's companion is
/// watching that instance, by the same rule (<see cref="InstanceWatching"/>) the Live page and the
/// instance card use. No flags and no watched tags: a Discord reply is easy to read over a
/// shoulder, and Modbot keeps those on its own pages (user decision 2026-10-01).
/// </para>
/// </remarks>
public sealed class StaffCommands
{
    public const string NoteSavedMessage = "Note saved.";
    public const string EmptyNoteMessage = "A note needs something in it.";
    public const string NeedsAReasonMessage = "A watch needs a reason.";
    public const string PickHowLongMessage = "Pick how long to watch.";
    public const string PickOneMessage = "Pick a VRChat name or a Discord member.";
    public const string PickOnlyOneMessage = "Pick a VRChat name or a Discord member, not both.";
    public const string NoGroupMessage = "No group is set up in Modbot yet.";
    public const string NoInstancesMessage = "No instances are open right now.";
    /// <summary>
    /// What a caller without See profiles is told for anything but an id Modbot has: the same
    /// sentence for a name, a typo and an id Modbot has never seen, so it says nothing about who is
    /// in the records.
    /// </summary>
    public const string NeedsAnIdMessage = "Use the person's VRChat id. A name needs the \"See profiles\" permission in Modbot.";

    public const string OpenModbotLabel = "Open in Modbot";
    public const string OpenLiveLabel = "Open Live";

    /// <summary>Discord shows at most ten cards on one message.</summary>
    public const int MostCards = 10;

    /// <summary>Discord refuses a message whose cards hold more than this many characters between them.</summary>
    public const int CardCharacterLimit = 6000;

    /// <summary>Under <see cref="CardCharacterLimit"/>, with room for the message's own words.</summary>
    private const int CardCharacterBudget = 5600;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly LookupQuery _lookup;
    private readonly IStaffActions? _staff;

    public StaffCommands(ModbotContext db, IModbotClock clock, LookupQuery lookup, IStaffActions? staff = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lookup);

        _db = db;
        _clock = clock;
        _lookup = lookup;
        _staff = staff;
    }

    // ── Who the command is about ─────────────────────────────────────────────────────────────

    /// <summary>One person a command is about.</summary>
    /// <param name="Name">
    /// What Modbot calls them, when it knows and the caller may see it. Null for a Discord member
    /// (the command gets an id) and for a VRChat person named to a caller without See profiles.
    /// </param>
    public sealed record Person(FactPlatform Platform, string Id, string? Name);

    /// <summary>
    /// The person named by exactly one of <c>member</c> and <c>vrchat</c>, or the reply that says
    /// why not. The one rule for every command that names a person: <c>/note</c>, <c>/watch</c>,
    /// <c>/ban</c> and <c>/kick</c> (Discord commands design §3.7).
    /// </summary>
    /// <param name="seesNames">
    /// Whether the caller may see profiles. A VRChat name is a profile's: the web app shows it only
    /// with See profiles (<c>PersonSight.VRChatName</c>). Without it the <c>vrchat</c> option takes an
    /// exact id only, no name is searched or shown, and every refusal is one sentence.
    /// </param>
    public async Task<(Person? Person, StaffCommandAnswer? Problem)> PersonAsync(
        DiscordCommandCall call, bool seesNames, CancellationToken ct)
    {
        var member = call.Option(DiscordCommands.MemberOption)?.Trim() ?? string.Empty;
        var query = call.Option(DiscordCommands.VRChatOption)?.Trim() ?? string.Empty;

        if (member.Length > 0 && query.Length > 0)
            return (null, Invalid(PickOnlyOneMessage));

        if (member.Length == 0 && query.Length == 0)
            return (null, Invalid(PickOneMessage));

        if (member.Length > 0)
            return (new Person(FactPlatform.Discord, member, null), null);

        if (!seesNames)
        {
            // An exact id or nothing. A name, a typo and an unknown id all get the same sentence,
            // and the person is never named back: the reply shows the id and the profile link only.
            var known = await _lookup.KnownIdAsync(query, ct).ConfigureAwait(false);

            return known
                ? (new Person(FactPlatform.VRChat, query, null), null)
                : (null, Invalid(NeedsAnIdMessage));
        }

        var matches = await _lookup.FindAsync(query, ct).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return (null, Invalid(
                $"Nobody in Modbot's records matches \"{ModerationEventEmbed.Fit(ModerationEventEmbed.Escape(query), 80)}\". "
                + "Modbot only knows people it has seen in the group's history."));
        }

        if (matches.Count > 1)
        {
            var publicAddress = await PublicAddressAsync(ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"Several people match \"{ModerationEventEmbed.Fit(ModerationEventEmbed.Escape(query), 80)}\". Run the command again with the id:");

            // The one list of people that still carries ids: the moderator is being asked to run
            // the command again with one, so here the id is the answer rather than decoration.
            foreach (var match in matches)
            {
                sb.Append('\n').Append("• ")
                    .Append(CardLink.Person(match.DisplayName, match.UserId, publicAddress))
                    .Append(" — `")
                    .Append(match.UserId.Replace("`", string.Empty, StringComparison.Ordinal))
                    .Append('`');
            }

            return (null, Invalid(sb.ToString()));
        }

        return (new Person(FactPlatform.VRChat, matches[0].UserId, matches[0].DisplayName), null);
    }

    private static StaffCommandAnswer Invalid(string text) => new(DiscordReply.Say(text), "invalid");

    private static string Who(Person person, string? publicAddress)
        => person.Platform == FactPlatform.VRChat
            ? CardLink.Person(person.Name, person.Id, publicAddress)
            : CardLink.DiscordPerson(person.Name, person.Id, publicAddress);

    /// <summary>The target fields of the access record: the VRChat id, or the Discord id.</summary>
    private static (string? Target, string? DiscordTarget) Targets(Person person)
        => person.Platform == FactPlatform.VRChat ? (person.Id, null) : (null, person.Id);

    /// <summary>Whether this caller may see a VRChat person's name: the web app's See profiles.</summary>
    public static bool SeesNames(ModbotUser user)
        => DiscordCommands.Allows(user.EffectivePermissions, ModbotPermissions.ViewProfile);

    private static StaffMember Member(ModbotUser user) => new(user.Id, user.Username, user.EffectivePermissions);

    // ── /note ────────────────────────────────────────────────────────────────────────────────

    public async Task<StaffCommandAnswer> NoteAsync(DiscordCommandCall call, ModbotUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);

        var (person, problem) = await PersonAsync(call, SeesNames(user), ct).ConfigureAwait(false);
        if (problem is not null)
            return problem;

        var (target, discordTarget) = Targets(person!);
        var text = call.Option(DiscordCommands.NoteTextOption)?.Trim() ?? string.Empty;

        if (text.Length == 0)
            return new StaffCommandAnswer(DiscordReply.Say(EmptyNoteMessage), "invalid", target, discordTarget);

        if (text.Length > DiscordCommands.NoteTextMax)
        {
            return new StaffCommandAnswer(
                DiscordReply.Say($"That note is too long (at most {DiscordCommands.NoteTextMax.ToString("N0", CultureInfo.InvariantCulture)} characters)."),
                "invalid", target, discordTarget);
        }

        if (_staff is null)
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage), "refused", target, discordTarget);

        var written = await _staff
            .WriteNoteAsync(person!.Platform, person.Id, text, Member(user), ct)
            .ConfigureAwait(false);

        if (!written.Written)
        {
            return new StaffCommandAnswer(
                DiscordReply.Say(written.Error ?? "The note could not be written."), "refused", target, discordTarget);
        }

        return new StaffCommandAnswer(await OpenInModbotAsync(NoteSavedMessage, person, ct).ConfigureAwait(false), "answered", target, discordTarget);
    }

    // ── /watch ───────────────────────────────────────────────────────────────────────────────

    public async Task<StaffCommandAnswer> WatchAsync(DiscordCommandCall call, ModbotUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);

        var (person, problem) = await PersonAsync(call, SeesNames(user), ct).ConfigureAwait(false);
        if (problem is not null)
            return problem;

        var (target, discordTarget) = Targets(person!);

        var reason = call.Option(DiscordCommands.WatchReasonOption)?.Trim() ?? string.Empty;

        if (reason.Length == 0)
            return new StaffCommandAnswer(DiscordReply.Say(NeedsAReasonMessage), "invalid", target, discordTarget);

        if (reason.Length > DiscordCommands.WatchReasonMax)
        {
            return new StaffCommandAnswer(
                DiscordReply.Say($"That reason is too long (at most {DiscordCommands.WatchReasonMax} characters)."),
                "invalid", target, discordTarget);
        }

        var now = _clock.UtcNow;

        DateTimeOffset? endsAt;

        switch (call.Option(DiscordCommands.WatchForOption))
        {
            case DiscordCommands.ForOneDay:
                endsAt = now.AddDays(1);
                break;
            case DiscordCommands.ForOneWeek:
                endsAt = now.AddDays(7);
                break;
            case DiscordCommands.ForThirtyDays:
                endsAt = now.AddDays(30);
                break;
            case DiscordCommands.ForUntilStopped:
                endsAt = null;
                break;
            default:
                return new StaffCommandAnswer(DiscordReply.Say(PickHowLongMessage), "invalid", target, discordTarget);
        }

        var followUpAt = call.Option(DiscordCommands.WatchFollowUpOption) switch
        {
            DiscordCommands.FollowUpTomorrow => now.AddDays(1),
            DiscordCommands.FollowUpInAWeek => now.AddDays(7),
            _ => (DateTimeOffset?)null,
        };

        if (_staff is null)
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage), "refused", target, discordTarget);

        var started = await _staff
            .StartWatchAsync(person!.Platform, person.Id, reason, endsAt, followUpAt, Member(user), ct)
            .ConfigureAwait(false);

        if (!started.Started)
        {
            return new StaffCommandAnswer(
                DiscordReply.Say(started.Error ?? "The watch could not be started."), "refused", target, discordTarget);
        }

        var publicAddress = await PublicAddressAsync(ct).ConfigureAwait(false);
        var text = new StringBuilder("Watching ").Append(Who(person, publicAddress));

        if (endsAt is { } until)
            text.Append(" until ").Append(DiscordTime.Day(until));

        text.Append('.');

        if (followUpAt is { } check)
            text.Append(" Check again ").Append(DiscordTime.Day(check)).Append('.');

        return new StaffCommandAnswer(
            WithProfileLink(text.ToString(), person, publicAddress), "answered", target, discordTarget);
    }

    private async Task<DiscordReply> OpenInModbotAsync(string text, Person person, CancellationToken ct)
        => WithProfileLink(text, person, await PublicAddressAsync(ct).ConfigureAwait(false));

    /// <summary>The words, with a button to the person's profile when Modbot has an address to link to.</summary>
    private static DiscordReply WithProfileLink(string text, Person person, string? publicAddress)
    {
        var url = CardLink.UrlFor(
            person.Platform == FactPlatform.VRChat ? CardSubject.Person : CardSubject.DiscordPerson,
            person.Id,
            publicAddress);

        return url is null
            ? DiscordReply.Say(text)
            : new DiscordReply(text, [], [new DiscordLinkButton(OpenModbotLabel, url)]);
    }

    // ── /live ────────────────────────────────────────────────────────────────────────────────

    public async Task<StaffCommandAnswer> LiveAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.ManagedGroupId, s.ManagedGroupName, s.PublicAddress })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
            return new StaffCommandAnswer(DiscordReply.Say(NoGroupMessage), "answered");

        // The Live page's own list: the group's open instances, oldest first.
        var instances = await _db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.SeenInGroupList && i.ClosedAt == null)
            .OrderBy(i => i.OpenedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
            return new StaffCommandAnswer(DiscordReply.Say(NoInstancesMessage), "answered");

        var shown = instances.Take(MostCards).ToList();

        var worldIds = shown.Select(i => i.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var worlds = await _db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var names = await NamesAsync(shown, ct).ConfigureAwait(false);

        var style = new CardStyle(settings.PublicAddress, settings.ManagedGroupName, BrandIcon.For(settings.PublicAddress));

        var cards = shown
            .Select(i => Card(i, worlds.GetValueOrDefault(i.WorldId), names.GetValueOrDefault(i.Id), style))
            .ToList();

        cards = FitTogether(cards);

        var openLive = string.IsNullOrWhiteSpace(settings.PublicAddress)
            ? null
            : new[] { new DiscordLinkButton(OpenLiveLabel, $"{settings.PublicAddress.TrimEnd('/')}{CardLink.PageFor(CardSubject.Instance)}") };

        var left = instances.Count - shown.Count;
        var text = left > 0 ? $"and {left.ToString(CultureInfo.InvariantCulture)} more open" : null;

        return new StaffCommandAnswer(new DiscordReply(text, cards, openLive), "answered");
    }

    /// <summary>
    /// The display names of the people in each of these instances that a moderator is watching.
    /// Instances nobody is watching are absent, so their cards show the head count only: Modbot does
    /// not know who is inside them, and an old list is not "here now".
    /// </summary>
    /// <remarks>
    /// The rule of the Live page and of the instance card (<see cref="InstanceWatching"/>). A person
    /// the facts carried no name for is named from their stored profile when there is one, and
    /// otherwise left as a null the list counts in "and N more" -- never shown by id.
    /// </remarks>
    private async Task<Dictionary<Guid, IReadOnlyList<string?>>> NamesAsync(
        IReadOnlyList<VRChatInstance> instances, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string?>>();
        var people = await new InstancePeopleReader(_db).ForInstancesAsync(instances, ct).ConfigureAwait(false);

        var nameless = people.Values
            .Where(p => p.IsWatched)
            .SelectMany(p => p.Here)
            .Where(p => p.DisplayName is null)
            .Select(p => p.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var stored = nameless.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _db.VRChatUsers.AsNoTracking()
                .Where(u => nameless.Contains(u.UserId) && u.DisplayName != null)
                .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

        foreach (var (instanceId, inInstance) in people)
        {
            if (inInstance.IsWatched)
                result[instanceId] = inInstance.Here.Select(p => p.DisplayName ?? stored.GetValueOrDefault(p.UserId)).ToList();
        }

        return result;
    }

    /// <summary>
    /// One open instance: the world, how many are in, and when it opened -- and, while it is being
    /// watched, who. Public so its words can be tested without a database.
    /// </summary>
    /// <param name="names">Who is here, when a moderator is watching. Null otherwise.</param>
    public static DiscordEmbedContent Card(
        VRChatInstance instance, VRChatWorld? world, IReadOnlyList<string?>? names, CardStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(instance);

        style ??= CardStyle.None;

        var people = HeadCounts.Shown(instance);
        var unsure = HeadCounts.ShownUnsure(instance);

        // "12/40" the way the game shows it; the world's capacity is what its page says, shown and
        // never enforced. A count taken from n_users carries a "?", as it does in Modbot.
        var count = (people is { } n ? n.ToString(CultureInfo.InvariantCulture) + (unsure ? "?" : string.Empty) : "?")
            + (world?.Capacity is > 0 and var capacity ? "/" + capacity.ToString(CultureInfo.InvariantCulture) : string.Empty);

        var fields = new List<DiscordEmbedField>
        {
            new("People", count, Inline: true),
            new("Opened", DiscordTime.Relative(instance.OpenedAt), Inline: true),
        };

        if (names is { Count: > 0 } && InstanceCard.NameList(names) is { } list)
            fields.Add(new DiscordEmbedField("Who is here", list));

        return new DiscordEmbedContent(
            Title: CardText.Plain(world?.Name is { Length: > 0 } name ? name : instance.WorldId, 256),
            Description: instance.Name is { Length: > 0 } called ? CardText.Fit(CardText.EscapeText(called.Trim()), 200) : null,
            Color: (people ?? 0) > 0 ? CardColour.Green : CardColour.Grey,
            Fields: fields,
            Timestamp: null,
            Url: null,
            Footer: style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl);
    }

    /// <summary>
    /// Discord refuses a message whose cards hold more than 6,000 characters between them. A busy
    /// night with several full instances could pass that, so the lists of names are dropped from the
    /// last cards first until it fits: every card keeps its world, count and opening time.
    /// </summary>
    public static List<DiscordEmbedContent> FitTogether(List<DiscordEmbedContent> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var fitted = new List<DiscordEmbedContent>(cards);

        for (var i = fitted.Count - 1; i >= 0 && Length(fitted) > CardCharacterBudget; i--)
        {
            var kept = fitted[i].Fields.Where(f => f.Name != "Who is here").ToList();

            if (kept.Count != fitted[i].Fields.Count)
                fitted[i] = fitted[i] with { Fields = kept };
        }

        return fitted;
    }

    private static int Length(IEnumerable<DiscordEmbedContent> cards)
        => cards.Sum(c => c.Title.Length
            + (c.Description?.Length ?? 0)
            + (c.Footer?.Length ?? 0)
            + (c.AuthorName?.Length ?? 0)
            + c.Fields.Sum(f => f.Name.Length + f.Value.Length));

    private async Task<string?> PublicAddressAsync(CancellationToken ct)
        => await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.PublicAddress)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
