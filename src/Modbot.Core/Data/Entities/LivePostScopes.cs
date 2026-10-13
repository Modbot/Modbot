namespace Modbot.Core.Data.Entities;

/// <summary>Which instances get a "we're live" group post (<see cref="Settings.LivePostScope"/>).</summary>
public static class LivePostScopes
{
    /// <summary>No instance does.</summary>
    public const string Off = "off";

    /// <summary>
    /// An instance of a calendar event: one Modbot opened for it, or one in the event's world while the
    /// event is on.
    /// </summary>
    public const string Calendar = "calendar";

    /// <summary>Every instance of the group.</summary>
    public const string Any = "any";

    /// <summary>Only an instance Modbot opened itself, on time or with Open now.</summary>
    public const string Modbot = "modbot";

    public static bool IsKnown(string? scope) => scope is Off or Calendar or Any or Modbot;
}
