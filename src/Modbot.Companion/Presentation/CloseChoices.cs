namespace Modbot.Companion.Presentation;

/// <summary>
/// What the window's X and its Quit button do, as decided by the "Close to tray" setting.
/// </summary>
/// <remarks>
/// With the setting on the client lives in the tray: the X hides the window, and Quit asks first
/// because it ends something that was meant to keep running. With it off the window is the client,
/// so the X quits and Quit has nothing to ask.
/// </remarks>
public static class CloseChoices
{
    /// <summary>The question the Quit button asks when the client would otherwise stay in the tray.</summary>
    public const string QuitQuestion = "Modbot will stop reporting and won't stay in the tray.";

    /// <summary>True when the X hides the window to the tray; false when it quits.</summary>
    public static bool XHidesToTray(bool closeToTray) => closeToTray;

    /// <summary>True when Quit asks before quitting; false when it quits straight away.</summary>
    public static bool QuitAsksFirst(bool closeToTray) => closeToTray;
}
