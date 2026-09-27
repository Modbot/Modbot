# VRChat page review — against vrchat.com's group page

Date: 2026-09-27. Looked at: Modbot's VRChat page on the live group ("-Thy Kingdom 18Plus",
`grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd`) and the local copy on the test group, beside the
same group's page on vrchat.com (every tab and every Settings sub-tab, looking only). Code read at
staging `70087a41`.

The reader in mind is a volunteer moderator who knows VRChat and nothing else.

## What vrchat.com shows (layouts only)

| Tab | vrchat.com |
|---|---|
| Header | Banner, round icon over its lower edge, name, then green dot + online, people icon + members, `KING.9295` with a copy button. Above it: Represent Group, Report. |
| Tab row | Each tab has an **icon**: bulb Overview · bell Posts · calendar Events · pin Instances · picture Gallery · people Members · envelope Invites · gear Settings · hammer Banned Users. |
| Overview | **Upcoming Event** (picture, category tag, title, description, date, Public/Group, View, See All Events) → **Posts** (scrolling feed, newest first, picture on the right) → **Languages** and **Links** side by side (round badges: `ENG`; Discord / share / Google icons) → **About This Group** and **Rules** side by side as two cards. |
| Events | Display (Upcoming…) and Order pickers, a grid/list switch, event cards. |
| Instances | Open group instances only. Empty: "Nothing to see here!". |
| Gallery | Create Gallery; one card per gallery with Post an Image and Manage Gallery. |
| Members | Role filter, Join order, Search Group Members, then a two-column grid of cards (profile picture strip, round avatar, name). No trust rank on the card. |
| Invites | Invite Somebody, then three folding sections: **Join Requests** (Applicants: accept / reject / block), **Sent Invites**, **Blocked Requests**. |
| Settings | Sub-tabs **General · My Membership · Roles · Logs**. Roles are grouped **Management Roles** (crown for owner, shield for moderation roles) / **Member Roles** / **Default Role** (Everyone), with Create Role. Logs is the audit log as sentences, 25 a page, with a type filter. |
| Banned Users | Ban Somebody, then a list: avatar, name, a red bin to unban. Nothing about when, why or who. |

## Findings, ranked

### 1. The Overview's "Upcoming event" is wrong for most groups
On the live group, vrchat.com's Overview shows a watch party on Sep 29 at 5 PM. Modbot's says
**No upcoming events** and offers Create event. Modbot's card reads Modbot's own calendar only;
events made on vrchat.com or in game are never read (`CalendarVRChatPublisher` only writes, and
reads back only to find its own earlier copy).

A VRChat user reads that card as the group's calendar and concludes the group has nothing on.

**Alternative:** read the group's VRChat calendar when the Overview opens (one request per open,
none on a timer), merge it with Modbot's own events, and show the soonest the way VRChat does:
picture, title, first lines of the description, date, Public/Group, and "See all events". This is a
new kind of VRChat request and needs the user's yes on that rate first.

### 2. Nothing says what is happening right now
The first thing a VRChat user wants from a group page is "is anything on?". vrchat.com answers it
only in the Instances tab. Modbot knows far more (every open group instance, how full it is, who is
in it, arrivals, kicks and bans since it opened, from `/api/live`) and shows none of it on the
Overview.

**Alternative:** a **Right now** card directly under the header: one row per open group instance
(world picture, world name, Group Public / Group+ / Group, region flag, `25/40` people), each
opening the instance popup; when nothing is open, one line "No group instances open". Modbot's
own data, no VRChat request.

### 3. The analytics read as a second page stuck under the first
Below About come a range picker, a "Recording since" line, six numbers, a member count chart, two
join/leave charts, invites and requests, a roles table and a tenure list. None of it exists on
vrchat.com, so a VRChat user has no place in their head for it, and on a phone it is about ten
screens of scrolling past the About text to reach it.

**Alternative:** move it to its own tab, **Stats**, at the end of the row (after Banned Users —
VRChat's tabs keep their order). The Overview keeps a single strip of four numbers for the last
30 days (Members, Joined, Left, Most online at once), and the strip opens Stats. Instances' eight
charts move to Stats too; the Instances tab keeps what VRChat's has (open now) plus the recent
list, which is Modbot's.

### 4. Tabs that leave the page
Events, Members and Banned Users open other Modbot pages: the header disappears, the sidebar
moves, and the person no longer sees "their group page". On vrchat.com every tab keeps the
header.

**Alternative:** those three pages draw `GroupHeaderFor` at the top with their tab marked, the way
Posts and Instances already do, so every tab of the row keeps the banner and the row.

### 5. The tab row has no icons and hides tabs on a phone
VRChat users find tabs by their icons as much as by their names. Modbot's row is text only, and on
a phone it scrolls sideways with no sign that Settings and Banned Users exist to the right.

**Alternative:** VRChat's icons beside each name (lucide has a match for each: Lightbulb, Bell,
CalendarDays, MapPin, Images, Users, Mail, Settings, Gavel). On a narrow screen, wrap the row to
two lines instead of scrolling, so all nine are visible at once.

### 6. Join requests are not under Invites
On vrchat.com, join requests are the first thing in the Invites tab. In Modbot they are a sidebar
page (Requests), and the Invites tab lists only sent invites. A VRChat user opening Invites to
accept someone finds nothing.

**Alternative:** the Invites tab opens with **Join Requests** (the Requests list as it is, with
Person / Asked / History and approve / reject), then **Sent Invites**. The sidebar's Requests
stays for the moderators who use it.

### 7. Settings → Logs leaves the page
vrchat.com's Settings are General · My Membership · Roles · Logs. The live site (older than
staging) showed only General · Roles; staging already has a **Logs** sub-tab, but it opens the
Audit log page with no header and no Settings row, so the person lands somewhere else.

**Alternative:** Logs keeps the group's header and the Settings row above the Audit log (the same
fix as finding 4). Modbot's log is richer than VRChat's (filters by person, instance and type; kept
past VRChat's window), which is worth finding where a VRChat user would look.

### 8. About and Rules are one folding card
vrchat.com shows **About This Group** and **Rules** as two cards side by side. Modbot puts Rules
inside About, under a fold that a VRChat user has never seen.

**Alternative:** two cards side by side from `md` up, stacked on a phone; each with its own pencil.
Drop the fold (the Stats move in finding 3 takes away the reason to fold it).

### 9. Roles are not grouped the way VRChat groups them
The Roles panel lists every role in one table with badges ("moderation", "given on join",
"self-service") and two columns that are zero for most roles. vrchat.com groups roles as
Management / Member / Default with a crown and shields.

**Alternative:** in Settings → Roles and in Stats, group the same way with the same icons, and
show "given / taken away" only when either is above zero.

### 10. Instances: "closed" and "ended" look like the same word
The recent list marks some rows **closed** and others **ended** with no way to tell them apart,
and on a desk screen under ~1100px the When column is cut off.

**Alternative:** say what happened in words a VRChat user uses ("closed by a moderator" / "everyone
left"), and let the When column wrap onto two lines instead of clipping.

### 11. Smaller matches
- **Links:** keep the readable addresses (clearer than VRChat's bare icons) but show a Discord icon
  for Discord links, as VRChat does.
- **Languages:** "English" beats VRChat's `ENG`; keep.
- **Open to new members:** vrchat.com's Settings calls it "Open to New Members: Request Invite".
  Modbot's header could show the same as a small tag beside the code (Open / Request Invite /
  Invite Only), since moderators get asked it.
- **Posts on the Overview:** vrchat.com's Overview has the post feed. Adding the newest post to
  Modbot's Overview would cost one VRChat request per Overview open; worth asking about with
  finding 1, not before.

## What Modbot knows that VRChat does not, and where a VRChat user would look for it

| Modbot knows | Where it belongs |
|---|---|
| Who is in each open instance, arrivals, kicks and bans since it opened | Overview → Right now (finding 2) |
| Who was in a past instance, head count over time | Instances → recent list → instance popup (exists) |
| Member count, joins and leaves over time, busiest times | Stats tab (finding 3), with a four-number strip on the Overview |
| When and why someone was banned, case files, repeat offenders | Banned Users (Modbot's Bans page, which keeps the header per finding 4) |
| A person's Discord, trust rank, earlier moderation | Members / People rows and the person popup (exists) |
| The full audit log, kept longer | Settings → Logs (finding 7) |

## Phone and headset

- The header works on a phone (icon over the banner, name under it).
- The tab row is the weak point (finding 5).
- Headset mode drops tables into stacked blocks and enlarges targets. The Stats move (finding 3)
  helps most here, because charts are the hardest thing to read in a headset.
