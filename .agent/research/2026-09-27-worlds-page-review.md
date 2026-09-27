# Worlds page — design review

**Date:** 2026-09-27
**Page:** Analytics → VRChat → Worlds (`src/Modbot.Web/src/pages/analytics/Worlds.tsx`, server
`src/Modbot.Api/Features/Analytics/Worlds/`)
**Looked at:** the live group (Thy Kingdom, `/analytics/worlds`, 30 days, Aug 29 – Sep 27 UTC) read-only
in Chrome at 1084 px wide, at 390 px (the page in a 390 px frame) and with the VR density, plus the raw
responses of `/api/analytics/worlds?days=30`, `/api/worlds?id=…`, `/api/audit?type=vrchat.group.instance.create`
and the Instances page on the same range. Nothing was changed on the live group.

**Reader:** a VRChat staff member who cares about UI/UX, judging for a non-technical moderator.

---

## Verdict

The page's question is written down in foundation §10.1: **"Which of our worlds actually get used?"**
The page answers a different question: *in which worlds did a moderator's companion see people?* Every
headline number except one comes from presence reports, which exist only while someone from the team is
in the instance with the companion running. On the live group that gives this:

- **Popcorn Palace** was opened 3 times this month and held up to **8 people for 2 h 42 min** (Instances
  page). Worlds says **Time seen: —, Visitors: 0** and sorts it below a world that one person was in for
  13 minutes.
- **Karaoke in Sync!** held **12 people for 4 h 8 min**. Worlds: **0 visitors**, second to last.

So a moderator reading the page would conclude the group never really used Popcorn Palace or Karaoke,
which is wrong. The complete numbers exist (each instance's open and close time and its peak head count
from VRChat), and the page does not show them.

The three worst problems, in order:

1. **It ranks worlds by the weaker source.** Worlds nobody from the team visited read as unused, however
   full they were.
2. **Its numbers disagree with each other and with the pages next to it.** "Visitors 795" counts the same
   person again for every world. "Instances opened" says 4 where the world's own popup says 2. The column
   adds up to 19 where the Instances page says 37.
3. **Its words need a glossary.** "Presence reports" is a headline tile. "Time seen 12 d 20 h" is
   person-time, and it reads as "this world was open for 12 days" when it was open for 11 hours. "Arrivals
   seen" and "Holds" are there too.

---

## Confirmed / not confirmed

### 1. Ranked by presence, so a busy world with no companion reads as empty — **confirmed, the main problem**

- Rows are sorted by `MinutesSeen`, then `Visitors` (`WorldsAnalyticsQuery.cs:84-88`). Both come from
  `PresenceCounts.PerWorldAsync`, which reads presence facts only.
- Head counts are complete. `vrchat_instance.peak_user_count` is raised on every head count
  (`HeadCounts.RaisePeak`). Open time is `closed_at − opened_at`. The Instances page and the world popup
  already show both (`InstanceRows.cs`, "Most at once" and "Open for").
- Live, 30 days: Popcorn Palace, 3 instances, peaks 8 / 6 / 1, 5 h 18 min open → Worlds shows "—, 0, 0".
  Karaoke in Sync!, 1 instance, 12 people, 4 h 8 min → "—, 0, 0". oxxo (spanish), 1 person, 5 min → "—, 0, 0".
  All three are ranked below The Black Cat (one person, 13 minutes, but a companion was there).

### 2. "Visitors 795" counts people once per world — **confirmed, a real bug**

`Worlds.tsx:33` adds up each world's `visitors`. Each of those is `COUNT(DISTINCT subject_id)` **per
world** (`PresenceCounts.cs:86-110`). Anyone seen in two worlds is counted twice. 572 + 91 + 115 + 15 + 1 + 1
= 795. The real number of different people is lower, and the page never computes it.

### 3. "Instances opened" disagrees with the popup and the Instances page — **confirmed, a data-model leak**

- The column comes from the daily total `worlds.instances`, which counts `vrchat.group.instance.create`
  audit entries that have a world (`DailyTotalMetrics.cs:248`, `DailyTotalsJob.cs:306`).
- The world popup's count is `vrchat_instance` rows (`PlacesEndpoints.cs`). The Instances page lists the
  same rows.
- Live: VRChat Home **4** on Worlds, **2** in its popup. Sunset Bar **4** vs **3**. The audit log has two
  VRChat Home creates (Sep 13, Sep 14) that Modbot never saw as instances, and two Sunset Bar creates in
  the same minute (Sep 27 02:19Z).
- The column adds up to **19**. The Instances page's "Opened" tile says **37** for the same range. All 19
  of the difference are audit entries from before Sep 13 with **no world at all**
  (`/api/audit?type=vrchat.group.instance.create`: every entry from Aug 29 to Sep 13 07:27Z has
  `worldId: null`). They count toward Instances and are dropped silently on Worlds.

Why the old entries have no world is **not confirmed**. The likely cause is that the catch-up read them
in a form Modbot could not take a world from. It does not change the fix: count the instances Modbot
actually saw, as the popup does.

### 4. "Time seen 12 d 20 h" is person-time — **confirmed, a design choice to undo**

`MinutesSeen` is the sum of every person's session length (`PresenceCounts.cs:46-74`). Just B Club 4.0
was open for **10 h 50 min** in two instances (4 h 40 + 6 h 10). About 30 people at a time for that long
gives 18,452 person-minutes. `minutes()` prints that as "12 d 20 h". The tile "Time seen 17 d 10 h" in a
30-day range reads as "our worlds were busy for more than half the month". The popup repeats the same
number. No moderator reads "d" as "person-days".

### 5. "Presence reports" as a headline tile, and "Only N presence reports" — **confirmed**

- It is a count of rows in Modbot's own storage (`WorldsAnalytics.PresenceReports`). It answers "how
  much did the companion send", which is a question about Modbot, not about worlds. As one of four
  headline numbers, it takes the place of a real answer.
- The "Only N presence reports in this range" line is a `PageMessage` floating between panels (same
  problem as Instances finding 10).
- If the page's main numbers come from head counts (finding 1), both can go. Presence stays for one
  column, "People seen", where it is the only source.

### 6. "Arrivals seen", "Holds", the world id under every name — **confirmed**

- "Arrivals seen" (668) is a count of sessions, including people "already there" when a companion arrived
  (`WorldsAnalyticsContracts.cs:21`). Next to "Visitors" (572) the reader cannot tell what the difference
  means. It answers no question about worlds. Cut it.
- "Holds" is VRChat's capacity. Alone in its own column it is a number without a comparison. Put it where
  it means something, next to the peak: **"53 of 80"**.
- The world id is printed under every name (`Worlds.tsx:105`). It is the widest thing in the row, and it
  is why the table is 1,020 px in an 816 px panel at 1084 px wide (1,350 px in VR). The popup's title
  already shows the id, and the cell's hover `title` has it too. The Instances review keeps "no world
  ids under names" as a thing to leave alone. This page breaks it.

### 7. Rows don't open anything — **confirmed**

`Worlds.tsx:79` is a plain `<Tr>`. Only the name opens the popup (`WorldLink`). Clicking the numbers
or the picture does nothing (checked live, cursor `auto`). `InstanceTable.tsx:53-61` has the pattern:
whole-row click with `hover:bg-muted/40`, skipping inner links and buttons.

### 8. "Visitors per day, busiest worlds" — **confirmed, cut it**

- Five curved lines (`DailyLine`), so a single evening with 333 people draws a smooth hill over three days
  that were never measured.
- 10 of the 30 days are hatched as "no presence reports", including Sep 25 and Sep 26, which both had
  instances.
- The Sep 26 evening, the busiest of the month by head count (53), falls on UTC Sep 27 ("today") and is
  drawn only as a hollow ring at the right edge.
- The same presence source as finding 1, so it has the same blind spot.
- The popup already has "per day" charts for one world, which is the only place per-day history for a
  world is worth a chart. With the table sorted by time open and a bar in each row, the page needs no
  chart of its own.

### 9. Phone and VR — **confirmed**

- **390 px:** the tiles wrap three plus one orphan ("Presence reports" alone). The table shows World,
  Holds and Time seen. Visitors, Instances opened and Last seen are off to the right behind a thin
  scrollbar, and each name is cut to "by Blue-kun · wrld_3…".
- **VR density:** the table is 1,350 px in an 814 px panel. Only World, Holds and Time seen show. The
  sideways scroll has no visible hint. Same as the Instances table (INDEX.md).
- **Pictures:** the thumbnails came in several seconds after the rows (blank squares first). They go
  through `/api/files/vrchat`. Not investigated further.

---

## Proposed page

**Question, unchanged:** *Which of our worlds actually get used?*

**Answer, from complete sources only:** how many instances were opened in each world, how long they were
open, and the most people at once, against the world's capacity.

### Desktop

```
Worlds                                          [7 days|30 days|90 days|All time]
Recording since Aug 13 · Details                                 Aug 29 – Sep 27
┌ Worlds ─────────┬ Instances ────────┬ Time open ───────────┐
│ 9               │ 16                │ 32 h                 │
└─────────────────┴───────────────────┴──────────────────────┘
┌ Worlds ──────────────────────────────────────────────────────────────────────────┐
│ World                    Instances  Time open            Most at once  People seen  Last used │
│ [img] Just B Club 4.0        2      ██████████ 10 h 50   53 of 80        572         Sep 26    │
│       by Blue-kun                                                                             │
│ [img] Cyber Bar              1      ██████ 6 h 16        27 of 80         91         Sep 18    │
│ [img] Popcorn Palace         3      █████ 5 h 18          8 of 80          —         Sep 25    │
│ [img] Karaoke in Sync!       1      ████ 4 h 8           12 of 80          —         Sep 25    │
│ [img] 古民家ドッグラン          1      ██ 2 h 20             2 of 16          1         Sep 19    │
│ [img] Sunset Bar - DVC       3      ██ 2 h 17            46 of 60        115         Sep 26    │
│ …  (the whole row opens the world)                                                  │
└──────────────────────────────────────────────────────────────────────────────────┘
```

- **Instances:** `vrchat_instance` rows opened in the range (same rule as the Instances list, so the two
  pages agree, and the same count as the world's popup).
- **Time open:** time with at least one instance of that world open, overlaps counted once. The bar in
  the cell is the page's only chart.
- **Most at once:** the highest `peak_user_count` of those instances, "of" the world's capacity. Capacity
  from VRChat's page, never a fixed number (§3.1).
- **People seen:** presence, the one column that needs a companion. "—" where no companion was there,
  because 0 would be a measurement and it isn't one.
- **Sort:** time open, then instances.
- **Tiles:** Worlds, Instances, Time open. Each is the sum (or count) of the column below it, so they
  cannot disagree with the table.

### Phone (390 px) and VR

Two-line rows, no sideways scroll:

```
[img] Just B Club 4.0
      2 instances · 10 h 50 open · 53 of 80
```

The tiles are three, so they fit one row.

### Cut

Presence reports tile; the thin-reports note; Visitors tile (it double-counted); Arrivals seen; Holds as
its own column; the world id under each name; the per-day chart; "Worlds, by time people were seen in
them" (the panel is just "Worlds").

---

## Findings (ranked)

1. **Ranked by presence.** Rank by time open and show instances, time open and most at once from
   `vrchat_instance`.
2. **"Visitors 795" double-counts.** Delete the tile.
3. **"Instances opened" disagrees with the popup and the Instances page.** Count `vrchat_instance` rows,
   as the popup does. The Instances page's "Opened 37" tile still counts audit entries with no world.
   That one belongs to the Instances rework.
4. **"Time seen" is person-time shown as a duration.** Replace it with time open.
5. **"Presence reports" is a headline tile.** Delete it and its floating note.
6. **Jargon columns.** Cut "Arrivals seen". Fold "Holds" into "53 of 80".
7. **World id under every name.** Drop it. It stays in the popup and the hover title.
8. **Rows don't open.** Whole row opens the world popup, with the Instances table's hover.
9. **The per-day chart.** Cut it. Bars in the Time open column.
10. **Phone and VR.** Two-line rows below `sm`.
11. **Slow thumbnails.** Not investigated. Worth a look at `/api/files/vrchat` caching.

---

## What to leave alone

- The range picker and the "Recording since … · Details" line.
- The world popup. It is the right home for a world's per-day charts and its instance list.
- The picture and the "by author" line in each row.
- "Nothing recorded answers —, never 0" for the presence column.
