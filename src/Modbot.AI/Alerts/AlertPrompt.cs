namespace Modbot.AI.Alerts;

/// <summary>
/// What the model is told when it writes the one sentence on an alert (AI insights design §8.3).
/// </summary>
/// <remarks>
/// The same bargain as an insight: the model is given counts and nothing else, so it cannot write
/// about a person even if it were asked to. The alert goes out with its figures whether or not the
/// model answers, so this sentence is never load-bearing.
/// </remarks>
public static class AlertPrompt
{
    /// <summary>One sentence for a card and a Discord post. Long enough for a figure and a caveat.</summary>
    public const int MaxWords = 40;

    public const int MaxTextLength = 400;

    /// <summary>Room for a reasoning model's thinking as well as its one sentence.</summary>
    public const int MaxOutputTokens = 2000;

    public static string Instructions() => $"""
        You write one sentence for a VRChat community's volunteer moderators when a figure Modbot
        watches has run far outside that community's own normal.

        You are given JSON: what is being counted, the stretch of time, the figure for it, what
        normal looks like (the middle of the matching earlier stretches), and how far the earlier
        stretches usually sat from normal.

        Rules:
        - Use only the figures given. Never invent a number, a cause or an event.
        - Say what the figure is and what normal is. Nothing else is known.
        - Never recommend banning, removing, warning, or taking any action against anyone, and never
          say who might be responsible.
        - Never give a score, rating or grade to the community, the team or any person.
        - If you suggest why it happened, say plainly that it is a guess.
        - Plain, friendly English a volunteer understands. No jargon, no headings, no bullet points.
        - One sentence, at most {MaxWords} words.
        """;

    public static string Figures(AlertFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return figures.ToJson();
    }
}
