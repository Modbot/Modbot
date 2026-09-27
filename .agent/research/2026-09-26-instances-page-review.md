# Instances page — design review

**Date:** 2026-09-26
**Page:** Analytics → Instances (`src/Modbot.Web/src/pages/analytics/Instances.tsx`)
**Looked at:** the demo captures in the session scratchpad (`instances-shots/`, named below by file),
the written description and raw API output of the user's own install (`SPARSE-NOTES.md`, one Friday
evening, UTC−7), the specs named in the brief, and the code. Nothing was run, built or changed.

Day boundaries stay in UTC (decided today). Nothing below asks for per-viewer days.

---

## Verdict

The page's question is already written down: **"When is the community actually active?"**
(foundation §10.1). That is a good question, and the page does not answer it until the third
screen. The answer is the hour-of-week grid. It sits under eight tiles, a list of what is open now,
a 25-row table and a chart on its own range (two scrolls on a desktop, three on a phone). It is also
drawn from the weaker of the two sources: presence reports, which exist only while a moderator's
companion is in the instance. Around it, the page answers four other questions: what is open now
(Live's question), what ran recently (a history question), how full it ever got, and how long
instances last. It answers them with numbers that contradict each other on screen.

The three worst problems, in order:

1. **The page contradicts itself on real data.** "Closed" and "Typical time open" count only
   instances a moderator closed by hand in VRChat. The table counts every instance that ended. On
   the user's install that gives "Closed: 0" and "Typical time open: —" above four closed rows,
   each with a length. The demo hides it by writing a close record for every instance, which VRChat
   never does.
2. **Its answer comes third and rests on the weaker source.** The grid should be first and should be
   built from VRChat's own head counts, which cover every instance whether or not anybody from the
   team is in it.
3. **Eight tiles and six daily charts repeat one fact in different units.** Three tiles say "6".
   Two tiles use the word "Busiest", one for a peak and one for an average. Six charts each draw
   the same one evening as a single dashed spike. On a sparse month the page looks like 14 broken
   instruments. It is one evening told 14 times.

---

## Confirmed / not confirmed

### 1. "Closed: 0" above four closed rows; "Typical time open: —" above four lengths — **confirmed, a real bug (a data-model leak)**

- "Closed" is the sum of the daily total `instances.closed` (`Instances.tsx:52`,
  `InstancesAnalyticsQuery.cs:56-57,75`). That total counts `vrchat.group.instance.close` facts
  from the audit log (`DailyTotalMetrics.cs:246`).
- VRChat writes that audit-log entry only when a moderator closes the instance. The query's own
  comment says so, and says 40% of instances in the live sample never got one
  (`InstancesAnalyticsQuery.cs:21-27`).
- All four instances on the user's install ended when they dropped off the group's live list.
  Modbot records that itself: `GroupInstanceSync.cs:219` → `PlaceStore.Close(instance, now, "list")`
  (`PlaceStore.cs:143-154`). This sets `vrchat_instance.closed_at` and `closed_by = "list"` and
  writes **no fact**. The API shows `"closedBy": "list"` on every row and `"closed": []`.
- "Typical time open" has the same cause. It is the median over `lives` that have a close **fact**
  (`InstancesAnalyticsQuery.cs:61-66`, SQL at `175-186`, `closed` at `207`), so it is null. So is
  `typicalMinutesOpenPerDay`, and the "Typical time open, per day" panel reads "Nothing recorded".
- The table uses the other definition: "Open for" and "closed Sep 25, …" come from
  `vrchat_instance.closed_at` (`InstanceRows.cs:77-78`, `InstanceTable.tsx:84-93`).
- **Why the demo does not show it:** `DemoHistory.cs:296-309` writes a `GroupInstanceClosed` audit
  fact for every closed demo instance. Real VRChat does not. The demo needs fixing too, or it will
  keep hiding this whole class of bug.

What should happen: "ended" is read from `vrchat_instance` (`closed_at`, whether it was decided by
`list` or by `time`) for the tile, the typical length, the per-day median and "most open at once".
The "Closed" tile is deleted. Every instance ends, so "how many closed" is "how many opened" a few
hours later. "Closed by a moderator" is a fact about the team and belongs on My Team if anywhere.

### 2. "Most people at once: 6 · 8:15 PM" vs "Busiest hour: 1 · 5:00 PM" — **confirmed, but the cause is not the one in the brief**

These two do **not** come from different sources. Both are head counts (`Instances.tsx:236-241`,
`267-281`, both from `InstancePeaksQuery`). The contradiction is about **units**:

- "Busiest hour" is chosen by people-minutes and shown as people-minutes ÷ 60, rounded:
  81.8 ÷ 60 = 1.36 → **1** (`Instances.tsx:272`). The API also sends that hour's own peak,
  `busiestHour.mostPeopleAtOnce: 4`, and the page drops it.
- "Busiest day" is also chosen by people-minutes, but it shows that day's **peak** (6)
  (`Instances.tsx:219-221,263`).

So the one word "Busiest" means a peak on one tile and an average on the tile next to it. The
average is only explained in a hover `title` ("on average"), which a phone and a VR pointer never
show. A design choice, and a wrong one.

The real two-source clash on this page is somewhere else. "Most people at once, per day" is head
counts (`Instances.tsx:142-150`). "Most people seen in one instance, per day" is presence reports
(`181-193`). The heatmap's "People arriving" layer is presence too (`AnalyticsSql.cs:39-43`). And
"Most open at once" (a fact-log sweep, `Instances.tsx:57`) is fetched next to
`peaks.mostInstancesAtOnce` (head counts, with its moment), which the page receives and never
shows (`lib/api.ts:1628`).

**Can "keep them apart and label them" work with labels alone?** Yes, if the label is allowed to
name the source: "Most people at once" versus "Most people seen by companions". The present
wording, "seen", is one italic word short of that, and nobody reads it as a source. The rule as
written forbids nothing here, because a source name in a label is naming, not explaining. But it is
being read as if it forbade it, so it should say so (see *Rules I would change*). The better fix is
not to need the label at all: this page shows only head counts (finding 4).

### 3. "Most people at once: 6" and "Fullest instance: 6, Murder 4" are one fact — **confirmed, a design choice to undo**

With one instance open at a time, the total across instances and the fullest single instance are
the same reading. The sparse install has "Most open at once: 1", like most small groups on most
evenings. "Busiest day" then repeats the same 6 a third time. Merge them into one tile: the
number, the moment, and the instance when it was all in one (see *Proposed page*).

### 4. "Busiest day: Sep 26" while every peak was on Sep 25 — **confirmed, a labelling gap**

`longDay` prints the UTC day (`format.ts:24-31`). `dateTime` prints the viewer's own clock
(`format.ts:44-52`). Both follow console-look §17.3 exactly, so the rule itself puts two clocks on
one strip. The heatmap says "(UTC−7)" (`Instances.tsx:86`). The range label, the day tiles and the
daily charts never say "UTC". Keep UTC days, and say so once where days are chosen: the range label
reads `Aug 28 – Sep 26 UTC`, and a day tooltip reads `Sep 26 (UTC)`. Better still, stop putting
days in tiles. An evening is a run of instants and is shown in the viewer's clock
("Fri Sep 25, 5:21–8:22 PM"), so the question never comes up.

### 5. "People" is "—" on every row — **confirmed, a design choice that leaks from Live**

The column is "people **now**" (`InstanceTable.tsx:38,82`: `r.closedAt ? '—' : r.peopleNow`). It
comes from a table shared with Live-like lists, where it means something. In a history it is empty
by definition. The API still sends `peopleNow: 1` for closed rows, the last head count before
closing (`InstanceRows.cs:55`), so the dash hides a stale number rather than a missing one. On a
phone it is the one number column still on screen (`instances-30-phone-dark-part2.png`,
`-part3.png`): World, Instance, People, and then the table runs off the edge. So a phone reader sees
a column of dashes and nothing else. Drop the column from any list of instances that have ended.

### 6. "Started" holds two times, beside "Open for" — **confirmed, a design choice**

`InstanceTable.tsx:85-93`: the open time, then "closed …" in small type under a heading that says
"Started", then the length in the column before. That is three cells for two facts. Use one
**When** column, `Sep 25, 6:53–8:22 PM`, with the length after it in mono (`1 h 29 min`). Keep
"open now" and "went quiet" as the end when there is no close time.

### 7. "People in instances" on its own range — **confirmed; this is the data model showing through**

The spec's reason (§7) is that the page's ranges are whole UTC days of daily totals, while this
chart is 30-second readings. That is true of the storage, not of the reader. The reader picked
"30 days" and got a week (`InstanceActivityChart.tsx:38`). At "7 days" they get a different week:
the page's Sep 20–26 in UTC days against the chart's rolling now−7 days, whose axis starts Sep 19
(`instances-7-desktop-dark-part2.png`).

One control can honestly drive both:

- The server already cuts the readings down to about 500 points for any span (`ReadingRange`).
  Cutting 30 UTC days is no harder than cutting 7 rolling ones.
- The chart already marks days it has no counts for (`instanceActivitySeries.ts:205-211`).

The only thing a single control loses is "Day", and "Day" is really "last evening", which is better
as a panel you reach by clicking an evening (see *Proposed page*) than as a fourth range word.
The own range is a copy of the member count endpoint's range words (peaks spec §2.2 "mirrors…
exactly"). It is not a reader's need.

There is a worse problem the capture exposed. **This chart never shows the page's own peak.** The
cut keeps the **last** reading in each step (`InstanceActivityQuery.cs:84-88`). On the user's
install the week series reaches 4 people. The tile above says 6 at 8:15 PM, and the chart has no
point between 02:40Z (1 person) and 03:22Z (0) (API output in `SPARSE-NOTES.md`). The steps also
start at `now − span` (`InstanceActivityQuery.cs:41-44`), so they move with the clock, and the same
data draws a different height on each load. This is probably why the user saw a 0–2 people scale
while the capture's API output peaks at 4. That last part is not confirmed: I have one API response
and one description, not two loads side by side.

### 8. Two scales, half-person ticks, both lines in the last 5% — **confirmed**

- People scale: `domain [0,'auto']` without `allowDecimals={false}` (`InstanceActivityChart.tsx:113-120`),
  so a small peak gets 0.5 and 1.5 ticks.
- Instances scale: `allowDecimals={false}` (`121-131`). Recharts then spreads five whole-number
  ticks, so a series that peaks at 1 is drawn on 0–4 and reaches a quarter of the height.
- One evening in a week is about 3 hours of 168, so it is squeezed into the right edge. The demo
  shows the same squeeze with more data (`instances-30-desktop-dark-part2.png`,
  `instances-tooltip-peopleinstances-dark.png`).

**Is a step chart with two scales ever right here?** No. The comment defends it as "one thing at two
magnitudes" (`InstanceActivityChart.tsx:27-31`). But "30 people in one instance vs 30 across six"
is a question about how the people are split, and two lines on two scales cannot show a split. What
can is **one scale of people, stacked by instance**: one band per instance, stacked as a staircase.
The top edge is people at once. The number of bands is instances at once. Each band is the world's
name on hover and opens the instance popup on click. That is one scale, whole people, and it
answers both questions. It suits one evening. For a month, use one bar per evening (finding 5).

### 9. Six per-day charts, each one spike — **confirmed**

On the sparse install every chart is 30 days of thin baseline marks, a hatched first week (on some
of them) and one dashed bar at Sep 26.

- **Which are the same chart?** "Most people at once, per day", "Busy time per day" and "Most people
  seen in one instance, per day" all answer "how busy was that day", from two sources and in two
  units. "Opened and closed per day" and "Most open at once, per day" both answer "how many
  instances". For a group that runs one at a time, the second one is always 1. "Typical time open,
  per day" is a median of one or two instances a day, which is just their length. So there are two
  questions (how busy, how many), drawn six times.
- **Dashed vs solid:** dashed means "this UTC day is not over yet" (`DailyBars.tsx:80-94`, `today`
  from `MissingDays.cs:34`). On the user's install the evening was over at 8:22 PM on Friday. It is
  dashed because Sep 26 UTC began at 5 PM their time. No reader would work that out. At 30 days the
  2px zero marks (`DailyBars.tsx:65-77`) also run together into what looks like a dashed baseline.
  Other pages use dashes for carried values. So dashes mean three things.
- **The hatch on the first week** is the days before the first audit-log fact Modbot ever wrote
  (Sep 3; `MissingDays.cs:124-125`): Modbot was not reading the group yet. The zeros after it are
  days Modbot was reading and nothing opened. That distinction is right, but the reader cannot tell
  "not installed yet" from "no data". It is also applied unevenly. The head-count and presence
  charts draw those same six days as **zeros**, because `HeadCountsAsync` only marks a day missing
  when some instance is known to have been open on it (`MissingDays.cs:228-258`). Before Modbot
  existed, none is known. So on one screen, the same week is "no data" in two charts and "0 people"
  in four. (INDEX.md notes the same split in the demo.)
- **Also broken, both visible in the demo:** "Typical time open, per day" fills days with no
  instance with **0** (`DailyLine` defaults to `mode='zero'`, `DailyLine.tsx:32`;
  `coverage.ts:150-151`). So the line dives to "under a minute" on quiet days
  (`instances-30-desktop-dark-part4.png`, `instances-30-phone-dark-part5.png`). A day with no
  instances has no typical length. Its scale ticks at 65-minute steps ("1 h 5 min … 4 h 20 min")
  with "under a minute" as its zero. And both line charts use a curved line (`DailyLine.tsx:98`)
  for per-day values, which `DailyBars.tsx:19-20` itself says implies values between days that were
  never measured.
- **With one chart:** one bar per **evening** across the range. Height is the most people at once,
  from head counts. The label is the evening's local start. Tooltip: instances, length,
  people-hours. Click opens that evening. What is lost: per-UTC-day trend lines for time open and
  opened/closed (noise at this size, and "closed" was wrong anyway), and the presence peak (moves to
  the instance popup and Worlds, where "who" is the question). Nothing a roster depends on is lost.

### 10. "Only 17 presence reports in this range" as a bare line — **confirmed, wrong place and wrong part**

It is a `PageMessage` (`Instances.tsx:79-83`). That part is for a whole page that is loading, empty
or failed (`shared.tsx:163-173`, console-look §10.3). It floats between the head-count chart and
the heatmap. It sits directly under a chart that uses **no** presence reports, so it reads as that
chart's footnote. The two panels it does concern are the heatmap's arrivals layer and the last chart
on the page, five panels further down. Peaks spec §2.5 said "marks the presence-derived **panels**
thin". Console-look §10.4 says a data warning tints **the panel's own strip**. It is also not "half
the page's numbers": the peaks are head counts. Either put a warn strip on each panel it concerns,
or (better) take presence off this page so there is nothing to warn about. The head-count coverage
line (`Instances.tsx:225-233`) has the same problem and needs the same fix.

### 11. The heatmap is below the fold — **confirmed**

It is the fifth block (`Instances.tsx:85-113`). In the demo it starts at the bottom of the second
screen and is whole only on the third (`instances-30-desktop-dark-part2.png`/`-part3.png`). On a
phone it is on the fourth screen, after four full-width "Open right now" cards and 25 table rows
(`instances-30-phone-dark-part1…4.png`). It is also drawn from presence (`AnalyticsSql.cs:39-43`).
That source includes "already there" roster reports, so a companion started at 8 PM lights 8 PM as
"arriving". On the user's install the lit cells were Friday evening. `SPARSE-NOTES.md` is unsure
whether they were 5–6 PM or 7–8 PM, while head counts put people in instances from 5:21 PM. I
could not settle which from what was captured.

### 12. Every panel the same weight — **confirmed**

Two `StatStrip`s of four (`Instances.tsx:50-58`, `235-282`). On a phone each wraps as three plus one
orphan (`instances-30-phone-dark-part1.png`). Then a list, a table, a chart, a grid and six charts
in pairs, all in the same `Panel`. Nothing on the page is bigger than anything else. What the head
moderator rostering next Friday needs first is the grid, in their clock, from complete counts.
It is fifth.

---

## Proposed page

**Question, unchanged:** *When is the community actually active?*

**Top panel:** the hour-of-week grid, built from head counts. Each cell is the average number of
people in the group's instances during that hour, over the weeks in the range that Modbot was
counting. Toggle: People / Instances open. Tapping a cell lists the evenings that touched that hour.

**Then:** four tiles, the evenings chart, and the latest evening, open by default.

**Moves out:**

- "Open right now" → Live. The page keeps a link on its top strip, e.g. `4 open now ▸`.
- "Recent instances" → an instance history page, reached from Live and from each evening. It gets
  paging, a world filter and search. Today the table stops at 25 rows with no count, so the 30-day
  view silently ends at Sep 13 (`InstancesAnalyticsQuery.cs:128`,
  `instances-30-desktop-dark-part2.png`).
- Who was there → the instance popup, which already has People, "Seen longest" and "People over
  time".
- Presence peaks → Worlds, where presence is already the source.

**Cut:** Closed; Busiest day; Busiest hour; Fullest instance (merged); the two-scale chart and its
own range; all six per-day charts; both floating notes.

### Dense month, desktop 1440

Figures are the demo's where it has them; the evening count and the grid's hover line are made up for the sketch.

```
Instances                                              [7 days|30 days|90 days|All time]
                                            4 open now ▸              Aug 28 – Sep 26 UTC
┌ When people are in our instances (UTC−7) ───────────────── [People | Instances open] ┐
│        0     3     6     9     12    15    18    21                                  │
│ Mon    ·  ·  ·  ·  ·  ·  ·  ·  ▒  ▓  ▒  ▓  ▒  ·  ·  ·                               │
│ Tue    ·  ·  ·  ·  ·  ·  ·  ·  ·  ▒  ▓  ▓  ▒  ·  ·  ·                               │
│ ...                                                                                  │
│ Fri    ·  ·  ·  ·  ·  ·  ·  ·  ▓  ▒  █  ▓  █  ▓  ▒  ·                               │
│ Sat    ·  ·  ·  ·  ·  ·  ·  ·  ▒  █  █  █  ▓  ▓  ▒  ·                               │
│ Fri 3 PM · 14 people on average · 4 Fridays                  ← hover/tap line        │
└──────────────────────────────────────────────────────────────────────────────────────┘
┌ Evenings ──────┬ Instances ────┬ Most at once ──────────────┬ Typical length ────────┐
│ 21             │ 47            │ 51                         │ 2 h                    │
│                │ 4 at once     │ Sat Sep 26, 3:41 PM ▸      │                        │
└────────────────┴───────────────┴────────────────────────────┴────────────────────────┘
┌ Evenings ────────────────────────────────────────────────────────────────────────────┐
│  one bar per evening, height = most at once; hatched only before Modbot's first day  │
│  ▂ ▃ ▂   ▅ ▃ ▂ ▆   ▂ ▃ ▂ ▇   ▂ ▃   █              ← click a bar = that evening below │
│ Aug 28                    Sep 9               Sep 21                  Sep 26         │
└──────────────────────────────────────────────────────────────────────────────────────┘
┌ Sat Sep 26, 1:31 PM – now ───────────────────────── [◂ earlier] [later ▸] [Audit log ▸]┐
│  stacked staircase, one band per instance, one scale (people)                        │
│  ▁▁▂▂▃▃▅▅▆▆███▇▇                                                                     │
│ World          When                      Length       Most at once                   │
│ Tide Pool      1:41 PM – open now        2 h 14 min   17 of 32                       │
│ Quiet Library  2:26 PM – open now        1 h 29 min   10 of 24                       │
│ ...  (whole row opens the instance popup)                                            │
└──────────────────────────────────────────────────────────────────────────────────────┘
  Data covered (unchanged footer)
```

Grid, tiles and evenings chart fit in the first screen at 1440×900 dense. The latest evening
starts at the bottom of it.

"Evening" means a run of time in which at least one group instance was open, from
`vrchat_instance.opened_at` to `closed_at` (or `last_seen_at`), with gaps shorter than about an
hour joined up. It is a span of instants, not a day, so it needs no day boundary at all. It is
labelled by its start in the viewer's clock, and every viewer gets the same rows from the server,
so the UTC-day decision and the caching reason behind it both stand. The daily totals keep their
UTC days wherever they are still used.

### Sparse month (the user's install)

```
Instances                                               [7 days|30 days|90 days|All time]
                                                                     Sep 3 – Sep 26 UTC
┌ When people are in our instances (UTC−7) ───────────────── [People | Instances open] ┐
│ Mon–Thu, Sat, Sun   (empty rows)                                                     │
│ Fri    ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ▓  █  ▓  ▒  ·  ·  ·       │
└──────────────────────────────────────────────────────────────────────────────────────┘
┌ Evenings ──────┬ Instances ────┬ Most at once ──────────────┬ Typical length ────────┐
│ 1              │ 4             │ 6                          │ 37 min                 │
│                │ 1 at once     │ Fri Sep 25, 8:15 PM ▸      │                        │
│                │               │ Murder 4                   │                        │
└────────────────┴───────────────┴────────────────────────────┴────────────────────────┘
┌ Fri Sep 25, 5:21 – 8:22 PM ──────────────────────────────────────────── [Audit log ▸]┐
│  stacked staircase 5 PM → 8:30 PM, four bands one after another, top edge reaches 6  │
│ World           When                  Length        Most at once                     │
│ Murder 4        6:53 – 8:22 PM        1 h 29 min    6                                │
│ Murder 4        6:34 – 6:52 PM        18 min        1                                │
│ Drinking Night  6:00 – 6:33 PM        34 min        2                                │
│ Murder 4        5:21 – 6:00 PM        39 min        4                                │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

The evenings chart is left out when there is one evening, because one bar is a tile. The range
label starts at Modbot's first day (Sep 3) instead of hatching Aug 28 – Sep 2. There is no presence
note, because nothing on the page rests on presence. Every number on the screen agrees with every
other.

### 390px

```
Instances
[7d|30d|90d|All]
Aug 28 – Sep 26 UTC        4 open now ▸
┌ When people are in… (UTC−7) ┐
│ [People | Instances open]   │
│     0    6    12   18       │
│ Mon ▫▫▫▫▫▫▫▫▒▓▒▓▒▫▫▫▫▫▫▫    │
│ ... 7 rows, 24 thin cells   │
│ Fri 3 PM · 14 on average    │
└─────────────────────────────┘
┌ Evenings ┬ Instances ┬ Most at once ┐   one row of three; "Typical length" in the evening
│ 21       │ 47        │ 51 ▸          │
└──────────┴───────────┴───────────────┘
┌ Evenings (bars, tap one) ┐
┌ Sat Sep 26, 1:31 PM – now ◂ ▸ ┐
│ staircase, 160px            │
│ Tide Pool                   │  two-line list rows, not a seven-column table:
│ 1:41 PM – now · 2 h 14 · 17 │  name on top, when · length · most at once under it
└─────────────────────────────┘
```

**What survives a phone today:**

- The grid survives. It is readable at 390 (`instances-30-phone-dark-part4.png`).
- The stat strips survive badly: two orphans.
- The two-scale chart barely survives.
- The seven-column table does not: the useful columns are off-screen, and what is left on screen is
  the dash column.
- The "Open right now" cards cost two whole screens.

In VR the table needs a sideways scroll with no visible hint (`instances-30-desktop-vr-part1/2.png`,
INDEX.md).

---

## Readers, today vs proposed

| Reader | Needs | Today | Proposed |
|---|---|---|---|
| Head moderator rostering Friday | Which hours, in their clock, how many people, how many instances, from complete counts | The grid, from presence, two scrolls down (three on a phone) | The grid from head counts, first thing on the page, no scroll |
| Owner weighing a second instance | How often an instance got near its capacity, how often more than one ran | Never answered. "Fullest instance: 17" has no capacity beside it, and only the live cards show "17/32" | "Most at once" shows "17 of 32" (the world's capacity from the API, never a fixed number). The evenings chart shows instances at once per evening. First screen |
| Moderator: "was last night unusual?" | Last evening beside a typical one | Read the table, then find the UTC-day bars on screen 3–4, where a US evening is split over two bars | The latest evening is open by default and sits beside its bars in the evenings chart. First to second screen |

---

## Findings (ranked)

1. **"Closed" and "Typical time open" read a record VRChat rarely writes.** Read ends from
   `vrchat_instance.closed_at` for the typical length, its per-day line, and "most open at once".
   Delete the Closed tile. Change `DemoHistory.cs:296-309` so that about 40% of demo instances end
   without an audit close, as the live sample does (`InstancesAnalyticsQuery.cs:23`).
2. **The page's answer is fifth and from the weaker source.** Put the hour-of-week grid first.
   Rebuild it from `instance_head_count`: average people at once per hour of the week, and average
   instances open, over the weeks Modbot was counting. Drop the "People arriving" layer, which
   counts roster reports as arrivals.
3. **Eight tiles, three saying "6", and "Busiest" meaning two things.** Keep four tiles: Evenings,
   Instances (with most open at once as its note, from head counts, `peaks.mostInstancesAtOnce`),
   Most at once (moment, and the instance when it was one, clickable), Typical length. Delete
   Busiest day, Busiest hour, Closed and Fullest instance. A tile shows the measure it was chosen by.
4. **Two sources for "how many" on one page.** Take presence off this page: "Most people seen in one
   instance, per day", the arrivals layer and the 17-reports line. "Who was there" is the instance
   popup's job (People, Seen longest) and the Worlds page's. The contradiction then goes away with
   no label needed.
5. **Six per-day charts are two questions drawn six times.** Replace them with one "Evenings" bar
   chart: one bar per evening, height = most at once, click opens the evening. It needs no day
   boundary, so the UTC edge stops cutting evenings in half. It also fixes the "Typical time open"
   zero-fill and the curved per-day lines by deleting them.
6. **The activity chart drops the peak it sits under.** Keep each step's highest reading, not its
   last (`InstanceActivityQuery.cs:84-88`: order by people desc within the step). Start steps on
   whole hours, not at `now − span` (`41-44`), so a reload with no new data draws the same chart.
7. **Two scales for one crowd.** Replace the two lines with a staircase stacked by instance on one
   people scale. Each band is an instance, and clicking it opens that instance. Until then, add
   `allowDecimals={false}` to the people scale (`InstanceActivityChart.tsx:113-120`).
8. **The chart's own range is the storage showing through.** Drive it from the page range. Replace
   "Day" with the latest-evening panel and its ◂ ▸ controls. Delete the Day/Week/Month/All toggle
   (`InstanceActivityChart.tsx:38,79-84`).
9. **Nothing opens anything except two words in each table row.** Make the Most-at-once tile open
   the instance popup. Make an evening bar select the evening. Make a band open its instance. Make a
   grid cell list its evenings. Put "Audit log ▸" on the evening, using the audit log's existing
   instance and date filters (`lib/pageFilters.ts:18-28`). Make every instance row open the popup on
   a click anywhere on it, with the row hover of console-look §7.8, as the Live cards already do.
10. **Dashes mean three things.** "Not over yet" should belong to an evening with an instance open
    now, not to a UTC day that happens to be today (`DailyBars.tsx:80-94`). With evenings in place of
    days this is automatic. For any day chart that stays, do not draw the 2px zero mark when there
    are more than about 14 bars. Let the grid line be the zero.
11. **The same week is "no data" on two charts and "0 people" on four.** Treat days before Modbot's
    first head count ever as missing on every head-count chart (`MissingDays.cs:228-258`). Better:
    start the range label and the charts at Modbot's first day when the chosen range reaches back
    before it ("Sep 3 – Sep 26 UTC"), so there is no week of hatching to explain.
12. **Two clocks on one strip, only one of them named.** Add "UTC" to the range label and to day
    tooltips (`shared.tsx:41-45`, `rechartsTooltip`). Show instants and evenings, never bare days,
    in tiles.
13. **"Recent instances" is a history list with no paging, on a page about patterns.** Move it to an
    instance history page linked from Live and from each evening, with paging, world filter and
    search. In every instance list, drop "People (now)" for ended rows and merge Started/closed into
    one "When" column (`InstanceTable.tsx:38-41,82,85-93`). On a phone, list rows are two lines, not
    a seven-column table.
14. **"Open right now" duplicates Live and costs two phone screens.** Replace it with `4 open now ▸`
    on the page's top strip, linking to Live.
15. **Data warnings float as page messages.** Where a warning stays (head-count coverage), put it on
    the panel's own strip as console-look §10.4 says (warn tint and filled square), not as a
    `PageMessage` between panels (`Instances.tsx:79-83,225-233`).

---

## Rules I would change

**Peaks spec §7, "Folding the activity chart into the page's window"**: replace with:

> **The page's range drives every panel on it.** A panel drawn from readings shows the readings in
> that range, cut down to what the chart can draw by keeping each step's highest reading, and marks
> the days it has nothing for. One evening in detail is its own panel, reached by clicking that
> evening, not a second range control. A chart that cannot reach the peak printed above it is
> wrong, however it was cut down.

**Peaks spec §7, "Making the presence peak and the head-count peak one number"**: keep it, and
add:

> The Instances page shows head counts only. Presence answers *who* was there, and that belongs in
> the instance popup and on Worlds. Two sources that must never be blended should not share a
> screen where the reader has to tell them apart.

**Peaks spec §2.1, busiest day and hour**: add:

> A tile shows the measure it was chosen by. "Busiest" picked by people-time and shown as a peak,
> or as a rounded average, is two measures under one word. Neither tile is kept on Instances. The
> hour-of-week grid answers "when", and the evening answers "how much".

**CLAUDE.md, "UI text — controls, not explanations"**: after "That is all the text a screen
needs.", add:

> **A label may carry the few words that make its number true.** Its clock ("UTC"), its source
> ("counted by VRChat", "seen by companions") or its unit, when the same screen shows a number that
> would otherwise seem to contradict it. That is part of the name, not an explanation. A sentence
> about *why* is still not allowed, and if a screen needs more than a word or two to keep two
> numbers apart, show one of them somewhere else.

**Console-look §17.3**: after the "A day of a chart or a range" row, add:

> A page that shows UTC days says "UTC" once, in its range label, and in any day tooltip.

---

## What to leave alone

- **Head counts as the source for how many**, and coverage measured against open time, never
  against the calendar (peaks spec §3.1). A quiet group should never be marked unreliable for
  being quiet.
- **Peaks carry their moment**, the tie rule (§4.3), and "nothing recorded answers null, never 0"
  (§4.4).
- **A staircase, not a curve**, for readings.
- **UTC days on the server** and the reason for them: the same query gives the same rows to every
  moderator. The grid is shifted into the viewer's clock in the browser (`Instances.tsx:294-304`).
  Evenings keep this rule, because they are instants.
- **Missing is not zero, as a principle** (`MissingDays.cs:15-20`). Only how it is applied needs
  fixing (finding 11).
- **The instance popup**: People over time, Seen longest, the activity feed. It is the right home
  for "who".
- **The "Data covered" footer.**
- **Number-first tiles, "1 h 29 min", no year on this year's dates, no world ids under names.**
  All good. Keep them in the new tiles and rows.
- **Whole-card click on the Live cards.** It is the pattern the instance rows should copy.
