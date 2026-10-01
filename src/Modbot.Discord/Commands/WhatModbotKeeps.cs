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
/// keep, the way the privacy policy's "What does Modbot record about me?" does, and it follows that
/// section heading for heading and item for item, in shorter words. The title says "can keep"
/// because it is the most a group can keep, not what this one does. Showing a member the records
/// themselves is a different and much harder job: most of them name other people too (who else was
/// in the instance, who reported whom, what a moderator wrote), and those are exactly what
/// <c>/me</c> never shows.
/// </para>
/// <para>
/// <strong>Nothing left out.</strong> The point of the list is to say plainly what is stored, so a
/// line that only covers part of a policy item is a line that hides the rest: the AI call log, who
/// opened a piece of evidence and the address a sign-in came from are all here because the policy
/// names them.
/// </para>
/// <para>
/// <strong>One place.</strong> The docs page for <c>/me</c> lists the same lines word for word, and
/// a test reads the page and checks every heading and line is there, so the command and the docs
/// cannot drift apart. When the privacy policy's list changes, change this and the docs page with
/// it.
/// </para>
/// </remarks>
public static class WhatModbotKeeps
{
    /// <summary>The card's title, which is also the button's answer to "how much": at most this.</summary>
    public const string Title = "What Modbot can keep";

    public static IReadOnlyList<KeptKind> Kinds { get; } =
    [
        new("From VRChat",
        [
            "Your display name, and the names, bios and status messages you had before",
            "Your VRChat id, bio, status, status message and pronouns",
            "Links to your profile picture, icon, banner and avatar picture",
            "When you joined VRChat, your account tags and the platform you were last on",
            "Whether VRChat says you are age-verified, and whether a moderator marked you 18+ by hand",
            "The group you show on your profile: its id, name and icon",
            "VRChat's full answer about your profile, minus where you are, private notes and your friend key",
        ]),
        new("About the group",
        [
            "That you are a member, when you joined, your roles, and whether you left",
            "Notes moderators keep on your membership in VRChat",
            "Bans and unbans, when they happened and who did them",
        ]),
        new("Where you have been",
        [
            "Which of the group's instances you joined and left, and when",
            "When you changed avatar, and the avatar's name",
            "Which instance you were in when something happened, and who else was there",
            "Instance kicks and warnings from VRChat's group log, with where they happened",
            "If you moderate with the companion and turned it on: when you saved a clip there, and its fingerprint",
        ]),
        new("Moderation",
        [
            "Kicks, bans and unbans, with the reasons, the moderator's note and their Modbot username",
            "Notes moderators wrote about you, with who and when, including ones taken back",
            "Case files, with a copy of your profile, membership and ban entry at the time",
            "Evidence on them, and who attached, took off, viewed, downloaded or destroyed each file",
            "Flags from the group's word lists and AI topics: the rule, the words or picture, and where",
            "The AI call log: the whole prompt and answer, up to 20,000 characters each",
            "How many times you were kicked, banned, removed or turned away, and the last moderator",
        ]),
        new("Discord",
        [
            "Your messages in full, who you replied to, and every edit and delete, old text kept",
            "For attachments: the file's name, type, size and link, not the file",
            "Your Discord id, username, display name, nickname, avatar link and roles",
            "When you joined and left, whether you boost the server, and any timeout",
            "If you are banned: your name, avatar link and the ban reason",
            "When you joined, moved between and left voice channels, and for how long. Never the audio",
            "Your account link, with both names",
            "Your giveaway entries, with your name and account ids",
            "Each time you use /me, and any request to delete your data",
        ]),
        new("If you have a Modbot account",
        [
            "Your username, email address, and linked Discord and VRChat accounts",
            "Every sign-in, with the address it came from",
            "Every failed sign-in, with the username typed and the address, never the password",
            "What you did and the settings you changed, with your name",
        ]),
    ];
}
