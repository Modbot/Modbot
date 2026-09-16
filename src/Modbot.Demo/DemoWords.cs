namespace Modbot.Demo;

/// <summary>
/// The words the demo is built out of.
/// </summary>
/// <remarks>
/// <para>
/// Every name here is made up. Nothing in this file refers to a real VRChat account, a real world,
/// a real Discord server or a real person, and nothing in the demo ever should — a demo deployment
/// serves every visitor as an administrator (demo mode design §3), so anything it holds is public.
/// </para>
/// <para>
/// Names are assembled from two halves rather than listed one by one, because four hundred hand
/// written names would be four hundred chances to accidentally write somebody's real handle.
/// </para>
/// </remarks>
internal static class DemoWords
{
    public static readonly string[] NameFirst =
    [
        "Neon", "Velvet", "Static", "Copper", "Lunar", "Paper", "Glass", "Echo", "Mellow", "Frost",
        "Amber", "Quartz", "Drift", "Pixel", "Cobalt", "Willow", "Ember", "Cinder", "Saffron", "Onyx",
        "Ripple", "Marble", "Hazel", "Vapor", "Clover", "Tinsel", "Aurora", "Basalt", "Citrus", "Dusk",
        "Fern", "Garnet", "Halo", "Indigo", "Juniper", "Kelp", "Lantern", "Maple", "Nectar", "Opal",
        "Plume", "Quill", "Rune", "Sable", "Thistle", "Umber", "Vellum", "Wisp", "Yarrow", "Zephyr",
    ];

    public static readonly string[] NameSecond =
    [
        "fox", "moth", "heron", "otter", "finch", "lynx", "raven", "koi", "hare", "wren",
        "crane", "newt", "stoat", "shrike", "tern", "vole", "gull", "mink", "pike", "swift",
    ];

    public static readonly string[] NameTail =
    [
        "", "", "", "", "", "VR", "_", "01", "77", "x", "ish", "ling", "_vr", "42", "zz",
    ];

    public static readonly string[] Bios =
    [
        "Here most evenings. Say hi.",
        "Full body, mostly quiet, always dancing at the back.",
        "I make avatars. Ask me about shaders.",
        "Group regular since the old world. Ping me if you need a hand.",
        "Night owl. Usually in the lounge after midnight.",
        "New-ish. Still working out where everything is.",
        "Photographer. If I point a camera at you and you'd rather I didn't, just say.",
        "Desktop only for now, sorry about the staring.",
        "Event host. I open the room on Fridays.",
        "Here to listen, not to talk much.",
        "Ask before you pick me up, please.",
        "Learning to DJ. Be gentle.",
        "",
        "",
        "",
    ];

    public static readonly string[] Pronouns =
    [
        "she/her", "he/him", "they/them", "she/they", "he/they", "any", "ask", "", "", "",
    ];

    public static readonly string[] Statuses = ["active", "join me", "ask me", "busy"];

    public static readonly string[] StatusLines =
    [
        "chilling", "open to invites", "in a private room", "afk", "working", "dancing",
        "listening", "", "", "",
    ];

    public static readonly string[] Platforms = ["standalonewindows", "android", "ios"];

    /// <summary>A world name and the kind of place it is, for the twelve worlds the group uses.</summary>
    public static readonly (string Name, string Description, int Capacity)[] Worlds =
    [
        ("The Long Porch", "A wooden porch at dusk with too many chairs. The group's usual hangout.", 40),
        ("Quiet Library", "Shelves, lamps and a fireplace. Voices carry, so people keep them down.", 24),
        ("Neon Yard", "An open lot under a motorway with a sound system nobody claims.", 60),
        ("Tide Pool", "A shallow beach at low tide. Good for photos, bad for hide and seek.", 32),
        ("Paper Lanterns", "A festival street that never runs out of festival.", 48),
        ("The Garage", "Concrete, a projector and a lot of bean bags. Movie nights happen here.", 32),
        ("Glasshouse", "A greenhouse with rain on the roof. The newcomer welcome room.", 24),
        ("Rooftop Six", "Six rooftops joined by walkways. Everyone ends up on the tallest one.", 40),
        ("Cold Station", "An abandoned platform where the last train is always due.", 24),
        ("Thistle Fields", "Long grass, low sun and a barn with a piano in it.", 40),
        ("The Workshop", "Benches, tools and half-finished avatars. Where the makers meet.", 16),
        ("Night Market", "Stalls, steam and narrow lanes. The busiest room the group opens.", 64),
    ];

    /// <summary>The group's Discord channels: name, kind and what gets said in them.</summary>
    public static readonly (string Name, string Type, string[] Lines)[] Channels =
    [
        ("welcome", "text",
        [
            "hey everyone, just joined!",
            "welcome in!",
            "hi! how do I get the member role?",
            "read the rules channel and react at the bottom",
            "thanks, done",
            "welcome aboard",
        ]),
        ("general", "text",
        [
            "anyone on tonight?",
            "I'll be around after 9",
            "room's open if anyone wants to come through",
            "brb, cat",
            "my headset died mid-sentence, sorry about that",
            "that was a good one, thanks for hosting",
            "does anyone know a fix for the mic cutting out?",
            "try switching the audio device in settings, that did it for me",
            "lol",
            "who's opening tomorrow?",
            "I can if nobody else wants to",
            "new avatar, thoughts?",
            "that's very good actually",
            "how long have you been working on it",
            "about three weeks on and off",
            "I keep falling through the floor in that world",
            "it's the second floor, known issue",
            "see you all friday",
        ]),
        ("help", "text",
        [
            "my game crashes when I join the night market",
            "lower the avatar limit, that room is heavy",
            "worked, thank you",
            "how do I link my discord to my vrchat?",
            "there's a link page, a mod can send you it",
            "can someone check if my mic is working",
            "you're loud and clear",
        ]),
        ("photos", "text",
        [
            "from last night",
            "this one came out really well",
            "the lighting in the glasshouse is unreal",
            "may I use this as my banner?",
            "of course",
            "group photo before everyone left",
        ]),
        ("events", "text",
        [
            "movie night this saturday, 8pm",
            "what are we watching?",
            "vote in the thread",
            "I'll open the garage half an hour early",
            "reminder: quiz tonight",
            "can't make it, have fun",
        ]),
        ("music", "text",
        [
            "set list from friday if anyone wants it",
            "that third track was perfect",
            "who was on at midnight?",
            "that was me",
            "please do that again",
        ]),
        ("mod-chat", "text",
        [
            "keeping an eye on the new join in the yard",
            "seen, I'll sit in",
            "gave them a warning, they took it fine",
            "same person as last month I think",
            "checked, different account, same behaviour",
            "banned, case written up",
            "thanks",
        ]),
        ("rules", "announcement",
        [
            "Be decent to each other. That is the whole list, the rest is detail.",
            "No recording people who have asked you not to.",
            "Ask before picking anyone up.",
            "Moderators wear the blue badge. Anything at all, say something to one of us.",
        ]),
        ("announcements", "announcement",
        [
            "The friday room moves to the night market from next week.",
            "We've added two new moderators. Say hello to them.",
            "Server's quiet over the holidays. Rooms will still open most evenings.",
        ]),
        ("introductions", "text",
        [
            "hi, been in vr about a month, mostly desktop",
            "welcome!",
            "hello, found this group through a friend",
            "glad you made it",
            "hi all, long time lurker",
        ]),
        ("off-topic", "text",
        [
            "my dog has learned to open the fridge",
            "post proof",
            "this is why we can't have nice things",
            "it's raining again",
            "it is always raining",
        ]),
        ("suggestions", "text",
        [
            "could we get a channel for avatar help?",
            "seconded",
            "we'll try it and see if it gets used",
            "can the friday room start earlier?",
            "half the group is in a different timezone, that's the problem",
        ]),
        ("Lounge", "voice", []),
        ("Movie Room", "voice", []),
    ];

    public static readonly (string Name, int Color, bool Assign)[] DiscordRoles =
    [
        ("@everyone", 0, false),
        ("Member", 0x5865F2, true),
        ("Moderator", 0x2ECC71, true),
        ("Admin", 0xE74C3C, true),
        ("18+", 0x9B59B6, true),
        ("Event host", 0xF1C40F, true),
        ("Regular", 0x1ABC9C, true),
        ("Modbot", 0x95A5A6, false),
    ];

    /// <summary>The group's own roles in VRChat.</summary>
    public static readonly string[] GroupRoles = ["Member", "Regular", "Event host", "Moderator", "Admin"];

    public static readonly (string Label, string Description, bool NeedsWritten)[] BanReasons =
    [
        ("Harassment", "Targeting somebody after being asked to stop.", true),
        ("Hate speech", "Slurs, or abuse aimed at who somebody is.", true),
        ("Sexual content", "Sexual behaviour or content in a group room.", true),
        ("Underage", "Not old enough to be in an 18+ room.", true),
        ("Crashing", "Avatars or content that crash other people's games.", false),
        ("Evading a ban", "Came back on another account.", true),
        ("Threats", "Threatening somebody inside or outside the group.", true),
        ("Spam", "Repeated unwanted messages or invites.", false),
    ];

    public static readonly string[] BanWriteUps =
    [
        "Followed two people between rooms after both asked them to stop. Warned once in the yard, "
        + "carried on ten minutes later in the lounge. Three moderators saw it.",
        "Used slurs in voice in the night market, repeatedly, and did not stop when asked. Screenshots "
        + "and a clip are attached.",
        "Crasher avatar taken into a full room. Twelve people dropped. Same avatar as an earlier "
        + "incident in another group.",
        "Came back on a new account four days after the first ban and said so in general. Same voice, "
        + "same avatar, same behaviour.",
        "Sexual comments aimed at a newcomer in the glasshouse, which is the welcome room. Warned in "
        + "March for the same thing.",
        "Threatened another member in DMs and posted about it in mod-chat. The other member has the "
        + "screenshots and is happy for them to be kept.",
        "Spammed invites to an unrelated group in three channels and in voice. Asked to stop twice.",
        "Told two moderators they were under sixteen after being asked in an 18+ room. Removed rather "
        + "than argued with; no hard feelings, welcome back in a few years.",
    ];

    public static readonly string[] WarnReasons =
    [
        "Talking over people in the welcome room.",
        "Mic quality; asked to sort it out or push to talk.",
        "Picked somebody up after being asked not to.",
        "Arguing in general instead of taking it to DMs.",
        "Loud music over voice without asking.",
        "Camera in somebody's face after they said no.",
    ];

    public static readonly string[] KickReasons =
    [
        "Would not stop after a warning.",
        "Heavy avatar in a full room.",
        "Shouting in voice.",
        "Kept re-joining to argue.",
    ];

    public static readonly string[] Regions = ["us", "use", "eu", "jp"];

    public static readonly string[] StaffNames =
    [
        "demo", "avery", "kit", "rowan", "sam", "noor", "jules", "tam",
    ];

    /// <summary>The calendar's events: title, description and how long they run.</summary>
    public static readonly (string Title, string Description, int Hours)[] Events =
    [
        ("Friday Hangout", "The usual. Doors at 8, room stays open until it empties.", 4),
        ("Movie Night", "Something long and slightly bad. Vote in the events channel.", 3),
        ("Newcomer Welcome", "A quiet half hour for anyone who joined this week.", 1),
        ("Avatar Workshop", "Bring something half-finished and somebody will help.", 2),
        ("Quiz", "Six rounds, teams of four, no prizes worth having.", 2),
        ("Night Market", "The big one. Two rooms if the first fills up.", 5),
    ];
}
