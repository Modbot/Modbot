namespace Modbot.Core.Calendar;

/// <summary>One of Google Calendar's event colours: its id as the event resource's <c>colorId</c> takes it, Google's name for it and its colour.</summary>
/// <param name="Id">Google's id, <c>"1"</c> to <c>"11"</c>.</param>
/// <param name="Name">The name Google Calendar shows for it.</param>
/// <param name="Hex">The colour as Google's colours list gives it for events (<c>#rrggbb</c>).</param>
public sealed record CalendarGoogleColour(string Id, string Name, string Hex);

/// <summary>
/// The colours an event can have on Google Calendar, and whether it shows as busy or free: Google's
/// own fixed event palette, not Modbot's. An event with no colour takes the calendar's own.
/// </summary>
public static class CalendarGoogleColours
{
    /// <summary>Google's event colours, in Google's order. The ids and colours are the ones its colours list gives.</summary>
    public static readonly IReadOnlyList<CalendarGoogleColour> All =
    [
        new("1", "Lavender", "#a4bdfc"),
        new("2", "Sage", "#7ae7bf"),
        new("3", "Grape", "#dbadff"),
        new("4", "Flamingo", "#ff887c"),
        new("5", "Banana", "#fbd75b"),
        new("6", "Tangerine", "#ffb878"),
        new("7", "Peacock", "#46d6db"),
        new("8", "Graphite", "#e1e1e1"),
        new("9", "Blueberry", "#5484ed"),
        new("10", "Basil", "#51b749"),
        new("11", "Tomato", "#dc2127"),
    ];

    /// <summary>Shows the owner of the calendar as busy while the event runs (Google's <c>opaque</c>).</summary>
    public const string Busy = "busy";

    /// <summary>Leaves the owner of the calendar free while the event runs (Google's <c>transparent</c>). What every event was before the choice existed.</summary>
    public const string Free = "free";

    /// <summary>Whether <paramref name="colour"/> is one of Google's ids.</summary>
    public static bool IsColour(string? colour) => colour is not null && All.Any(c => c.Id == colour);

    /// <summary>Whether <paramref name="showAs"/> is <see cref="Busy"/> or <see cref="Free"/>.</summary>
    public static bool IsShowAs(string? showAs) => showAs is Busy or Free;

    /// <summary>The event's colour if it is one of Google's, else null for the calendar's own.</summary>
    public static string? ColourOf(string? colour) => IsColour(colour) ? colour : null;

    /// <summary>Google's <c>transparency</c> word for <paramref name="showAs"/>: <c>opaque</c> for busy, else <c>transparent</c>.</summary>
    public static string TransparencyOf(string? showAs) => showAs == Busy ? "opaque" : "transparent";

    /// <summary>The longest stored colour id.</summary>
    public const int MaxColourLength = 2;

    /// <summary>The longest stored show-as word.</summary>
    public const int MaxShowAsLength = 8;
}
