# Analytics — peaks, activity over time, and saying what a number rests on

**Date:** 2026-09-19
**Touches:** foundation §5 (analytics and data engine), §10.1 (Analytics is five sections)
**Asked for:** *"Analytics: show peak counts, graphs over time and instance activity and such"*

---

## 1. What already existed

Analytics was already five pages with one question each (foundation §10.1), and most of the
"graphs over time" half of the request was built before this change:

| Already there | Page | Source |
|---|---|---|
| Member count, reading by reading, own range, thinned to ~500 points | My Group | `group_member_count` |
| Joins, leaves, net, invites and requests per day | My Group | daily totals |
| Discord member count, messages, voice, active people, busiest hours | My Server | daily totals |
| Actions per moderator, per kind, per day; coverage gaps | My Team | daily totals + presence facts |
| Time seen, visitors, instances opened per world; visitors per day | Worlds | presence facts + daily totals |
| Instances opened and closed per day | Instances | daily totals |
| Most instances open at once **per day**; hour-of-week heatmap | Instances | fact log |
| Most people in one instance **per day** | Instances | presence facts |
| Typical (median) time open, one number for the window | Instances | fact log |
| `coverage` footer: how far the daily totals and the fact log reach | every page | both |
| "Only *n* presence reports in this range" | Worlds | presence facts |

So the honest summary of the brief is: **the daily series were largely there; the peaks were not,
and the one source that could have answered them honestly was unread.**

### 1.1 What was missing

1. **No peak carried a time.** Every "most" on the page was a per-day series a reader had to eyeball
   the maximum of. "Forty-eight people" is a boast; "forty-eight people at 20:15 last Saturday" is
   something a team can roster against.
2. **Everything about population came from presence facts**, which exist only while a moderator's
   companion is in the instance. A busy night nobody with the client attended reads as nought.
3. **`instance_head_count` had never been read by analytics.** It has existed since instance tooling
   and is written every thirty seconds for every open group instance from VRChat's own `n_users` —
   no companion involved. The Live page and the Discord announcement card were its only readers.
4. **No graph of how busy instances are over time.** The page could say how many instances opened and
   how long they lasted, never how full they were.
5. **A single number that wanted to be a line**: the median time open for the whole window.

---

## 2. What was added

### 2.1 Instances page — peaks, each with its moment

Under `peaks` on `GET /api/analytics/instances`:

| Figure | What it is | Where it comes from |
|---|---|---|
| `mostPeopleAtOnce` | The most people in all the group's instances added together, at one moment | head counts |
| `mostInstancesAtOnce` | The most instances with a head count at one moment | head counts |
| `busiestDay` | The UTC day with the most people-minutes | head counts |
| `busiestHour` | The single clock hour with the most people-minutes, on a real date | head counts |
| `busiestInstance` | The fullest one instance ever got, with world, instance number and Modbot's id | head counts |
| `mostPeopleAtOncePerDay` | The daily peak, as a line | head counts |
| `peopleMinutesPerDay` | People-time per day, as a line | head counts |
| `coverage` | How much of the window Modbot had a count for (§3) | head counts + instance table |

**Busiest is people-minutes, not the peak.** A night that touched forty for five minutes is not a
busier night than one that held twenty for six hours, and the question behind "when are we busiest"
is a staffing question. The peak is reported beside it so the two can be told apart.

**Busiest hour is one real hour on one real date**, not a cyclic bucket. The cyclic answer — "Tuesdays
at 8pm" — already exists as the hour-of-week heatmap, and giving the same idea two shapes on one
page would invite the reader to compare numbers that are not comparable.

### 2.2 Instances page — activity over time

`GET /api/analytics/instances/activity?range=day|week|month|all` returns people in the group's
instances, moment by moment, thinned to at most 500 points. It mirrors
`/api/analytics/group/member-count` exactly, including the four range words, because it is the same
shape of thing: a chart of *readings*, not of days, whose useful window is hours rather than months.

The page draws it as a **staircase** (`stepAfter`), because a head count is stored only when it
changes and the value between two readings is the earlier one's, right up to the next. Drawing it as
a curve would invent a slope that nothing in the data supports.

### 2.3 Instances page — two more lines and one relabelled

- `mostPeopleAtOncePerDay` — the daily peak from head counts, complete whether or not anyone watched.
- `peopleMinutesPerDay` — people-time per day, drawn in hours.
- `typicalMinutesOpenPerDay` — the median time open, day by day. The single number stays as the stat
  at the top; the line answers the question anyone who reads that number twice actually has.
- The existing presence-derived panel is relabelled **"Most people *seen* in one instance, per day"**,
  because it now sits beside a complete figure and the difference has to be visible in the label.

### 2.4 My Group — two peaks

`peaks.members` and `peaks.online` on `GET /api/analytics/group`: the highest member count and the
highest online count inside the window, each with the reading that reached it, plus `coverage`
(§3.2). Read from `group_member_count` and not from the daily totals, which hold the last reading of
a day and would put every peak at midnight.

### 2.5 Presence reports counted on the Instances page

`presenceReports` joins the response, and the page marks the presence-derived panels thin below 200
reports — the same number and the same wording the Worlds page already uses. Two panels on the page
rest on presence and were previously unmarked.

---

## 3. Coverage — the part that decides whether any of this is any good

A peak drawn from two hours of counting in a week is real, is **not** the week's peak, and looks
exactly like one. Nothing about the number says which. So coverage is data, not a caption, and it
travels with the peaks.

### 3.1 Instance coverage

`InstanceCoverage` (`src/Modbot.Analytics/Activity/InstanceCoverage.cs`):

| Field | Meaning |
|---|---|
| `windowDays` | Days asked for |
| `daysCounted` | Days on which at least one instance was being counted |
| `instancesOpen` | The group's instances open at some point in the window |
| `instancesCounted` | How many of those ever had a head count |
| `minutesInstancesWereOpen` | Instance time inside the window, added across instances |
| `minutesCounted` | Of those minutes, the ones after each instance's first head count |
| `thin` | `minutesCounted < minutesInstancesWereOpen / 2` |

**The denominator is open time, never calendar time.** A ninety-day window in which the group ran
instances on ten evenings is not thin coverage; it is a quiet group. Measuring against the calendar
would mark every small community unreliable and would train moderators to ignore the mark — which is
the only failure mode worse than not having one.

**An instance counts from its first head count onwards**, because from that moment the sync had it
and kept it to within thirty seconds. Before that there is nothing; an instance Modbot never read
contributes open minutes and no counted minutes, which is exactly the finding.

**Thin marks, it never hides.** A real peak from a short window is worth seeing as long as the reader
knows that is what it is. The page prints one line — *"Head counts cover 19% of the time instances
were open"* — or, with nothing at all, *"No head counts in this range"*.

### 3.2 Member count coverage

`MemberCountCoverage`: `windowDays`, `daysWithReadings`, `readings`, and `thin` when fewer than half
the window's days carry a reading. The group-info sync reads every five minutes, so a window Modbot
was running through has every day; a window it was not — a younger deployment, or a presence
retention window that deleted the older readings — has gaps, and the highest number across a
gap-ridden window is the highest Modbot saw.

### 3.3 Two sources, kept apart and both labelled

| | Presence reports | Head counts |
|---|---|---|
| Written by | a moderator's companion | `InstanceHeadCountSync`, from VRChat |
| Covers | instances a companion was in | every open group instance |
| Says | **who** was there | **how many** are there |
| Thin when | few reports in the range | little of the open time was counted |

Neither replaces the other and the page never blends them. Every panel says which it is drawn from,
and the two similar-looking daily charts are named for the difference (`Most people at once` vs
`Most people **seen** in one instance`).

---

## 4. Where each number actually comes from

### 4.1 Rebuilding the staircase

`InstanceActivitySql.Running` is one set of CTEs, shared by the peaks query and the activity series,
that turns `instance_head_count` rows into a moment-by-moment total.

A head count row is written only when the number **moves** (`HeadCounts.Record`), so the stored
series is a staircase and the total is a sum of changes:

- each reading contributes its difference from that instance's previous reading;
- an instance's first reading contributes the whole count, and `+1` to the instance count;
- an instance's end — `COALESCE(closed_at, last_seen_at)`, never earlier than its last reading —
  contributes its last count back out, and `-1`.

**Seeding the window.** An instance already open when the window began is found from
`vrchat_instance` and asked for its last reading before the window, through the
`(instance_id, counted_at)` index. Those seeds are folded into **one** row at the window's first
moment rather than left one per instance: a running sum applies rows one at a time, and a reader
looking at the first of several rows at one instant would see a total that was never true. Ends may
still share an instant — the close sweep finishes several instances on one clock reading — but ends
only take the total down, and a momentary dip cannot invent a peak.

### 4.2 Folding it into days

People-minutes needs the staircase cut at time boundaries, or an instance that sat at twelve people
from 21:00 to 02:00 would put five hours of people-time on the wrong side of midnight. The cut is at
**hour** boundaries rather than day boundaries: it costs a handful more rows and it gives the busiest
hour for nothing.

Each day row then carries its own busiest hour, and `Peaks.BusiestHour` picks the window's busiest
out of the day rows — the largest of a set of per-day largests is the largest overall. That is what
keeps the answer to a few hundred rows instead of one row per hour in the range.

### 4.3 Ties, and why the rule is stated once

Every peak: **the biggest value wins, and among equals the earliest moment wins.** Two evenings that
both reached forty-eight is ordinary. Without a rule the page names whichever row the database
returned first and moves between two loads with no new data behind it.

The rule lives in `Peaks` (`src/Modbot.Analytics/Activity/Peaks.cs`) as four small functions over a
list of day rows — pure arithmetic, tested without a database, used by every peak.

### 4.4 Nothing recorded answers null

"Nothing was recorded" and "the peak was nought" are different statements and only the first is true
of an empty range. Every peak is nullable; a value of nought is never a peak (an instance counted all
evening that nobody attended is a real record of an empty evening, and *"0 people at 19:04"* says
nothing); an empty window returns `InstancePeaks.Empty(windowDays)` and answers 200, not 500.

---

## 5. Keeping the queries bounded

These read tables that grow forever. The existing analytics queries are all bounded by the window,
and these follow the same shape.

- **One new index**, `ix_instance_head_count_time` on `instance_head_count(counted_at)`. The table
  only had `(instance_id, counted_at)`, which answers the per-instance question the Live page asks
  and leaves *"every count between these two moments"* a full scan of a table that grows with every
  change in every open instance forever. Migration `20260919210842_AddInstanceHeadCountTimeIndex`.
- **The heavy read is one index range** over that index — readings inside the window and nothing
  before it.
- **The seed reaches back exactly `VRChatInstance.CountsAsNewAfter` (72 h)**, the same rule that
  decides an instance is finished, and uses `(group_id, opened_at desc)`. It can only miss an
  instance open for longer than that whose count never changed during the whole window; that one
  enters the series at its next reading instead of at the window's first moment.
- **Aggregation happens in SQL, choosing happens in C#.** The database folds every change into at
  most one row per UTC day; the peak is then picked from at most `MaxDays` (1830) rows. Sending the
  staircase to the application would be tens of thousands of rows for a busy month.
- **The activity series is thinned server-side** to 500 points, by the same bucket-and-keep-the-last
  rule the member count chart uses. Thinning in the browser would mean sending the whole staircase
  first, which is the cost thinning exists to avoid.
- **Coverage is one pass** over the window's instances with a keyed `MIN(counted_at)` per instance.
- **The group peaks are one aggregate** over `group_member_count`, a table capped at 288 rows a day
  by construction.

Nothing here calls VRChat. Every figure is read from tables Modbot already writes, so opening the
page costs no rate-limit budget (foundation §4.3.4).

---

## 6. Permissions

Every figure is behind `ViewAnalytics`, the flag the five pages already share. The new
`/api/analytics/instances/activity` route carries it like every other, and it is added to the
access test's list — which exists precisely because a route added later without the flag is the one
that leaks.

No new permission was invented. A peak is the same class of reading as the series it is the maximum
of, and splitting them would mean a role that can see the chart but not its highest point.

---

## 7. What was decided against

- **A "Peaks" page of its own.** §10.1 is five sections with one question each, and a sixth page of
  superlatives is the single metrics dashboard that section exists to prevent. Each peak was put on
  the page whose question it answers: members and online on My Group, everything about instances on
  Instances.
- **Folding the activity chart into the page's window.** The page's ranges are whole UTC days of
  daily totals; this is readings every thirty seconds. One control cannot honestly mean both, and
  the member count chart set the precedent.
- **A second charting library.** Recharts throughout, through the existing `components/charts`
  pieces. The activity chart re-uses `memberCountSeries.ts`'s axis helpers, which the machine usage
  card already shares.
- **Estimating what happened while nobody was counting.** Interpolating across a gap would produce a
  number that is wrong in a way nothing about it looks wrong — foundation §5.4's standing objection.
  A gap is reported as coverage and the peak stays the peak of what was seen.
- **Making the presence peak and the head-count peak one number.** They measure different things, one
  of them is complete, and a blended figure would be neither.
- **A per-day daily total for head counts.** Tempting, since daily totals outlive retention — but the
  peak of a day is not a sum and the daily totals engine counts facts. `instance_head_count` is not a
  fact table, is not covered by a retention window today, and a metric that cannot be rebuilt from
  facts would have to earn the §5.2.1 exception. Left for a later change if retention ever reaches
  these rows.
- **Using `vrchat_instance.peak_user_count` for the fullest instance.** It covers the instance's whole
  life, which may reach outside the window asked about, and it carries no time.

---

## 8. Files

| Path | What |
|---|---|
| `src/Modbot.Analytics/Activity/PeakCounts.cs` | `PeakCount`, `BusiestDay`, `BusiestHour`, `ActivityDay` |
| `src/Modbot.Analytics/Activity/Peaks.cs` | picking the highest and the busiest, and the tie rule |
| `src/Modbot.Analytics/Activity/InstanceCoverage.cs` | how much of the window was counted, and `thin` |
| `src/Modbot.Analytics/Activity/MemberCountCoverage.cs` | the same for member count readings |
| `src/Modbot.Analytics/Activity/ReadingRange.cs` | the four ranges and the step that keeps a chart drawable |
| `src/Modbot.Api/Features/Analytics/Instances/InstanceActivitySql.cs` | the shared staircase CTEs |
| `src/Modbot.Api/Features/Analytics/Instances/InstancePeaksQuery.cs` | day rows, busiest instance, coverage |
| `src/Modbot.Api/Features/Analytics/Instances/InstanceActivityQuery.cs` | the thinned series endpoint |
| `src/Modbot.Web/src/pages/analytics/InstanceActivityChart.tsx` | the staircase chart |
| `src/Modbot.Web/src/pages/analytics/instanceActivitySeries.ts` | its rows, carried to the range's end |
| `src/Modbot.Core/Data/Migrations/20260919210842_AddInstanceHeadCountTimeIndex.cs` | the one index |
| `docs/content/docs/analytics.mdx` | head counts, coverage, and every new figure |
