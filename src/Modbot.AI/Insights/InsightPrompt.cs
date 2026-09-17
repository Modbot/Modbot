using Modbot.Core.Data.Entities;

namespace Modbot.AI.Insights;

/// <summary>What the model is told, and what it is given (AI insights design §1.2).</summary>
/// <remarks>
/// The rules here are instructions, not guarantees -- a model can ignore them. That is why the
/// figures are stored beside the text, and why the figures themselves hold nothing about any person:
/// the one rule that must hold is kept by what the model is never given, not by what it is asked.
/// </remarks>
public static class InsightPrompt
{
    /// <summary>Roughly what fits in one Discord card with room to spare.</summary>
    public const int MaxWords = 220;

    /// <summary>Discord's limit on a card's description.</summary>
    public const int MaxTextLength = 4000;

    public static string Instructions(string kind) => $"""
        You write a short summary of a VRChat community's own figures for its volunteer moderators.
        This summary is about: {Topic(kind)}.

        You are given JSON with a list of figures. Each has a name, "now" (the stretch of days being
        summarised) and "before" (the stretch of the same length just before it). A null value means
        nothing was recorded, which is not the same as zero. There may also be short ranked lists for
        the stretch being summarised.

        Rules:
        - Use only the figures given. Never invent a number, a cause or an event.
        - Lead with what changed most against the stretch before, then anything else worth knowing.
        - When a figure is null, or both values are zero, leave it out unless its absence matters.
        - If you suggest why something changed, say it is a guess.
        - Never recommend banning, removing, warning or taking any action against anyone, and never
          give a score, rating or grade to the community, the team or any person.
        - Write plain, friendly English a volunteer understands. No jargon, no headings, no tables.
        - At most {MaxWords} words. Short paragraphs or a few bullet points starting with "- ".
        """;

    public static string Figures(InsightFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return figures.ToJson();
    }

    private static string Topic(string kind) => kind switch
    {
        InsightKinds.Group => "the group as a whole -- members, how people join, and how active it is",
        InsightKinds.Team => "the moderation team as a whole -- how much moderation happened and of what kind",
        InsightKinds.Instances => "the group's instances (VRChat instances) -- how many ran, for how long, and how busy they were",
        _ => kind,
    };
}
