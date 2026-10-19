# UX review — ideas sized as chips

Date: 2026-09-27. Running app at http://localhost:8080 (test group "Sample Group"), code at
`origin/staging` 699aa498. Checked at desk width (1280–1920) and at 390 px (a 390 px frame, since the
shared Chrome window stayed maximised), plus Headset mode. Nothing was saved, banned, posted or
deleted. The sidebar changed mid-review (another session deployed a "Community" grouping that is not
on staging yet), so no idea here is about sidebar order.

Each idea says **seen** (in the running app), **code** (confirmed in source) or both. Earlier
findings are included only where the running app still shows them, marked *still open from …*.
Skipped on purpose: the Calendar one-platform sync (its own chip is on it).

## Status, 2026-09-29 (checked against origin/staging code; not seen in a browser)

| # | Status | Commit |
|---|---|---|
| 1 | Done, staging + master | 6d20971f (+ 6e66f2de on the audit log) |
| 2 | Done, staging + master | 0ab80298 |
| 3 | Done, staging + master | 5188486a |
| 4 | Done, staging + master | 4eb173d8 |
| 5 | Done, staging only | 802b1cd9, 6289c794, 9a512cdc |
| 6 | Done, staging + master | 462ad14d |
| 7 | Done, staging + master | d802309d |
| 8 | Done, staging + master | 70cb922a |
| 9 | Done, staging + master | c0c9e24b |
| 10 | **Still open**: the chart keeps its own range on purpose (MemberCountChart.tsx comment, older than this review); the page range label still uses the server's UTC days (`charts/format.ts` dayRange) | none |
| 11 | Done, staging + master | eeb87976 (+ a8cd5687 for worlds/instances) |
| 12 | **Still open**: `pages/Chat.tsx` still ends at "Chat is off."; Chat is still in `lib/nav.ts` for everyone with UseAiChat | none |
| 13 | Done, staging only | 0d6926fb, 68f950aa |
| 14 | Done, staging + master | 3144790a |
| 15 | Done, staging + master | 8aabb762 |

## Ranked

| # | Idea | Page | Size | Needs a decision? |
|---|---|---|---|---|
| 1 | Person's Activity list has no dates, and shows some things twice | Person popup | S | No |
| 2 | Search can find a person but can't act on them, and shows an id instead of who they are | Search (Ctrl K) | M | Yes: verbs in search? |
| 3 | Dates and times are written four ways; "563d ago" never becomes months or years | Everywhere | M | No |
| 4 | Person popup wording: "lifted by Sep 27", sync ages, two "No … account" rows | Person popup | S | Small |
| 5 | Two member counts on one screen (5 vs 4) | VRChat → Members | S | No (find cause first) |
| 6 | VRChat tabs: banner fills half the screen; page is titled "People" under a VRChat header | VRChat tabs | M | Yes: title + sidebar |
| 7 | People list on phone and headset: sideways table, "Standing —" and "Profile read" on every row | People | M | Yes: drop a column? |
| 8 | "Actions" on phone/headset list pages holds only keyboard moves (Next row, Previous row) | Bottom bar | S | No |
| 9 | Chart date labels skip days (Sep 22, 23, 25, 26, 27) | Stats, Discord | S | No |
| 10 | Stats: two range controls that disagree, and a range that ends tomorrow | Stats | M | Yes: which control stays |
| 11 | Person tabs "Metrics" (one panel) and "JSON" (raw data) up front | Person popup | S | Yes |
| 12 | Chat when off: a dead end still in the sidebar with its own shortcut | Chat | S | Yes |
| 13 | Remembered filters look like a short log ("12 entries shown") | Audit log, lists | S | Yes: keep remembering? |
| 14 | 17 pairs of keycaps down the sidebar | Sidebar | S | Yes (just shipped) |
| 15 | Calendar opens on an empty Day grid on a phone | Calendar | S | Yes |

---

## 1. Person's Activity list has no dates, and shows some things twice — S

**Seen.** `/people?subject=usr_da80…` (FenyaKrautCutie) → Activity. Rows read 07:52 AM, 07:52 AM,
03:11 AM, 03:08 AM, 03:02 AM, 03:02 AM, **11:09 AM**, 11:09 AM. The last two are an earlier day, and
nothing says which. The date lives only in a hover title, which a phone or a laser pointer never
shows. The first two rows are the same fact ("joined the group") from two sources, VRChat and Sync.

**Code.** `components/facts.tsx:80-100` (`FactTime`) prints `clockTime` only. `FactList` in
`components/subject/shared.tsx:138-170` has no day grouping. The same list draws the Overview
"Latest" panel and the instance popup.

```
BEFORE                                  AFTER
VRChat          07:52 AM                Today
 joined the group                        VRChat · Sync   7:52 AM
Sync            07:52 AM                  joined the group
 joined the group                        VRChat          3:11 AM
VRChat          03:11 AM                  modbot_alpha invited …
 …                                      Sep 26
Sync            11:09 AM                 Sync            11:09 AM
 showed as 18+ verified                   showed as 18+ verified
```

**Fix.** Day headings in `FactList` ("Today", "Yesterday", "Sep 25", year when not this year).
Merge rows that are the same fact seen by two sources into one row with both badges. Tests for the
grouping and the merge.
**Why.** "When did we last deal with them?" is the popup's first question. A clock time with no day
answers it wrongly as soon as the list crosses midnight.

## 2. Search can find a person but can't act on them — M, decision

**Seen.** Search → "ban fenya" → *Nothing matches*. "fenya" → one row: name +
`usr_da80ded9-c8d9-416b-861d-0fe26a96ac40` in grey. Nothing says whether they are a member, banned
or a visitor. "ban" → only "Go to Bans".

**Code.** `components/CommandPalette.tsx:275-281` prints `item.detail` (the id) in mono.

```
BEFORE                                   AFTER
┌ ban fenya ─────────────── Esc ┐        ┌ ban fenya ─────────────── Esc ┐
│ ▢ Nothing matches             │        │ People                        │
└───────────────────────────────┘        │ ◯ FenyaKrautCutie   Member    │
                                         │   Ban FenyaKrautCutie…        │
┌ fenya ─────────────────── Esc ┐        └───────────────────────────────┘
│ People                        │
│ FenyaKrautCutie  usr_da80ded9…│        ┌ fenya ─────────────────── Esc ┐
└───────────────────────────────┘        │ ◯ FenyaKrautCutie   Member    │
                                         └───────────────────────────────┘
```

**Fix.** A person row shows their standing (Member / Banned / Visitor / Left), not the id (search by
id still works). A leading verb the moderator can already do — ban, kick, note — plus a name offers
"Ban FenyaKrautCutie…", which opens the popup with the ban dialog open. Permission-gated like the
buttons.
**Why.** Finding a person and banning them is the job a moderator does under pressure. It's
currently search → popup → Ban → dialog; this makes it search → dialog.
**Decision.** Verbs in search, or standing only?

## 3. Dates and times are written four ways — M

*Still open, in part, from 2026-09-25-ux-review.md #23.*

**Seen / code.**

| Where | Shows | From |
|---|---|---|
| Now, "Since you last looked" | `Sep 27 08:44 PM` | `pages/Now.tsx:499` formatDay + clockTime, no comma |
| Audit log "When" | `02:57 AM Sep 27` (time first) | `pages/AuditLog.tsx:503-505` |
| Stats tiles, Live | `Sep 25, 11:53 AM` | `components/charts/format.ts:44` dateTime |
| Instances, ranges | `8:04 PM` (no leading 0) | `lib/format.ts:245` timeOfDay — its comment says "not 08:04 PM" |
| Facts, clocks | `08:04 PM` | `lib/format.ts:97` clockTime (`2-digit`) |
| People "First seen" | `505d ago`, `563d ago` | `lib/format.ts:107-117` ago() stops at days |

**Fix.** One shape: `Sep 27, 8:44 PM` (year when not this year), and a time on its own as `8:44 PM`.
`clockTime` and `timeOfDay` become one helper. Audit log puts the day first. `ago`/`howLong` go on
past days: `45d ago` → `2 mo ago`, `563d ago` → `1 yr ago`. Update the format tests.
**Why.** A moderator comparing the audit log with the popup should not have to re-read a time to
see that it's the same one.

## 4. Person popup wording — S, small decision

**Seen.** FenyaKrautCutie → Membership card:
"Was banned on Sep 27; **lifted by Sep 27**." — reads as "lifted by a person called Sep 27". It means
"no later than", because the ban-list check noticed it.
"Member list synced 7m ago; ban list synced 23m ago." — engineer text on every popup.
"No Discord account." and "No Modbot account." each take a full panel. On a phone they push
Membership and Past actions below the first screen. The ban dialog's title repeats the `usr_` id under
the name.

**Code.** `components/subject/PersonPopup.tsx:627-628` ("lifted by"), `:639-640` (sync ages),
`:258, :261` (the two empty rows).

```
BEFORE                                  AFTER
▢ No Discord account.                   Not linked to Discord or Modbot
▢ No Modbot account.                    Membership
Membership                               Member since Sep 27
 Member since Sep 27.                    Ban lifted Sep 27
 Was banned on Sep 27; lifted by Sep 27. Checked 7m ago
 Member list synced 7m ago; ban list …   [Kick] [Ban]
```

**Fix.** "Ban lifted Sep 27" (or "Ban lifted by Sep 27" → "Ban lifted, noticed Sep 27" if the
exact day is unknown). One "Checked 7m ago" (the older of the two ages). One row for no links. Name
only in the ban dialog title.
**Why.** Plain words (CLAUDE.md); one popup, less scrolling on a phone.
**Decision.** For "lifted": "Ban lifted Sep 27" or "Ban lifted, noticed Sep 27".

## 5. Two member counts on one screen — S

*Still open from 2026-09-27-site-adversarial-review.md #11 ("Header counts disagree by one").*

**Seen.** VRChat → Members: the header says **5 members**, and the list right under it says
**4 members · Synced 10m ago** (FenyaKrautCutie, E-Ray Racer2025, ~Zuelatak~, bin¹). modbot_alpha
shows on People as Standing "—".

```
Sample Group   ● 0 online   👥 5 members        ← VRChat's count
[Overview][Posts]…[Members]…
Membership is Members  ✕
4 members · Synced 10m ago                     ← Modbot's list
```

**Fix.** Find which member is missing (likely the bot's own account, or a member added after the last
list read) and either include them or make both numbers come from the same read. No new text.
**Why.** Two numbers that disagree make both untrustworthy.

## 6. VRChat tabs: the banner fills half the screen, and the page says "People" — M, decision

*Banner part still open from 2026-09-27-site-adversarial-review.md #9, and it's true at desk width
too, not only in a headset.*

**Seen.** VRChat → Members at 1920×889: the banner and header reach y≈490, so the list starts
halfway down. The page title reads **People** and the sidebar lights **People**, under a VRChat
header and tab row. Banned Users goes to `/bans?from=group` the same way.

**Code.** `lib/nav.ts` — `members` is `under: 'people'`. The header is drawn on every tab
(`pages/analytics/GroupPageTop.tsx`).

```
BEFORE (Members tab)                    AFTER (Members tab)
People                      (title)     VRChat                     (title)
┌──────────────────────────────┐        ◯ Sample Group · 0 online · 5 members
│        big banner image       │        [Overview][Posts]…[Members]…
│  ◯ Sample Group               │        Membership is Members ✕
└──────────────────────────────┘        FenyaKrautCutie …
[Overview]…[Members]…                    E-Ray Racer2025 …
Membership is Members ✕
4 members …    ← list starts here
```

**Fix.** Full banner on Overview only; other tabs get a slim strip (icon, name, counts, tabs). When
the list is opened from the VRChat page (`from=group`), the title and sidebar say VRChat.
**Decision.** Should `from=group` light VRChat, or keep lighting People everywhere?

## 7. People list on phone and headset — M, decision

**Seen.** 390 px: the table is 592 px wide inside a 390 px panel and scrolls sideways; only Person
and Standing fit, so "last seen" is off-screen. Headset: each row repeats its labels, and 10 of 14 rows
say "Standing —". Desk: the "Profile read 53m ago" column (when Modbot last fetched their VRChat
profile) is the same on almost every row and answers no moderator question. "First seen 505d ago".

**Code.** `pages/People.tsx:336-346` (columns).

```
PHONE BEFORE (scrolls →)                PHONE AFTER
Person            Standing │ Last…      ◯ FenyaKrautCutie  18+   Member
FenyaKrautCutie   Member   │ 14h          Trusted User · seen 14h ago
modbot_alpha      —        │ 16h        ◯ modbot_alpha           Visitor
                                          seen 16h ago
```

**Fix.** On phone and headset, a row is two lines: name + badges + standing on the first, rank and
"seen 14h ago" on the second. No "Standing —": leave it blank or say Visitor. Drop "Profile read"
from the list (it's in the popup).
**Decision.** OK to drop "Profile read" from the list?

## 8. "Actions" on phone and headset holds only keyboard moves — S

*Still open (in part) from 2026-09-25-ux-review.md #7 and 2026-09-27-site-adversarial-review.md #8.
Fixed on Now; still on list pages.*

**Seen.** Headset → People → bottom bar **Actions** → a sheet with "Next row", "Previous row",
"Open the selected row", "Search", "Add a filter". With a laser pointer or a finger you point at the
row. Search is already the button beside it.

**Code.** `components/Chrome.tsx:404` shows Actions whenever the page registers any action, and list
pages register their keyboard moves.

```
BEFORE  [ Menu ][ Search ][ Now ][ Actions ]   → Next row / Previous row …
AFTER   [ Menu ][ Search ][ Now ][ Filter ]    (or no 4th button)
```

**Fix.** Mark keyboard-only actions (row moves) so the bottom bar ignores them. Show Actions only
when something is left. On list pages, the 4th button becomes Filter.
**Why.** A quarter of the bar on the device a moderator uses mid-event should do something.

## 9. Chart date labels skip days — S

**Seen.** Stats → Growth → Member count (Week): axis reads **Sep 22, Sep 23, Sep 25, Sep 26, Sep
27**. Sep 24 looks missing; nothing is.

**Code.** `pages/analytics/memberCountSeries.ts:82-89` `timeTicks` cuts the span into 6 equal parts
(every 1.2 days for a week), and `timeLabel` prints each as a date, so labels land mid-day and skip
one.

```
BEFORE  |Sep 22   |Sep 23   |Sep 25   |Sep 26   |Sep 27
AFTER   |Sep 21 |Sep 22 |Sep 23 |Sep 24 |Sep 25 |Sep 26 |Sep 27
```

**Fix.** Ticks at local midnights with a step of 1, 2, 7 days or 1 month that fits about 6 labels.
Hours on a ≤2-day axis, on the hour. Tests in the existing series tests.

## 10. Stats: two range controls that disagree, and a range that ends tomorrow — M, decision

*Still open from 2026-09-26-analytics-ux-review.md F13.*

**Seen.** Stats → Growth: the page range is **30 days · Aug 30 – Sep 28** (it was Sep 27 here). The
Member count chart has its own **Day / Week / Month / All**, set to Week, and draws Sep 21–27. Two
controls, two ranges, one screen. The end date is the UTC day.

```
BEFORE                                          AFTER
[7 days][30 days][90 days][All time]  Aug 30 – Sep 28      [7 days][30 days][90 days][All time]  Aug 29 – Sep 27
Member count        [Day][Week][Month][All]      Member count           (follows the page)
```

**Fix.** The chart follows the page range (drop its own control, or keep "Day" as a zoom only).
Range labels in the viewer's days.
**Decision.** Which control stays?

## 11. Person tabs: "Metrics" and "JSON" — S, decision

*Still open, in part, from 2026-09-25-ux-review.md #11 (tabs went from 10 to 8, and these two remain).*

**Seen.** Tabs: Overview, Activity, Notes, Profile changes, Cases, Flags, **Metrics**, **JSON**.
Metrics holds one panel ("Time in world — Not seen in an instance yet."). JSON is the raw record in
green monospace. On a phone the row scrolls and these two push Flags off-screen.

**Code.** `components/subject/PersonPopup.tsx:181-182`.

**Fix.** Metrics moves into Overview (it already has the "Time seen" strip). JSON becomes "Raw
data" at the foot of Overview or under a ⋯ menu, for people who can see it.
**Decision.** Keep JSON as a tab for owners only, or move it into a ⋯ menu?

## 12. Chat when it's off — S, decision

**Seen.** Sidebar → Chat (also `g c`) → "Chat is off." and nothing else. The page is in the
sidebar for everyone with UseAiChat, whether or not it's on.

**Code.** `pages/Chat.tsx:319-326`.

**Fix.** When chat is off: hide it from the sidebar and search for people who can't turn it on; for
people who can, the page shows a **Turn on** button to Settings → AI. A button, not a sentence.
**Decision.** Hide it, or keep it with the button?

## 13. Remembered filters look like a short log — S, decision

**Seen.** Sidebar → Audit log opened already filtered to "Source is any of Companion App, Sync,
VRChat +4" and "About is ~Zuelatak~", left from an earlier visit, and says "12 entries shown".
Nothing on the count shows the list is narrowed.

**Code.** `lib/filters.ts:172-190` remembers chips per page in localStorage.

```
BEFORE  12 entries shown
AFTER   12 of 1,204 entries   [Clear]
```

**Fix.** The count says "N of M" whenever a filter is on (needs the total from the server, or
"12 entries · filtered"). Or: opening a page from the sidebar starts clean, and Back brings the
filters back.
**Decision.** Keep remembering filters?

## 14. 17 pairs of keycaps down the sidebar — S, decision

**Seen.** Every sidebar row ends in two keycaps (G K, G J, G N …): 34 little boxes beside 17
labels.

**Code.** `components/Chrome.tsx:133-138` (just shipped in 11bb7b30).

**Fix.** Keycaps show on hover and for a few seconds after pressing G; the rest of the time the list is
labels only. The Search hint stays.
**Decision.** This was just shipped. Keep it as is, or quieter?

## 15. Calendar opens on an empty Day grid on a phone — S, decision

**Seen.** 390 px → Calendar → Day view, 8 AM–7 PM of empty hours. Schedule (a list) is one tap away.

**Fix.** On a phone the Calendar opens on Schedule. A chosen view is remembered.
**Decision.** Schedule by default on a phone?

---

## Checked and fine

- **Ban flow feedback.** The ban dialog shows the result in place, says when a case file was
  written, and offers evidence upload (`components/moderation/ModerationActions.tsx:292-330`). Opened,
  not submitted.
- **Phone bottom bar on Now** is Menu · Search · Now (the keyboard-sheet problem is fixed there).
- **Touch sizes.** `index.css:217-220` raises rows to 46 px and controls to 44 px on a coarse
  pointer at phone width. My 30 px reading came from a mouse.
- **Person popup on a phone** keeps Note · Kick · Ban in a bar at the bottom.
- **Structure.** One `h1` per page, main/nav/aside/header/footer landmarks, `aria-current` on the
  sidebar, alt on every avatar on People.
- **Now** shows what is waiting, instances and changes since last look, and "All working".
- **VRChat Overview** matches vrchat.com's layout. "This week" compares with last week.
- **Settings** now has a left-hand section list instead of 14 tabs in a row.
- **Live** labels its sections VRChat and Discord voice. "Off" beside "Updated 1s ago" was because
  my tab was in the background (the stream stops while hidden, `pages/Live.tsx:48-50`).

## Not checked

- Keyboard focus rings. Chrome didn't pass Tab presses to a background tab in the shared window.
- Contrast. My script misread the `oklab()` colours, so I have no numbers.
- Pages with real data in them (Requests, Flags, Reviews, Giveaways are empty in the test group).
- Light theme.
