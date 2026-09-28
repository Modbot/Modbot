namespace Modbot.Companion.App;

/// <summary>
/// Where bugs and feedback go: the project's GitHub issues, from the window's foot and the tray.
/// </summary>
/// <remarks>
/// The list rather than the new-issue form, so a moderator sees whether it has been reported
/// already. Always this bare address: nothing the client knows (a world, a person, a group) is
/// carried into a public issue.
/// </remarks>
internal static class Issues
{
    public const string Label = "Bugs and feedback";

    public static readonly Uri Page = new("https://github.com/Modbot/Modbot/issues");
}
