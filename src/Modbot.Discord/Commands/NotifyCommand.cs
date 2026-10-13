using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/notify</c> and the <em>Notify me</em> button under an event's channel post: how a member signs
/// up to be told before events (event alerts design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>For every member, with no Modbot account</strong>, like <c>/remindme</c>; the replies are
/// private. The command is not on the Commands card: it exists in Discord while alerts are on
/// (<see cref="DiscordCommands.For"/>), and refuses with a plain sentence if it is run after they
/// were switched off.
/// </para>
/// <para>
/// <strong>/notify</strong> opens a form with the Modbot categories, the ones the member already
/// signed up for ticked, and, only when both ways of being told are on, the way. Sending it replaces
/// the member's category sign-ups with what is ticked. A list holds 25 choices, so with more
/// categories the first 25 by name are offered, and a sign-up for any other is left as it is.
/// </para>
/// <para>
/// <strong>The button</strong> signs the member up for the event's categories, or for the event
/// itself when it carries none. Pressed again by someone signed up for all of it, it takes those
/// sign-ups off. Only an event <c>/events</c> would show can be signed up for.
/// </para>
/// <para>
/// Both count against one per-person limit (<see cref="LimitsKey"/>), apart from the other commands'.
/// </para>
/// </remarks>
public sealed class NotifyCommand
{
    /// <summary>The key of the per-person limit, apart from <c>/remindme</c>'s and the others'.</summary>
    public const string LimitsKey = "notify";

    /// <summary>The form's id.</summary>
    public const string FormId = DiscordActionButton.Prefix + "form:notify";

    /// <summary>The form's list of categories.</summary>
    public const string CategoriesField = "categories";

    /// <summary>The form's list of ways, there only when both ways are on.</summary>
    public const string WayField = "way";

    /// <summary>The button's id is this and the event's id: <c>modbot:notify:&lt;event id&gt;</c>.</summary>
    public const string ButtonPrefix = DiscordActionButton.Prefix + "notify:";

    public const string ButtonLabel = "Notify me";

    /// <summary>A list holds 25 choices.</summary>
    public const int MostCategories = 25;

    public const string TooFastMessage = "Slow down. Try again in a minute.";
    public const string OffMessage = "Event alerts are off.";
    public const string NoCategoriesMessage = "There are no categories to sign up for.";
    public const string NoEventMessage = "Modbot can't find that event.";
    public const string NothingMessage = "You will not be told about anything.";

    public const string DirectLabel = "Direct message";
    public const string PingLabel = "Ping in a channel";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public NotifyCommand(
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

    public static bool IsForm(string formId) => formId == FormId;

    public static bool IsButton(string buttonId) => buttonId.StartsWith(ButtonPrefix, StringComparison.Ordinal);

    /// <summary>The <em>Notify me</em> button for an event's channel post.</summary>
    public static DiscordActionButton ButtonFor(Guid eventId)
        => new(ButtonLabel, ButtonPrefix + eventId.ToString("D"));

    // ── The settings ─────────────────────────────────────────────────────────────────────────

    private sealed record Ways(bool On, bool Direct, bool Ping);

    private async Task<Ways> ReadWaysAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new Ways(s.EventAlertsOn, s.EventAlertsDirectMessage, s.EventAlertsPing))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return settings ?? new Ways(false, false, false);
    }

    // ── /notify ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>/notify</c>: shows the form. Null when it did; a reply when it could not.
    /// </summary>
    public async Task<DiscordReply?> RunAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!TryUse(call.DiscordUserId))
            return DiscordReply.Say(TooFastMessage);

        var ways = await ReadWaysAsync(ct).ConfigureAwait(false);
        if (!ways.On)
            return DiscordReply.Say(OffMessage);

        var categories = await OfferedAsync(ct).ConfigureAwait(false);
        if (categories.Count == 0)
            return DiscordReply.Say(NoCategoriesMessage);

        var offered = categories.Select(c => c.Id).ToList();
        var ticked = await _db.EventAlertSignups.AsNoTracking()
            .Where(s => s.DiscordUserId == call.DiscordUserId && s.CategoryId != null && offered.Contains(s.CategoryId.Value))
            .Select(s => s.CategoryId!.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var way = await _db.EventAlertMembers.AsNoTracking()
            .Where(m => m.DiscordUserId == call.DiscordUserId)
            .Select(m => m.Way)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        await call.ShowFormAsync(Form(categories, ticked.ToHashSet(), ways, way), ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The form: the categories, and the way where there are two.</summary>
    private static DiscordForm Form(
        IReadOnlyList<CalendarCategory> categories, IReadOnlySet<Guid> ticked, Ways ways, string? way)
    {
        var fields = new List<DiscordFormField>
        {
            new(
                CategoriesField,
                "Categories",
                DiscordFormFieldKind.Choice,
                Required: false,
                Choices: [.. categories.Select(c => new DiscordChoice(c.Name, c.Id.ToString("D"), IsDefault: ticked.Contains(c.Id)))],
                MaxChoices: categories.Count),
        };

        if (ways.Direct && ways.Ping)
        {
            var ping = way == EventAlertSettings.Ping;

            fields.Add(new(
                WayField,
                "How to tell me",
                DiscordFormFieldKind.Choice,
                Required: true,
                Choices:
                [
                    new DiscordChoice(DirectLabel, EventAlertSettings.Direct, IsDefault: !ping),
                    new DiscordChoice(PingLabel, EventAlertSettings.Ping, IsDefault: ping),
                ],
                MaxChoices: 1));
        }

        return new DiscordForm("Event alerts", FormId, fields);
    }

    /// <summary>The categories the form offers: the first 25 by name.</summary>
    private Task<List<CalendarCategory>> OfferedAsync(CancellationToken ct)
        => _db.CalendarCategories.AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Take(MostCategories)
            .ToListAsync(ct);

    /// <summary>The form was sent: replaces the member's category sign-ups with what is ticked.</summary>
    public async Task<DiscordReply> HandleFormAsync(DiscordFormSubmit submit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submit);

        var ways = await ReadWaysAsync(ct).ConfigureAwait(false);
        if (!ways.On)
            return DiscordReply.Say(OffMessage);

        var now = _clock.UtcNow;
        var user = submit.DiscordUserId;
        var offered = await OfferedAsync(ct).ConfigureAwait(false);
        var offeredIds = offered.Select(c => c.Id).ToHashSet();

        // What is ticked, but only among the categories that were on offer: an id from anywhere
        // else names nothing the member could have ticked.
        var picked = submit.Picked(CategoriesField)
            .Select(v => Guid.TryParseExact(v, "D", out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Where(offeredIds.Contains)
            .ToHashSet();

        var held = await _db.EventAlertSignups
            .Where(s => s.DiscordUserId == user && s.CategoryId != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var signup in held.Where(s => offeredIds.Contains(s.CategoryId!.Value) && !picked.Contains(s.CategoryId.Value)))
            _db.EventAlertSignups.Remove(signup);

        var already = held.Select(s => s.CategoryId!.Value).ToHashSet();
        foreach (var id in picked.Where(p => !already.Contains(p)))
        {
            _db.EventAlertSignups.Add(new EventAlertSignup { DiscordUserId = user, CategoryId = id, CreatedAt = now });
        }

        // The way is chosen only where there are two to choose from.
        var way = ways.Direct && ways.Ping ? submit.Picked(WayField).FirstOrDefault() : null;
        if (EventAlertSettings.IsWay(way))
        {
            var row = await _db.EventAlertMembers.FirstOrDefaultAsync(m => m.DiscordUserId == user, ct).ConfigureAwait(false);
            if (row is null)
                _db.EventAlertMembers.Add(new EventAlertMember { DiscordUserId = user, Way = way!, UpdatedAt = now });
            else
            {
                row.Way = way!;
                row.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Said back from what is stored, so it is true whatever the form carried.
        var names = offered.Where(c => picked.Contains(c.Id)).Select(c => c.Name).ToList();
        var others = await _db.EventAlertSignups.AsNoTracking()
            .CountAsync(s => s.DiscordUserId == user && s.CategoryId != null && !offeredIds.Contains(s.CategoryId.Value), ct)
            .ConfigureAwait(false);
        var events = await _db.EventAlertSignups.AsNoTracking()
            .CountAsync(s => s.DiscordUserId == user && s.EventId != null, ct)
            .ConfigureAwait(false);

        return DiscordReply.Say(SignedUpWords(names, others + events, await WayWordsAsync(user, ways, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// What the member will be told about: <c>You will be told before events in A, B. By direct
    /// message.</c> Or that they will be told about nothing. <paramref name="more"/> counts the
    /// sign-ups the form does not show: single events, and categories past the first 25.
    /// </summary>
    public static string SignedUpWords(IReadOnlyList<string> categoryNames, int more, string? way)
    {
        if (categoryNames.Count == 0 && more == 0)
            return NothingMessage;

        var text = categoryNames.Count == 0
            ? "You will not be told about any category."
            : $"You will be told before events in {string.Join(", ", categoryNames.Select(RemindMeCommand.Escaped))}.";

        if (more > 0)
            text += $" You also have {more.ToString(CultureInfo.InvariantCulture)} other sign-up{(more == 1 ? string.Empty : "s")}.";

        return way is null ? text : text + " " + way;
    }

    /// <summary>"By direct message." or "By a ping in a channel." -- null where the member has no choice to hear about.</summary>
    private async Task<string?> WayWordsAsync(string user, Ways ways, CancellationToken ct)
    {
        if (!(ways.Direct && ways.Ping))
            return null;

        var way = await _db.EventAlertMembers.AsNoTracking()
            .Where(m => m.DiscordUserId == user)
            .Select(m => m.Way)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return way == EventAlertSettings.Ping ? "By a ping in a channel." : "By direct message.";
    }

    // ── The button ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <em>Notify me</em> was pressed: signs the member up for the event's categories, or the event
    /// when it has none; pressed by someone already signed up for all of it, takes that off.
    /// </summary>
    public async Task<DiscordReply> HandleButtonAsync(DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (!IsButton(press.ButtonId)
            || !Guid.TryParseExact(press.ButtonId[ButtonPrefix.Length..], "D", out var eventId))
        {
            return DiscordReply.Say(NoEventMessage);
        }

        if (!TryUse(press.DiscordUserId))
            return DiscordReply.Say(TooFastMessage);

        var ways = await ReadWaysAsync(ct).ConfigureAwait(false);
        if (!ways.On)
            return DiscordReply.Say(OffMessage);

        // Only an event /events would show, so the button cannot name one the server does not.
        var calendarEvent = await EventsCommand.Listed(_db)
            .Include(e => e.Categories)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct)
            .ConfigureAwait(false);

        if (calendarEvent is null)
            return DiscordReply.Say(NoEventMessage);

        var user = press.DiscordUserId;
        var categoryIds = calendarEvent.Categories.Select(c => c.Id).ToList();
        var byCategory = categoryIds.Count > 0;

        var held = await _db.EventAlertSignups
            .Where(s => s.DiscordUserId == user
                && (byCategory
                    ? s.CategoryId != null && categoryIds.Contains(s.CategoryId.Value)
                    : s.EventId == eventId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var wanted = byCategory ? categoryIds : [eventId];
        var heldIds = held.Select(s => byCategory ? s.CategoryId!.Value : s.EventId!.Value).ToHashSet();
        var removing = wanted.All(heldIds.Contains);

        if (removing)
            _db.EventAlertSignups.RemoveRange(held);
        else
        {
            var now = _clock.UtcNow;
            foreach (var id in wanted.Where(w => !heldIds.Contains(w)))
            {
                _db.EventAlertSignups.Add(new EventAlertSignup
                {
                    DiscordUserId = user,
                    CategoryId = byCategory ? id : null,
                    EventId = byCategory ? null : id,
                    CreatedAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var names = byCategory
            ? string.Join(", ", calendarEvent.Categories.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => RemindMeCommand.Escaped(c.Name)))
            : null;
        var title = RemindMeCommand.Escaped(calendarEvent.Title);

        return DiscordReply.Say(removing
            ? (byCategory
                ? $"You will no longer be told before events in {names}."
                : $"You will no longer be told before {title}.")
            : (byCategory
                ? $"You will be told before events in {names}."
                : $"You will be told before {title}."));
    }
}
