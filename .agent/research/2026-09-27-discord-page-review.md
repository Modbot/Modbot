# Discord page — design review

**Date:** 2026-09-27
**Page:** Analytics → Discord (`src/Modbot.Web/src/pages/analytics/MyServer.tsx`, `ServerHeader.tsx`,
`lib/serverOverview.ts`), its Members part (`pages/DiscordMembers.tsx`), the server side in
`src/Modbot.Api/Features/Analytics/Server/`.
**Looked at:** the live group's page (`/analytics/server`, 30 days, Aug 29 – Sep 27) and `/discord/members`
read-only in Chrome at 1028 px; the read-only `/api/discord/channels` answer on the live install; the local
copy (a four-member test server) in the Headset layout. Beside it, the real Thy Kingdom server in Discord's
web app: the server header and boost bar, the channel list, the per-channel member list, the Members page,
the Events list, and Server Insights (Overview, Growth & Activation, Engagement, Audience). Nothing was
changed in either place and no messages were read beyond what was on screen.

**Reader:** a volunteer moderator who knows Discord well and nothing else.

---

## Verdict

The header works. Banner, rounded icon, name, "● 186 Online · ● 797 Members", the boost bar and a row of
links: a Discord user knows at once which server this is. Below the header the page stops speaking
Discord. It opens on a range picker and a 30-day grid, where Discord's Server Insights opens on **four
numbers for this week, each with its change from last week**. Some of what it shows is quietly incomplete,
because the bot reads only part of the server, and the page does not say so.

The three worst problems, in order:

1. **It shows gaps as zeros.** The bot can read 35 of the server's 165 channels and cannot read the audit
   log. So "Moderation actions: Nothing recorded in this range" is not true, and #staff-chat (32 messages
   in 28 days on Discord's own count) is missing from Busiest channels and every total.
2. **It gives no "how are we doing this week?"** Everything is a 30-day sum with nothing to compare it
   to. Discord leads with *New members 4 ▼50%, Communicators 21 ▼12.5%, Retention 100% ▲*. A moderator
   cannot tell from Modbot's page whether things are getting better or worse.
3. **The member list is unreadable.** Every role is listed, separator roles included
   (`__________🪙Supporter🪙__________`), so one row runs to twenty or thirty lines. Discord's own Members
   page shows one role and "+N".

---

## Findings, ranked

### 1. Gaps shown as zeros — the bot reads 35 of 165 channels and no audit log

**What:** `/api/discord/channels` on the live install: 165 live channels that are not categories, 35 with
both View Channel and Read Message History for the bot. `botCanViewAuditLog` is false. The unreadable ones
include #staff-chat, #meetings, #off-topic, the ticket channels and every voice channel's text chat except
one.

**Effect on the page:**
- *Moderation actions per day* says "Nothing recorded in this range." Bans arrive from the gateway and
  timeouts from member updates, so those two are complete; but kicks and removed messages are read only
  from the audit log (`DiscordEventRecorder.cs:443`, `:474`), which the bot cannot read. Thy Kingdom's
  captcha bot kicks people who do not verify, and none of that can show here.
- *Messages*, *Busiest channels*, *Top contributors*, *Active* and *Went quiet* count only the 35
  channels. Discord's Insights has #staff-chat 4th with 32 messages; Modbot's list does not have it.
  Staff who talk mostly in staff channels read as having gone quiet.
- *Busiest channels* lists channels the bot could read in the past and cannot now ("were dyin squirtle"),
  so the list also changes as permissions change.

**Alternative:** say it where it matters, in the words of an error, not an explanation:
- the coverage line under the range: **"Reads 35 of 165 channels · No audit log"**, linking to
  Settings → Discord, where the channel list with the bot's permissions already is;
- the moderation panel, when the audit log is off: **"No audit log: kicks and removed messages
  missing."** above the chart, and no "Nothing recorded" while that is so.

Fixing it is the server owner's job in Discord (give the bot's role View Channel, Read Message History and
View Audit Log). No new request or intent: the permissions are already stored on each channel's row.

### 2. No "this week" numbers, and nothing compared

**What:** the first numbers are Members 797, Joined 32 (in 30 days), Still here after 7 days 13%; the
engagement numbers are three screens down. None says whether it is up or down.

**Discord:** Server Insights' first screen is four tiles, each "N" and "▲/▼ x% since last week":
Weekly visitors, Weekly communicators, Weekly new members, Weekly new member retention. Its Engagement
page leads with Visitors, Communicators, Messages sent, Voice minutes, each with the same arrow.

**Alternative:** a **This week** strip straight under the header, before the range picker, whatever the
range:

| New members | Talked | Messages | Voice |
|---|---|---|---|
| 17 ▲ 2 | 25 ▲ 3 | 33 ▼ 14 | 165 h ▲ 11 h |

(The live install's own numbers for Sep 21–27 against Sep 14–20. They differ from Discord's: Discord
counted 6 new members, where Modbot counts every join: of this week's 17 joins, 10 have already left
(most likely the captcha kicks) and one is a bot, which leaves Discord's 6. Discord's voice figure is minutes spent speaking; Modbot's is time
connected.)

"Talked" is people who sent a message or were in voice (Modbot's own "active"; Discord's chart legend
says "talked" too). "Visitors" is left out: Discord counts people who opened the server, which no bot can
see. All four come from daily totals Modbot already keeps; the server returns this week and the week
before as one small block, so the strip does not depend on the range picked.

### 3. The Members list shows every role

**What:** 50 rows a page; on the first page a row lists 7 roles in the middle case and 43 at most, with the server's separator roles
(`_____🪪Bio🪪_____`, `____📦Misc📦____`) among them. On a phone one row is taller than the screen.
"Timed out until" is "—" on every row on the first page.

**Discord:** the Members page (Server menu → Members) has *Name · Member since · Joined Discord · Join
method · Roles · Signals*. Roles shows the top role as a chip and "+N". Times are relative ("2 days ago").

**Alternative:**
- **Roles:** the highest role by Discord's order (roles already carry `position`), and "+N"; the full list
  stays in the person popup. Roles whose name has no letters once the underscores and emoji are taken away
  are not the "top" role.
- **Account age:** a new column, worked out from the user's id (the same way "Est." is worked out from
  the server's id). Discord shows it because new accounts are the first thing a moderator checks. No request.
- **Timed out:** a red "timed out" mark beside the name instead of a column that is empty on every row.
- "Joined" stays a date; a relative time can come later.

### 4. The same thing counted three ways

**What:** "Members 797" (Discord's count, bots in) and, lower down, "Members 766" (bots out). "Active in
30 days" three times: 36 in the Engagement strip, 37 in the Active members panel, "5%" in Member health.
The numbers are each right and read as a mistake.

**Alternative:** one member count, Discord's, as the header already shows. One "active" figure: the
Active members panel keeps its Day / 7 days / 30 days; the Engagement strip and Member health stop
repeating it. Member health keeps *Went quiet* and its list, which is the one part that is Modbot's own.

### 5. Member count chart says nothing at this size

**What:** a straight line at 797 on a 0–800 axis, with 60% of the width hatched "no data" (the bot has
13 days of the 30). The day-by-day change (+32 joined, −N left) is in the next panel.

**Alternative:** keep the chart's 0-based axis (decided on 2026-09-26) but put it behind the Joins and
leaves panel as a toggle — **Joins and leaves / Member count** — so the first chart a moderator sees is the
one that moves. The Members tile gets "+N this month" under it.

### 6. The header waits for everything

**What:** the page draws nothing but "Loading…" until the whole analytics answer arrives (5 s and more on
the live install; the screenshot timed out twice while it rendered). The header does not need any of it:
`GET /api/discord/server` already returns it on its own, for the Members part.

**Alternative:** read the header from `/api/discord/server` straight away, as the Members part does; the
charts load under it.

### 7. Busiest channels don't look like Discord's channel list

**What:** every row is "#name", voice channels' text chat included ("#Aniimo", "#Modbot Dev Work"). Two
voice channels are both called "zomboid?" and both appear as "#zomboid?". No category.

**Discord:** `#` for text, a speaker for voice, a forum icon for forums, and the category they sit in. Its
Insights table adds readers and chatters per channel.

**Alternative:** the icon Discord uses for the channel's type, the name without a forced "#", and the
category in grey after it ("zomboid? · Voice channels"). Channel rows already carry type and category.
Top 10, with the rest behind "Show all".

### 8. Moderation sits inside Growth

**What:** "Moderation actions per day" is the fourth panel under Growth. In Discord, Bans and Audit log
sit under a heading of their own (Server settings → Moderation). Discord's Insights has no moderation
numbers at all, so this is where Modbot knows more than Discord and hides it.

**Alternative:** a **Moderation** section after Engagement: bans, kicks, timeouts and messages removed per
day (the same chart), and a "Bans" link. With finding 1, it says when kicks and removed messages are
missing.

### 9. What Modbot knows and Discord doesn't isn't shown

Modbot has, and this page does not show:
- **Linked to VRChat:** how many members have linked an account. It is a filter on the Members list and
  nowhere else. (On the live install today: 0 of 797, which is itself worth seeing.)
- **New accounts joining:** account age from each joiner's id. Discord's Audience page asks "Are my new
  members also new to Discord?" (35% under a month here).
- **How long members have stayed:** tenure from join dates. Discord's Audience: 57% a year or more.
- **Who went quiet:** already there (Member health), and not in Discord.

**Alternative:** a small **Members** section with three tiles: *Linked to VRChat 0 of 797*, *New
accounts among joiners 35%*, *Here a year or more 57%*, each linking to the Members list filtered to it
where a filter exists. All from stored rows; no request.

### 10. Smaller things

- **A raw id in Top contributors** ("184771246364295168"): someone not in the stored member list. Show
  the name last seen in the fact log, or "Unknown member", never a bare id.
- **"Read 12d ago"** above the Members list reads as stale, though joins and leaves arrive live (a member
  who joined yesterday is in it). Drop it when the bot is connected; keep "not read yet" as it is.
- **Events tab** goes to the Calendar with no count. Discord's sidebar says "5 Events". The VRChat page's
  header already shows the next event; the Discord header could do the same from the same calendar.
- **"Voice now"** goes to Live, which is mostly VRChat instances. Fine as a link; the label is right.
- **Range default** is 30 days while the bot has 13; most charts open 60% hatched. Leave it: the hatch is
  honest, and this fades as the install ages.
- **Phone:** the header and stat strips work at 390 px. The Members table is the problem (finding 3).
  Headset layout: fine; the bottom bar covers nothing important.

---

## What Discord shows that Modbot can't, and what it would cost

| Discord shows | Modbot | Cost to add |
|---|---|---|
| Online per member, grouped by role | Not planned | Presence intent (privileged). Declined on 2026-09-27 in favour of the one online count. |
| Visitors (opened the server) | Can't | Not available to bots at all. |
| Join method (invite used) | Not kept | Reading invites needs Manage Server and a request per join. Not proposed. |
| Server-set boost goal ("20/36 Boosts") | Shows level and count | Not in the bot's data as far as the SDK shows. Not proposed. |
| Readers per channel | Can't | Not available to bots. |
| Voice minutes per channel | Not kept per channel | The voice facts carry the channel; a new daily total. No request. Later. |

---

## Proposed order

1. Say what Modbot can't see (finding 1). Small, and it stops the page misleading.
2. Header first, then **This week** (findings 6, 2).
3. Members list: top role + "+N", account age, timed-out mark (finding 3).
4. One number per thing; member count behind a toggle; Moderation as its own section (4, 5, 8).
5. Busiest channels with Discord's icons and categories (7).
6. The Modbot-only Members tiles (9).

---

## Decisions (2026-09-27, with the user)

All of A–F were taken, with two changes to what is written above:

- **Roles (finding 3):** the chip is the highest role in Discord's own order, dividers included. Skipping
  roles by the shape of their name is a rule built for one server's habit, and servers vary too much
  for it. The same goes for colours: role chips keep the role's own colour, and no role is coloured
  specially because of what it means on one server.
- **Busiest channels (finding 7):** category names are shown as Discord stores them, not trimmed.
- **New members this week (finding 2):** counts people who joined and are still in the server (6 on the
  live install for Sep 21–27, which matches Discord's number), not every join (17).
- **Members tiles (finding 9):** all three were taken: Linked to VRChat, New accounts joining, Here a year
  or more with the bar.

Not taken up here: the raw id in Top contributors, "Read 12d ago" above the member list, an events count
in the header.

### Second round: "The Discord section should remind them of Discord" (2026-09-27)

From the whole-site review (`2026-09-27-site-adversarial-review.md`, finding 7 and the Discord half of
finding 1), decided with the user one at a time:

- **Bans (A):** the Discord row's Bans link opened VRChat's ban list (1,180 VRChat accounts). Not
  dropped: Discord bans get a list of their own, reached two ways. The Bans page has **VRChat** and
  **Discord** tabs (`platform=discord` in the address), and the Discord row's Bans link, and the
  moderation panel's, open the Discord tab with the server's header above it (`from=server`).
- **Where the list comes from:** Modbot keeps it (`discord_ban`). The whole list is read from Discord
  on every fresh sign-in and once a day; a ban or unban event changes its one row with no request.
  Reading needs Ban Members. On the live install the bot does not hold it, and no Discord ban has been
  recorded since Aug 13, so the tab says it cannot read the list until the owner grants it.
  Discord's list carries a reason but no date, so a ban found already in place has no "Banned on".
- **Icons (B):** the row takes Discord's icons and order: chart Overview, calendar Events, people
  Members, speaker Voice now, gavel Bans. **"Events" has no count:** Modbot's calendar had 2 upcoming
  events where Discord's own column said 5, and the bot does not read Discord's event list.
- **Voice channels under the header (C):** later, as its own task.
- **Discord members' Joined (D):** relative now ("11 days ago", "4 years ago"), the day on hover. This
  replaces finding 3's "Joined stays a date".
