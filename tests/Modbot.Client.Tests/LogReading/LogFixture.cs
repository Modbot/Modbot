using Modbot.Client.LogReading;

namespace Modbot.Client.Tests.LogReading;

/// <summary>
/// The real thing: the <c>[Behaviour]</c> subset of an actual 17,123-line VRChat session log,
/// including a populated group instance, genuine arrivals and departures inside it, and both
/// phantom bursts.
/// </summary>
/// <remarks>
/// Hand-written samples prove the parser handles what its author imagined. This file is the only
/// evidence available that it handles what VRChat actually writes.
/// </remarks>
public static class LogFixture
{
    public const string Name = "behaviour-2026-09-03.txt";

    /// <summary>The local user in this session, and the id their display name resolves to.</summary>
    public const string LocalDisplayName = "bin¹";

    public const string LocalUserId = "usr_f2049d71-e76b-42d2-a8bd-43deec9c004e";

    /// <summary>The one group instance visited: "The Black Cat".</summary>
    public const string GroupLocation =
        "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:85019"
        + "~group(grp_c7ba8659-4bf5-462f-8a7c-2cc31591f560)~groupAccessType(public)~ageGate~region(use)";

    public const string GroupWorldId = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";
    public const string GroupInstanceId = "85019";
    public const string GroupId = "grp_c7ba8659-4bf5-462f-8a7c-2cc31591f560";

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", Name);

    public static IReadOnlyList<string> Lines() => File.ReadAllLines(Path);

    public static IEnumerable<VRChatLogEvent> Events()
    {
        foreach (var raw in Lines())
        {
            if (VRChatLogLineParser.TryParse(raw, out var line)
                && BehaviourEventParser.Parse(line) is { } parsed)
            {
                yield return parsed;
            }
        }
    }
}
