using System.Text.RegularExpressions;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Twitch;

/// <summary>What the poll does about a "We're live on Twitch" post for one stream, right now.</summary>
public enum TwitchPostDecision
{
    /// <summary>The stream has not been live long enough yet. Asked again next pass.</summary>
    Wait,

    /// <summary>Make the post.</summary>
    Make,

    /// <summary>Twitch calls it something other than live (a rerun, a premiere): never posted.</summary>
    NotLive,

    /// <summary>The poll only found the stream long after it started, so "we're live" would be old news.</summary>
    SeenTooLate,

    /// <summary>The last "live" post was made too recently.</summary>
    WithinCoolDown,

    /// <summary>No site is ticked for a "live" post.</summary>
    NoSite,

    /// <summary>The stream ended before it had been live long enough.</summary>
    TooShort,
}

/// <summary>
/// The rules of the Twitch feature that do not touch the database or Twitch, so the poll, the
/// settings and the tests ask the same questions and get the same answers (Twitch design).
/// </summary>
/// <remarks>The clock is passed in: nothing here reads it.</remarks>
public static partial class TwitchRules
{
    /// <summary>The post's title when the operator has not written one.</summary>
    public const string DefaultTitle = "We're live on Twitch";

    /// <summary>The post's text when the operator has not written one.</summary>
    public const string DefaultText = "{title}\n{link}";

    public const string TitleWord = "{title}";
    public const string CategoryWord = "{category}";
    public const string LinkWord = "{link}";

    public const int LeastAfterMinutes = 1;
    public const int MostAfterMinutes = 60;
    public const int LeastEveryHours = 1;
    public const int MostEveryHours = 168;

    /// <summary>How often Get Streams is asked: once a minute.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(1);

    /// <summary>How often a changed viewer count goes out as a live update while the channel is live.</summary>
    public static readonly TimeSpan UpdateEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after Twitch says a stream started the poll may first see it and still make a "live"
    /// post: the same 15 minutes a "live" post may be late (<see cref="PostRules.TwitchLiveLateLimit"/>).
    /// </summary>
    public static readonly TimeSpan NoticeWithin = PostRules.TwitchLiveLateLimit;

    /// <summary>The event window: a stream is linked to an event that starts up to this long after it.</summary>
    public static readonly TimeSpan EventLeadTime = TimeSpan.FromHours(1);

    /// <summary>A Twitch login: letters, digits and underscores, up to 25.</summary>
    [GeneratedRegex("^[a-z0-9_]{1,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginPattern();

    /// <summary>
    /// The login in what an operator typed: the login itself, with or without an <c>@</c>, or a
    /// <c>twitch.tv</c> address. Lower case; null when it is none of those.
    /// </summary>
    public static string? LoginOf(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return null;

        var text = typed.Trim();

        if (text.Contains('/', StringComparison.Ordinal))
        {
            var withScheme = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;

            if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http")
                || !(uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            text = uri.AbsolutePath.Trim('/').Split('/')[0];
        }

        text = text.TrimStart('@').ToLowerInvariant();
        return LoginPattern().IsMatch(text) ? text : null;
    }

    /// <summary>The channel's page on Twitch.</summary>
    public static string ChannelLink(string login)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        return "https://www.twitch.tv/" + login;
    }

    /// <summary>
    /// Whether Twitch is set up enough to ask it anything: a client id, a secret, a channel and a
    /// Check that passed for them.
    /// </summary>
    public static bool SetUp(Data.Entities.Settings? settings) =>
        settings is not null
        && !string.IsNullOrWhiteSpace(settings.TwitchClientId)
        && !string.IsNullOrWhiteSpace(settings.TwitchClientSecretEncrypted)
        && !string.IsNullOrWhiteSpace(settings.TwitchChannelLogin)
        && !string.IsNullOrWhiteSpace(settings.TwitchChannelId)
        && settings.TwitchCheckedAt is not null
        && settings.TwitchProblem is null;

    /// <summary>Whether Twitch is limiting Modbot at <paramref name="now"/>: nothing goes to it, Check included.</summary>
    public static bool Stopped(Data.Entities.Settings? settings, DateTimeOffset now) =>
        settings?.TwitchStoppedUntil is { } until && now < until;

    /// <summary>Whether a stream is a live one: Twitch's <c>type</c> is <c>live</c>.</summary>
    public static bool IsLive(string? type) => string.Equals(type, "live", StringComparison.Ordinal);

    /// <summary>
    /// Fills the placeholders of a post template: <c>{title}</c> is the stream's title,
    /// <c>{category}</c> its category and <c>{link}</c> the channel's address. Anything else in braces
    /// is left as it is. The result is cut to <paramref name="longest"/> characters.
    /// </summary>
    public static string Fill(string template, string? title, string? category, string link, int longest)
    {
        ArgumentNullException.ThrowIfNull(template);

        var text = template
            .Replace(TitleWord, title ?? string.Empty, StringComparison.Ordinal)
            .Replace(CategoryWord, category ?? string.Empty, StringComparison.Ordinal)
            .Replace(LinkWord, link, StringComparison.Ordinal);

        return Cut(text, longest);
    }

    /// <summary>
    /// What to do about a stream's post. Only a live one posts; one seen too long after it started
    /// does not; it waits until it has been live <paramref name="afterMinutes"/>; no second post
    /// comes within <paramref name="everyHours"/> of <paramref name="lastPostAt"/>; and a post needs a
    /// site ticked.
    /// </summary>
    public static TwitchPostDecision Decide(
        string? type,
        DateTimeOffset startedAt,
        DateTimeOffset firstSeenAt,
        DateTimeOffset now,
        int afterMinutes,
        int everyHours,
        DateTimeOffset? lastPostAt,
        bool anySiteTicked)
    {
        if (!IsLive(type))
            return TwitchPostDecision.NotLive;

        if (firstSeenAt - startedAt > NoticeWithin)
            return TwitchPostDecision.SeenTooLate;

        if (now < startedAt + TimeSpan.FromMinutes(ClampAfterMinutes(afterMinutes)))
            return TwitchPostDecision.Wait;

        if (lastPostAt is { } last && now - last < TimeSpan.FromHours(ClampEveryHours(everyHours)))
            return TwitchPostDecision.WithinCoolDown;

        return anySiteTicked ? TwitchPostDecision.Make : TwitchPostDecision.NoSite;
    }

    public static int ClampAfterMinutes(int minutes) => Math.Clamp(minutes, LeastAfterMinutes, MostAfterMinutes);

    public static int ClampEveryHours(int hours) => Math.Clamp(hours, LeastEveryHours, MostEveryHours);

    /// <summary>
    /// The one calendar event that was on when a stream started: from an hour before it starts to its
    /// end, a date moved on its own where it was moved to. Null when none was, and null when more
    /// than one was: Modbot does not guess between overlapping events, a person chooses.
    /// </summary>
    public static Guid? ChooseEvent(IEnumerable<CalendarEvent> events, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(events);

        var found = events
            .Where(e => e.DeletedAt is null && e.CancelledAt is null
                && e.State is CalendarEventStates.Scheduled or CalendarEventStates.Open or CalendarEventStates.Finished)
            .Where(e => CalendarRepeat.Between(e, startedAt, startedAt + EventLeadTime).Any())
            .Select(e => e.Id)
            .Distinct()
            .Take(2)
            .ToList();

        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>Cuts text to <paramref name="longest"/> characters without splitting a surrogate pair.</summary>
    public static string Cut(string text, int longest)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= longest)
            return text;

        var end = longest;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;

        return text[..end];
    }
}
