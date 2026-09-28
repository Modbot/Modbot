# The whole site, reviewed against a first-time moderator

- **Date:** 2026-09-27
- **Looked at:** every sidebar page, tab, sub-tab and the person popup on the live group ("-Thy Kingdom
  18Plus", read-only, changed nothing) and the local copy, in Chrome at desk width (1264 px), phone
  width (541 px, Chrome's narrowest window) and in the Headset layout. Beside them: the same group on
  vrchat.com and the same server in Discord's web app, looking only. Code read at staging `2f869395`.
- **Follows:** `2026-09-25-ux-review.md` (finding 12, the sidebar), and today's VRChat, Discord,
  Settings and Worlds reviews. Where one of those already covers a problem it is named, not repeated.
- **The reader:** a volunteer moderator on their first day. They know VRChat and Discord. They do not
  know what Modbot calls anything.

## Verdict

The pages this week added are good: the VRChat page's header and tab row now read as vrchat.com's,
the Discord header reads as Discord's, the ban dialog, the standing bar and Settings' side list are
all better than what they replaced. The trouble is between the pages.

**The sidebar still describes Modbot, not the two places the moderator knows.** The VRChat group and
the Discord server, the two things a new moderator would look for first, are the 13th and 15th
entries, under a heading called "Analytics". The VRChat group's own parts are spread around the list
(Requests 2nd, Calendar 5th, Bans 8th) or not in it at all (Members, Instances). Opening four of the
VRChat page's nine tabs moves the sidebar's light to another entry and renames the page: the tab says
Members and the page title says People.

The three worst problems, in order:

1. **The sidebar has no VRChat part and no Discord part** (finding 1).
2. **Now says "Nothing waiting" while somebody is waiting to join** (finding 2).
3. **Search does not know the words a moderator types**: "join" and "events" find nothing;
   "banned", "kick" and "voice" find people with those words in their names (finding 3).

## Findings, ranked by how much they hurt a new moderator

### 1. No VRChat part and no Discord part in the sidebar

**What a new moderator meets:**

| They want | Where it is today | Clicks |
|---|---|---|
| The group | Analytics → **VRChat**, 13th entry | 1, if they read "VRChat" under "Analytics" as the group |
| Members | Not in the sidebar. People (7,268 people, visitors included), then Filter → Membership → Members; or VRChat → Members tab | 2–4 |
| Join requests | **Requests**, 2nd entry; also VRChat → Invites | 1 |
| Banned people | **Bans**, 8th; also VRChat → Banned Users | 1 |
| Open instances | Live, or VRChat → Instances (not in the sidebar) | 1–2 |
| Events | **Calendar**; also VRChat → Events and Discord → Events | 1 |
| The Discord server | Analytics → **Discord**, 15th | 1 |
| Discord members | Not in the sidebar. Discord → Members | 2 |

- 17 entries; the first 11 have no heading (`lib/nav.ts:15-99`).
- The one heading that groups the platforms is "Analytics", which tells a moderator "charts", not
  "the group".
- Four VRChat tabs lead to a page of their own (`lib/groupOverview.ts:55`): Events → **Calendar**,
  Members → **People**, Banned Users → **Bans**, Settings → Logs → **Audit log**. The page title and
  the lit sidebar entry change to that page's name, so one click on the VRChat page's own tab row
  appears to leave the VRChat page. The same page opened from the sidebar has no group header, so it
  looks like a different page again.
- Same thing on the Discord row: Voice now → **Live**, Events → **Calendar**, Bans → **Bans**
  (`lib/serverOverview.ts:16-22`).

**Discord's Bans tab opens VRChat's ban list.** 1,180 VRChat accounts, `usr_` ids under every name. A
Discord moderator reads it as the Discord server's bans. Modbot keeps no Discord ban list page.

**Alternative:** a sidebar with a VRChat part and a Discord part, each headed by the platform's
name, holding what that platform's own page holds. See the proposed site map below. Pages that belong
to a part light that part's entry and keep its name ("Members", not "People"). Discord's row drops
Bans until there is a Discord ban list to show.

### 2. Now says "Nothing waiting" while a join request waits

- **Live, 2026-09-27 13:40:** Now → Needs a decision → "Nothing waiting". Requests → OXDOOZY, Trusted
  User, asked Jan 17, Approve / Reject. The Requests entry in the sidebar has no count.
- Now shows "0 open flags · 0 reviews · Join requests": two counts and a bare link
  (`pages/Now.tsx:218-221`). The comment at `Now.tsx:49-50` says why: join requests are only read from
  VRChat, and whether Now may read them is an open question.
- "Nothing waiting" is the one line on the front page a moderator is meant to trust, and it is wrong
  whenever someone has asked to join.

**Alternatives:**
- **A.** Now reads the join request list when it opens (one VRChat request per Now open or Refresh,
  none on a timer), counts it into "Needs a decision" with the newest rows, and the sidebar's
  Join requests entry shows the count. A new use of an endpoint Modbot already calls; needs a yes on
  the rate first.
- **B.** No new request: while join requests are not read, the line says "0 flags · 0 reviews" and
  the empty row says nothing about join requests at all ("No flags or reviews"), so it stops claiming
  there is nothing.

### 3. Search does not know the words a moderator types

`CommandPalette.tsx:154-156` matches a page only on its own label. Tried on the live group:

| Typed | Found | Wanted |
|---|---|---|
| `join` | Nothing matches | Join requests |
| `events` | Nothing matches | Calendar |
| `banned` | A person called BannedPrincess (VRChat and Discord) | Bans |
| `kick` | A person called Ongshekickedme | Audit log |
| `members` | **Discord members** first, then Members | The group's members first |
| `voice` | Voicesguy72, VividVoice, robromansvoice, … | Live |

**Alternative:** each page carries a short list of other words it answers to (Requests: join, requests,
applicants, waiting; Calendar: events, schedule; Bans: banned, ban list, unban; Live: voice, instances
now, who's online; Members: members, roster; Audit log: kick, warn, log, history). Pages that match
come before people. "Members" goes to the group's members; Discord's list is "Discord members". No
request, no server change.

### 4. The person popup opens on the word "Person" and an id

- The popup's title is "Person" and its subtitle is the `usr_` id (`PersonPopup.tsx:186-189`). The
  name is about 260 px down, under the profile banner. On a phone the banner fills the first screen.
- vrchat.com's profile and Discord's profile card both lead with the name, big.
- The standing bar says "4 actions, 3 mods" (`Standing.tsx:93`). "mods" is chat slang.
- Two of the nine tabs are for developers: **Metrics** and **JSON** (`PersonPopup.tsx:180-181`).

**Alternative:** the title is the person's name, with trust rank and platform beside it; the id moves
to a copy button. "3 moderators". JSON moves behind the popup's ⋯ menu; Metrics becomes **Numbers**.

### 5. Engineer words on the lists everyone opens

| Where | Now | Plainer |
|---|---|---|
| Members, Bans (`Freshness.tsx:55-57`) | Last synced 17m ago. A new sweep is running now. 4,790 members at the last full sweep. | Checked with VRChat 17m ago · checking again now · 4,790 members |
| People, Members (`People.tsx:337,345`) | Known for | First seen |
| People (`People.tsx`) | Profile 20m ago | Profile read 20m ago |
| Bans (`Bans.tsx:193`) | Modbot first saw it | Seen by Modbot |
| Bans | Bans that stand (sort menu) | Still banned |
| Person popup, Repeat offenders (`SubjectHistory.tsx:59`, `RepeatOffenders.tsx:100`) | Counts rebuilt 3m ago. | Counted 3m ago |
| Reviews (`Reviews.tsx:109`) | Detection last ran 12m ago | Last checked 12m ago |
| Team (`MyTeam.tsx:50-64`) | Coverage gaps · Instances nobody watched | Instances with no moderator in them |
| Chat | `moonshotai/kimi-k3` under the box | (nothing; Settings → AI names the model) |

The `usr_` id is printed under every name on People, Members and Bans (`People.tsx:473`). Requests
and Members dropped it on 2026-09-25 (`5653157a`); the People merge brought it back. It is the
widest thing in each row and means nothing to a moderator; the popup has it.

### 6. The VRChat page stops looking like VRChat below the tab row

- **The charts are still on the Overview.** Below About and Rules: a range picker, six numbers, three
  charts, an invites panel, a roles table, a tenure panel. The VRChat review's finding 3 (a Stats tab)
  was not built.
- **One chart is wrong for this group.** "How long members have been members: Over a year **0**",
  for a group founded Dec 6, 2022 with 4,791 members. The panel counts 238 members with a known join
  date (`MyGroup.tsx:194-199`); the rest are missing, not zero.
- **The type is Modbot's, not VRChat's.** vrchat.com uses big bold card titles ("About This Group",
  "Rules"), a rounded sans throughout and its teal for the chosen tab. Modbot's cards have 13 px titles,
  and numbers, dates and the group code are in the typewriter font.

**Alternative:** a **Stats** tab at the end of the row (the earlier review's shape); on the VRChat
page only, VRChat's large card titles and the sans font for numbers; the tenure panel says "238 of
4,791 known" in its title and hides buckets it cannot fill.

### 7. The Discord page is a dashboard with Discord's header on top

- Discord lays a server out as a **column**: banner, boost bar, "5 Events", Channels & Roles, Members,
  then the channels by category, with voice channels listing who is in them. Modbot's Discord page
  uses a sideways **tab row** (Overview · Members · Voice now · Events · Bans), which Discord never
  does.
- The VRChat row has icons; the Discord row has none, so the two sibling pages are built differently.
- Voice now leaves the page for Live. Bans opens VRChat's bans (finding 1).
- Discord's Members page shows relative times ("18 hrs ago", "4 years ago"); Modbot's shows "Sep 16"
  beside "2 months".

**Alternative:** the Discord page's tab row becomes Discord's column items, with Discord's icons
(calendar "N Events", people "Members", speaker "Voice"), the event count in the label, and Bans
removed. Later, the voice channels and who is in them drawn under the header the way Discord draws
them, from what Live already reads.

### 8. On a phone, a third of the bottom bar is a keyboard help sheet

The bar is Menu · Search · Actions. On Now, Actions opens "Search and commands" and "Keyboard
shortcuts" (`App.tsx:499-500, 653`), neither of which a phone has use for. The 2026-09-25 review
found this (finding 7); it is still there on every page without actions of its own.

**Alternative:** Actions shows only when the page has actions; otherwise the bar is Menu · Search.

### 9. In a headset, the banner fills half the screen on every VRChat tab

At 1264 × 799 in the Headset layout, the banner and header reach y = 500. On Members, Banned Users and
Events the list starts below that, so the first screen is a picture.

**Alternative:** the full banner on Overview only; the other tabs draw a slim strip (icon, name, tab
row). vrchat.com's banner scrolls away as soon as you scroll; Modbot's is on top of every working list.

### 10. The same word for different things

| Word | Means |
|---|---|
| People | The sidebar's list of everyone; Settings → People (Modbot's own accounts) |
| Settings | Modbot's settings (sidebar); the group's settings (VRChat → Settings) |
| Roles | VRChat group roles; Modbot account roles; Discord roles |
| Members | The group's members; Discord members (both "Members" in their own rows) |
| Requests / Join requests | The same list, called both |
| Bans / Banned Users | The same page, called both |

With the site map below, most of these resolve by where they sit: "Members" under VRChat and under
Discord. Settings' "People" heading becomes **Modbot accounts**.

### 11. Smaller things

- **Bans:** 1,180 of 1,180 rows say "Write the case file". A link repeated on every row is a column of
  noise; "—" with the pencil on hover, and a "No case file" filter, says the same.
- **Audit log:** five of the first eight entries on the live group are Discord voice joins and moves.
  Someone looking for "who banned X" scrolls past them. Voice moves could be off by default.
- **Now** has no Discord in it, though Live shows who is in voice.
- **Header counts disagree by one:** VRChat header 4,791 members, Members list 4,790.

## What is right and should stay

- The VRChat header and tab row, with VRChat's icons, names and order.
- Invites now opens with Join Requests, as vrchat.com does.
- The standing bar and Kick / Ban pinned to the bottom of the popup on a phone.
- Settings' grouped side list.
- The Discord "This week" strip and the "Reads 35 of 165 channels · No audit log" line.

## Proposed site map

```
[group banner]  -Thy Kingdom 18Plus
Search                       Ctrl K
Now                            1
Live

VRChAT ───────────────────
  Group            (Overview, Posts, Gallery, Invites, Settings: the page as it is)
  Members          (People narrowed to members; lights here, titled Members)
  Join requests    1
  Instances
  Worlds
  Banned users     (the Bans page; lights here, titled Banned users)

DISCORD ──────────────────
  Server           (the Discord page)
  Members          (Discord members)

MODERATION ───────────────
  People           (everyone, both platforms)
  Flags
  Reviews
  Audit log
  Team

PLANS ────────────────────
  Calendar
  Giveaways

MODBOT ───────────────────
  Chat
  Modbot's log
  Settings
```

- **Why:** the two headings a moderator already knows come first after Now and Live, and each holds
  what that platform's own page holds, so a VRChat tab and its sidebar entry are the same thing.
- **Addresses do not change.** Only labels, order, headings and which entry lights.
- **Events** stays one shared Calendar under Plans: an event goes to both platforms.
- **Downside:** 20 entries instead of 17. They sit under five headings, so the eye reads five
  groups, not twenty lines.
