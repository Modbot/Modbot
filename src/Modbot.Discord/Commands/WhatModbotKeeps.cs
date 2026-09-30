namespace Modbot.Discord.Commands;

/// <summary>One heading of <see cref="WhatModbotKeeps"/> and its lines.</summary>
public sealed record KeptKind(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// What a group's Modbot can keep about a member, in plain words: the answer to the
/// <c>/me</c> button "What Modbot keeps" (Discord /me design §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A fixed list, not a reading of the database.</strong> It says what Modbot is able to
/// keep, the way the privacy policy's "What does Modbot record about me?" does, and it is cut down
/// from that section heading for heading. Showing a member the records themselves is a different
/// and much harder job: most of them name other people too (who else was in the instance, who
/// reported whom, what a moderator wrote), and those are exactly what <c>/me</c> never shows.
/// </para>
/// <para>
/// <strong>One place.</strong> The docs page for <c>/me</c> lists the same lines word for word, and
/// a test reads the page and checks every line is there, so the command and the docs cannot drift
/// apart. When the privacy policy's list changes, change this and the docs page with it.
/// </para>
/// </remarks>
public static class WhatModbotKeeps
{
    /// <summary>The card's title.</summary>
    public const string Title = "What Modbot keeps";

    /// <summary>The one line above the list: it is the most a group can keep, not what this one does.</summary>
    public const string AtMost = "At most, depending on what the group has switched on:";

    public static IReadOnlyList<KeptKind> Kinds { get; } =
    [
        new("From VRChat",
        [
            "Your display name, and the names, bios and status you had before",
            "Your VRChat id, bio, status, pronouns and picture links",
            "Whether you are age-verified",
            "The group you show on your profile",
        ]),
        new("About the group",
        [
            "That you are a member, when you joined, your roles, and whether you left",
            "Notes moderators keep on your membership",
            "Bans and unbans",
        ]),
        new("Where you have been",
        [
            "Which of the group's instances you joined and left, and when",
            "Your avatar changes",
            "Who else was in the instance when something happened",
        ]),
        new("Moderation",
        [
            "Kicks, bans and notes about you, and why",
            "Case files and the evidence attached to them",
            "Flags raised by the group's word lists and AI rules",
        ]),
        new("Discord",
        [
            "Your messages in full, with edits and deletes",
            "Your Discord id, names, avatar link, roles, and when you joined and left",
            "When you were in voice channels, never the audio",
            "Your account link and giveaway entries",
        ]),
        new("If you have a Modbot account",
        [
            "Your username, email address and sign-ins",
            "What you did in Modbot",
        ]),
    ];
}
