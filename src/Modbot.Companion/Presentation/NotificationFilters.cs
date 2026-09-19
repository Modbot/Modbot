using System.Text.Json.Nodes;
using Modbot.Companion.Instances;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;

namespace Modbot.Companion.Presentation;

/// <summary>One of the three ways the companion has of telling a moderator something.</summary>
/// <remarks>
/// Each keeps its own on switch and its own volume elsewhere; this only says which kinds of event
/// it is allowed to raise. A way that is switched off tells nobody anything, whatever its list
/// says.
/// </remarks>
public enum NotificationWay
{
    /// <summary>A card on the notification overlay, in the headset.</summary>
    PopUp,

    /// <summary>The short bleep.</summary>
    Sound,

    /// <summary>The spoken line.</summary>
    Voice,
}

/// <summary>
/// Which kinds of event raise a notification, and by which of the three ways.
/// </summary>
/// <remarks>
/// <para><strong>One list of kinds, a tick per way.</strong> The same seven kinds for all three,
/// so there is one vocabulary and one place to look, but each way has its own ticks — a moderator
/// who wants every arrival spoken and only flagged arrivals bleeped is the ordinary case, and one
/// shared list could not say it (notification filters design 2026-09-19 §3.1).</para>
/// <para><strong>The defaults are what the companion did before this existed.</strong> The voice
/// said joins and leaves; the sound and the pop-ups fired for a flagged join and for a problem;
/// nothing reacted to anything else. A file written before this feature has its voice column read
/// out of the <c>voice</c> object it already has, so a switch somebody turned off stays off (§4).</para>
/// <para>Kept in the <c>notificationFilters</c> object of <c>settings.json</c>
/// (<see cref="CompanionSettings"/>) and nowhere else. No server is told any of it.</para>
/// </remarks>
public sealed class NotificationFilters : IEquatable<NotificationFilters>
{
    /// <summary>The kinds that can be filtered, in the order the card lists them.</summary>
    /// <remarks><see cref="NotificationKind.Test"/> is not one: a person pressed the button.</remarks>
    public static IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        NotificationKind.Joined,
        NotificationKind.AlreadyThere,
        NotificationKind.Left,
        NotificationKind.ChangedAvatar,
        NotificationKind.FlaggedJoin,
        NotificationKind.LogStopped,
        NotificationKind.Problem,
    ];

    /// <summary>The three ways, in the order the card's columns run.</summary>
    public static IReadOnlyList<NotificationWay> Ways { get; } =
        [NotificationWay.PopUp, NotificationWay.Sound, NotificationWay.Voice];

    public const string PopUpField = "popUp";

    public const string SoundField = "sound";

    public const string VoiceField = "voice";

    private readonly HashSet<NotificationKind> _popUp;
    private readonly HashSet<NotificationKind> _sound;
    private readonly HashSet<NotificationKind> _voice;

    public NotificationFilters(
        IEnumerable<NotificationKind> popUp,
        IEnumerable<NotificationKind> sound,
        IEnumerable<NotificationKind> voice)
    {
        ArgumentNullException.ThrowIfNull(popUp);
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(voice);

        _popUp = Keep(popUp);
        _sound = Keep(sound);
        _voice = Keep(voice);
    }

    /// <summary>
    /// What the companion did before filters existed: the voice said joins and leaves, all three
    /// ways raised a flagged join and a problem, and nothing else was ever raised at all.
    /// </summary>
    public static NotificationFilters Default { get; } = new(
        popUp: [NotificationKind.FlaggedJoin, NotificationKind.Problem],
        sound: [NotificationKind.FlaggedJoin, NotificationKind.Problem],
        voice: [NotificationKind.Joined, NotificationKind.Left, NotificationKind.FlaggedJoin, NotificationKind.Problem]);

    /// <summary>Everything ticked, everywhere. Not a default; it is here for a test to name.</summary>
    public static NotificationFilters Everything { get; } = new(Kinds, Kinds, Kinds);

    /// <summary>Nothing ticked anywhere. The three on switches still decide whether a way runs at all.</summary>
    public static NotificationFilters Nothing { get; } = new([], [], []);

    /// <summary>Whether a card is drawn for this kind.</summary>
    public bool PopUpShows(NotificationKind kind) => Wants(NotificationWay.PopUp, kind);

    /// <summary>Whether the bleep sounds for this kind.</summary>
    public bool SoundPlays(NotificationKind kind) => Wants(NotificationWay.Sound, kind);

    /// <summary>Whether the voice says this kind.</summary>
    public bool VoiceSays(NotificationKind kind) => Wants(NotificationWay.Voice, kind);

    /// <summary>Whether one way raises one kind. The Test button is never filtered.</summary>
    public bool Wants(NotificationWay way, NotificationKind kind)
        => kind is NotificationKind.Test || For(way).Contains(kind);

    /// <summary>The kinds one way raises.</summary>
    public IReadOnlySet<NotificationKind> For(NotificationWay way) => way switch
    {
        NotificationWay.PopUp => _popUp,
        NotificationWay.Sound => _sound,
        _ => _voice,
    };

    /// <summary>The same filters with one tick put on or taken off.</summary>
    public NotificationFilters With(NotificationWay way, NotificationKind kind, bool on)
    {
        var kinds = new HashSet<NotificationKind>(For(way));
        if (on)
            kinds.Add(kind);
        else
            kinds.Remove(kind);

        return way switch
        {
            NotificationWay.PopUp => new NotificationFilters(kinds, _sound, _voice),
            NotificationWay.Sound => new NotificationFilters(_popUp, kinds, _voice),
            _ => new NotificationFilters(_popUp, _sound, kinds),
        };
    }

    /// <summary>The kind an observation the client read out of VRChat's log counts as.</summary>
    public static NotificationKind? KindOf(PresenceKind presence) => presence switch
    {
        PresenceKind.Joined => NotificationKind.Joined,
        PresenceKind.PresenceObserved => NotificationKind.AlreadyThere,
        PresenceKind.Left => NotificationKind.Left,
        PresenceKind.AvatarChanged => NotificationKind.ChangedAvatar,
        PresenceKind.LogStopped => NotificationKind.LogStopped,
        _ => null,
    };

    /// <summary>
    /// The word a kind is written as. The five the Events page also has are spelled the way
    /// <see cref="EventFilters.Kinds"/> spells them, so one vocabulary covers both.
    /// </summary>
    public static string Word(NotificationKind kind) => kind switch
    {
        NotificationKind.Joined => "joined",
        NotificationKind.AlreadyThere => "already there",
        NotificationKind.Left => "left",
        NotificationKind.ChangedAvatar => "changed avatar",
        NotificationKind.FlaggedJoin => "flagged join",
        NotificationKind.LogStopped => "log stopped",
        NotificationKind.Problem => "problem",
        _ => "test",
    };

    /// <summary>The kind that word names, or null for a word this client does not know.</summary>
    public static NotificationKind? KindFor(string? word)
        => Kinds.Cast<NotificationKind?>().FirstOrDefault(k => string.Equals(Word(k!.Value), word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The row's label on the Notifications card.</summary>
    public static string Label(NotificationKind kind) => kind switch
    {
        NotificationKind.Joined => "Joined",
        NotificationKind.AlreadyThere => "Already there",
        NotificationKind.Left => "Left",
        NotificationKind.ChangedAvatar => "Changed avatar",
        NotificationKind.FlaggedJoin => "Flagged join",
        NotificationKind.LogStopped => "Log stopped",
        NotificationKind.Problem => "Problem",
        _ => "Test",
    };

    /// <summary>The column's label on the Notifications card.</summary>
    public static string Label(NotificationWay way) => way switch
    {
        NotificationWay.PopUp => "Pop-up",
        NotificationWay.Sound => "Sound",
        _ => "Voice",
    };

    /// <summary>The word a way is written as in the file.</summary>
    public static string Field(NotificationWay way) => way switch
    {
        NotificationWay.PopUp => PopUpField,
        NotificationWay.Sound => SoundField,
        _ => VoiceField,
    };

    /// <summary>The <c>notificationFilters</c> object as it is written to the file.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var way in Ways)
        {
            var kinds = For(way);
            json[Field(way)] = new JsonArray(
                [.. Kinds.Where(kinds.Contains).Select(k => (JsonNode?)JsonValue.Create(Word(k)))]);
        }

        return json;
    }

    /// <summary>
    /// Reads the <c>notificationFilters</c> object.
    /// </summary>
    /// <remarks>
    /// No object at all is a file written before this feature: the defaults, with the voice column
    /// taken from the <c>voice</c> object the file already has, so a moderator who turned joins off
    /// does not find them back on. A missing column takes that column's default, and a word this
    /// client does not know is ignored rather than treated as an error, so a file written by a
    /// newer client still loads here.
    /// </remarks>
    /// <param name="olderVoice">The <c>voice</c> object's own switches, for a file with no filters in it.</param>
    public static NotificationFilters FromJson(JsonNode? node, VoiceSettings? olderVoice = null)
    {
        var fallback = olderVoice is null ? Default : SeededFrom(olderVoice);

        if (node is not JsonObject json)
            return fallback;

        return new NotificationFilters(
            Column(json, NotificationWay.PopUp, fallback),
            Column(json, NotificationWay.Sound, fallback),
            Column(json, NotificationWay.Voice, fallback));
    }

    /// <summary>
    /// The defaults with the voice column read out of an older file's <c>voice</c> object: its
    /// three switches are the same three choices this card's Voice column now makes.
    /// </summary>
    public static NotificationFilters SeededFrom(VoiceSettings voice)
    {
        ArgumentNullException.ThrowIfNull(voice);

        var spoken = new HashSet<NotificationKind> { NotificationKind.Problem };
        if (voice.Joins)
            spoken.Add(NotificationKind.Joined);
        if (voice.Leaves)
            spoken.Add(NotificationKind.Left);
        if (voice.FlaggedJoins)
            spoken.Add(NotificationKind.FlaggedJoin);

        return new NotificationFilters(Default.For(NotificationWay.PopUp), Default.For(NotificationWay.Sound), spoken);
    }

    /// <summary>The voice's three switches as this Voice column now reads, so the file says one thing.</summary>
    public VoiceSettings InStepWith(VoiceSettings voice)
    {
        ArgumentNullException.ThrowIfNull(voice);

        return voice with
        {
            Joins = VoiceSays(NotificationKind.Joined),
            Leaves = VoiceSays(NotificationKind.Left),
            FlaggedJoins = VoiceSays(NotificationKind.FlaggedJoin),
        };
    }

    public bool Equals(NotificationFilters? other)
        => other is not null && Ways.All(way => For(way).SetEquals(other.For(way)));

    public override bool Equals(object? obj) => Equals(obj as NotificationFilters);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var way in Ways)
        {
            foreach (var kind in Kinds.Where(For(way).Contains))
                hash.Add((way, kind));
        }

        return hash.ToHashCode();
    }

    private static IEnumerable<NotificationKind> Column(JsonObject json, NotificationWay way, NotificationFilters fallback)
    {
        if (json[Field(way)] is not JsonArray array)
            return fallback.For(way);

        return array
            .Select(node => node is JsonValue value && value.TryGetValue<string>(out var word) ? KindFor(word) : null)
            .Where(kind => kind is not null)
            .Select(kind => kind!.Value);
    }

    /// <summary>Only kinds a moderator can tick, so nothing can smuggle the Test kind into the file.</summary>
    private static HashSet<NotificationKind> Keep(IEnumerable<NotificationKind> kinds)
        => [.. kinds.Where(Kinds.Contains)];
}
