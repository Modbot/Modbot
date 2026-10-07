using Modbot.VRChat.Calendar;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The words Open now refuses with, in one place for the endpoint and for <c>/event open</c> in
/// Discord (Discord commands design §3.7, step 7): the same refusal reads the same wherever it is
/// pressed.
/// </summary>
public static class CalendarOpenWords
{
    public const string NoSuchEvent = "That event does not exist.";
    public const string NotConfigured = "Pick a managed group first.";
    public const string NoWorld = "The event has no world.";
    public const string TooEarly = "It is too early to open the instance.";
    public const string Over = "That event has already ended.";
    public const string AlreadyOpen = "The instance is already open.";
    public const string Checking = "Checking whether VRChat opened the instance.";

    /// <summary>
    /// The sentence for an outcome that opened nothing, or null when an attempt was made
    /// (<see cref="CalendarOpenNowOutcome.Tried"/>). <see cref="CalendarOpenNowOutcome.NoSuchEvent"/>
    /// has words here; the endpoint answers it with a 404 and no body.
    /// </summary>
    public static string? Refusal(CalendarOpenNowOutcome outcome) => outcome switch
    {
        CalendarOpenNowOutcome.NoSuchEvent => NoSuchEvent,
        CalendarOpenNowOutcome.NotConfigured => NotConfigured,
        CalendarOpenNowOutcome.NoWorld => NoWorld,
        CalendarOpenNowOutcome.TooEarly => TooEarly,
        CalendarOpenNowOutcome.Over => Over,
        CalendarOpenNowOutcome.AlreadyOpen => AlreadyOpen,
        CalendarOpenNowOutcome.Checking => Checking,
        _ => null,
    };
}
