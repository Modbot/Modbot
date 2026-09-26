# Analytics: a design review

- **Date:** 2026-09-26
- **Pages:** Team, VRChat, Worlds, Instances, Discord (`src/Modbot.Web/src/pages/analytics/`)
- **Read:** CLAUDE.md; foundation §5.4, §5.5, §5.8.5, §5.10, §10.1; the peaks spec (all of it); AI insights §1
  and §8; accountability signals; console look §6, §9, §10, §16, §19, §20; `index.css`; every file in
  `pages/analytics/`, `components/charts/` and `components/insights/`; the team and group queries in
  `Modbot.Api/Features/Analytics/`; the file list of `Modbot.Analytics/`.
- **Looked at:** the 39 captures in the session's `analytics-shots/`, the `visual-pass/shots/verify-*.png`
  set, `assets/analytics.png`, `docs/public/screenshots/team.png` and `worlds.png`.

**What the screenshots do not show.** Every desktop capture is 1440×900, the window and not the full page.
The page scrolls inside the app's own frame, so `captureBeyondViewport` did not reach it. The lower panels
were judged from the code and from the scrolled tooltip captures: the VRChat page's Roles and tenure
panels, Instances' last four charts, and the lower half of Discord. The demo has no AI insights, so the
Insights panel was judged from code only.

**A correction to the brief.** `Modbot.Analytics/Retention/` is about deleting old facts
(`FactRetention.cs`, `RetentionPruner.cs`). It has nothing to do with whether members stay. The only
"who stayed" figure in the product is Discord's **New members still here** table
(`MyServer.tsx:191-215`). VRChat has none. The regulars profile from foundation §5.10 has not been built.
No `modbot_subject_profile` exists anywhere in `src/`.

---

## Verdict

The section is honest and it does not answer anything. What it gets right is real and uncommon. Every
peak carries its moment. "Nothing recorded" is kept apart from "nought". Head counts and presence reports
are never blended. Coverage is measured against the time instances were open, not the calendar, so a
quiet group is not called unreliable. Keep all of that. But the Sunday-morning owner who asks "is my
group growing or dying, and is it my fault?" gets no answer on any page, in any form. That hurts them
three ways, in this order:

1. **Nothing is compared with anything, and nothing is said in words.** "Joined 59 · Left 5 · Net change
   +54" has no "compared with the 30 days before". The only comparison in the product is inside AI
   insights, which are off by default and appear on one page. The reader is handed ingredients.
2. **The page that should answer the question contradicts itself on its first screen.** On VRChat the
   owner sees two dashes and "No member count readings in this range". Directly under them is a smooth
   member-count line that, in this demo, is most likely drawn from carried-forward facts with no mark
   saying so. On Discord, all five charts sometimes draw as empty boxes under full stat tiles. A reader
   deciding whether to trust Modbot stops there.
3. **Nothing tells the reader what matters, and several numbers read as broken.** Up to eight tiles of
   the same weight sit above up to eleven panels of the same height. "Busiest hour: 24.4 h" and
   "Busiest day: 2.2 days" sit on the Instances page. That is the wall of charts with no point, the
   other red flag.

The organising rule ("five sections, one question each") should go. The pages are split by data source,
not by question. Proof: "is the community growing" is asked on two pages with two member counts. "When is
the community active" is answered by two heatmaps on two pages. Moderation is counted on Team for VRChat
and on Discord for Discord. Replace the rule with "one page that answers first, then one page per
decision" (see *Rules I would change*).

---

## Page by page

Each sketch shows the headline (the one thing to see first), the lead chart (tall, full width), the
supporting detail (smaller, below), and what is cut or moved. In the sketches, "compared with the 30 days
before" follows whatever range is chosen.

### Team (`/analytics/team`)

**Claims to answer:** "Who is doing the moderation work, and when is nobody covering?" (foundation §10.1)

**Does it?** Half, and the half it answers it answers badly.

- **"Who is doing the work"** is answered with a table sorted by total actions (`TeamAnalyticsQuery.cs:135`).
  The total counts role changes, invites, approvals and unbans (F7). It is a leaderboard, and it is the
  picture the docs publish (`docs/public/screenshots/team.png`).
- **"When is nobody covering"** is answered with a list of gaps. In the demo that is two rows reading
  "unknown · a companion stopped reporting · nothing more was reported" (`team-30-desktop-dark.png`). A
  list of events cannot answer "when": that needs a week grid. The list names the last moderator to leave
  as its fourth column.
- Nothing on the page answers the head moderator's other two questions: who has gone quiet, and which
  nights had nobody on.
- It is the first entry under Analytics (`lib/nav.ts:48`), so the leaderboard is the section's front door.

```
┌ You · rowan-koi ─────────────────────────────────────────────────────────────┐   ← shown to every
│  4 actions on people · your usual month: 5 · last active Sep 26              │     signed-in moderator
│  On in 11 evenings · 0 open reviews about you                                │     whose VRChat id is linked
└──────────────────────────────────────────────────────────────────────────────┘
┌ Nights with nobody on ─────────────────────────── [3+ people ▾]  (a saved setting) ┐
│ HEADLINE  "Fri 20:00–23:00 had nobody on for 3 of the last 4 Fridays."            │
│ LEAD      week grid, Mon–Sun × hours, local time:                                  │
│           shade = how busy (head counts, complete)                                 │
│           outline = a moderator was there;  warn-colour fill = busy and uncovered  │
│           tap a cell → those gaps, with instance and time                          │
└────────────────────────────────────────────────────────────────────────────────────┘
┌ The team ─────────────────────────────────────── sort: [last active] [name] ┐
│ Moderator   On (evenings)   Actions on people   Door work   Last active      │
│ …sorted by last active, so "who has gone quiet" is at the bottom, not the    │
│ lowest scorer                                                                │
└──────────────────────────────────────────────────────────────────────────────┘
  supporting: Actions per week, stacked by kind  (click a bar → audit log, that week, that kind)
```

- **Cut:** the four-tile strip. "Coverage gaps: 2" counts rows, and "Instances nobody watched: 0 of 47"
  becomes the grid's coverage mark. Also cut the separate **What kind of actions** list, which the
  stacked weekly chart carries.
- **Move:** **Last moderator out** into a gap's detail, opened from a grid cell, not a column. Move the
  Discord page's **Moderation actions per day** here, so moderation is one question on one page.
- **Keep:** the **Reviews of unusual patterns** link. The 1/3/5/10 people choice stays, but as a group
  setting (F13).

### VRChat (`/analytics/group`)

**Claims to answer:** "Is the community growing or shrinking, and what changed?"

**Does it?** No. The growth number is there (+54), with nothing to compare it to. "What changed" is not
attempted: no event is marked on any time line, and no join source is shown. The first screen spends two
of six tiles on dashes (`vrchat-30-desktop-dark.png`, `vrchat-30-phone-dark.png`).

```
[7 days | 30 days | 90 days | 12 months]                        Aug 28 – Sep 26
┌──────────────────────────────────────────────────────────────────────────────┐
│ HEADLINE  Growing. 352 members, up 54 in 30 days (the 30 days before: up 21). │
│           59 joined, 5 left. 36 of 41 who joined in August are still here.   │
└──────────────────────────────────────────────────────────────────────────────┘
┌ Members ────────────────────────────────────────────── [online ☐] ──────────┐
│ LEAD  tall (220px) line of members; the previous 30 days as a faint line    │
│       behind it; calendar events and ban waves marked on the time line;     │
│       days with no reading shaded, carried days dashed; drag to zoom        │
└──────────────────────────────────────────────────────────────────────────────┘
┌ Joined and left, by week ───────────┐┌ Who stayed, by month joined ────────┐
│ joined up, left down, around 0       ││ Aug  41 joined · 36 still here 88%  │
│ click → Members, joined that week    ││ Jul  38 joined · 30 still here 79%  │
└──────────────────────────────────────┘└─────────────────────────────────────┘
  supporting, one flush row: Invites sent · accepted · Join requests · decided
  lower: Roles · How long members have been members
```

- **Cut:** **Joined minus left, running** (F11). Cut the **Most members** tile: the member line's own
  highest point already shows it. Cut the page-level "No member count readings" message; its job
  becomes the shading inside the chart (F2).
- **Move:** **Most online at once** into the member chart's tooltip and the Instances page. The
  **Insights** panel's team and instances kinds go to their own pages (F16).
- **Keep:** the **Invites and join requests** figures. Their bar chart can go. The four numbers carry it.

### Worlds (`/analytics/worlds`)

**Claims to answer:** "Which of our worlds actually get used?"

**Does it?** Nearly. The table is the closest thing in the section to an answer. It is ranked by the
wrong measure, though. "Time seen" exists only where a moderator's companion was
(`Worlds.tsx:14-20`), while head counts, which are complete, carry a world on every instance
(`busiestInstance` already reads world by world). Its **Time seen** column mixes "3.0 days" and "44.2 h"
(`worlds-30-desktop-dark.png`), so it cannot be scanned. Its chart is five smoothed lines crossing each
other.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ HEADLINE  Night Market carried the most people: 31 hours of people in 4      │
│           instances. Paper Lanterns was opened once.                         │
└──────────────────────────────────────────────────────────────────────────────┘
┌ Worlds, by time people spent in them (head counts) ──────────────────────────┐
│ LEAD  ranked bars, one row per world:                                        │
│  [thumb] Night Market   ████████████  31 h   4 opened   full 3 of 4   ▁▃▅█   │
│  [thumb] Tide Pool      ████████      22 h   5 opened   full 1 of 5   ▂▂▄▃   │
│  (last column: people-time by week, a small line per row)                    │
│  click a row → the world's popup                                             │
└──────────────────────────────────────────────────────────────────────────────┘
  supporting: Visitors (companion) as a column, with its coverage marked on the column head
```

- **Cut:** **Presence reports** as a headline tile (F12). Cut **Visitors per day, busiest worlds**,
  which the per-row small lines replace. Cut **Arrivals seen**, which nobody decides anything by
  next to **Visitors**.
- **Keep:** **Holds**, turned into "full N of M times opened". That answers the only real decision on
  the page: open a second instance of this world, or stop opening it.
- **Later:** Worlds becomes the "where" half of the Activity page, under Instances' "when" (see *Rules I
  would change*). Both answer where and when people spend their time, and the nav already indents both
  under VRChat.

### Instances (`/analytics/instances`)

**Claims to answer:** "When is the community actually active?"

**Does it?** The heatmap answers it. It is the best chart in the section, and it is below the fold at
1440×900 (`instances-30-desktop-dark.png` shows eight tiles, four instance cards and the start of a table
before any chart). The page carries **eleven** figures about instances over time. Nothing tells the reader
the heatmap is the answer.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ HEADLINE  Busiest: Saturdays 12:00–17:00 and Fridays 14:00–16:00 (UTC−7).    │
│           Busy time up 40% on the 30 days before.                            │
└──────────────────────────────────────────────────────────────────────────────┘
┌ When the community is active (UTC−7) ───────── [People | Instances opened] ┐
│ LEAD  the heatmap, full width, top three cells labelled with their number,  │
│       a five-step shade key, tap/click a cell → the instances of that hour  │
└──────────────────────────────────────────────────────────────────────────────┘
┌ People in instances ──────────────────────── (follows the page range) ─────┐
│ staircase, events marked, the busiest moment labelled on the line          │
└─────────────────────────────────────────────────────────────────────────────┘
┌ Busy time per week ─────────────────┐┌ Peaks ──────────────────────────────┐
│ bars: people-hours; dot: daily peak  ││ Most at once   52  Sep 26 05:29     │
│                                      ││ Fullest        16  Neon Yard 03:53  │
└──────────────────────────────────────┘└─────────────────────────────────────┘
  → "Open right now" and "Recent instances" link to Live, which already shows them
```

- **Cut:** **Closed** (tile and bars), **Most open at once, per day**, **Typical time open, per day** and
  **Most people seen in one instance, per day** (F10). Cut the **Busiest day** and **Busiest hour** tiles
  as written (F4). The busiest hour becomes the headline sentence.
- **Move:** **Open right now** and **Recent instances** to Live. The comment at `Instances.tsx:61-65`
  says a moderator "usually came with" the question "what actually ran last night". That is a Live and
  audit-log question. Answering it here pushes the page's own answer off the first screen.

### Discord (`/analytics/server`)

**Claims to answer (M5 §6, not foundation §10.1):** "Is the Discord server healthy, and who keeps it
going?"

**Does it?** The ingredients are here. **Active in 30 days: 227, 90%** is the best single health figure
in the whole section, and it is a quarter-width tile. **New members still here** is the only "who stayed"
table in the product, and it is half-way down the page. **Time in voice: 5.2 days** answers nothing.
It is also the landing page's picture of analytics (`assets/analytics.png`): six charts of equal
weight.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ HEADLINE  227 of 253 members were active in the last 30 days (90%);          │
│           the 30 days before: 84%. 4 people went quiet.                      │
└──────────────────────────────────────────────────────────────────────────────┘
┌ Active members ──────────────────────────────────── [Day | 7 days | 30 days] ┐
│ LEAD  line, previous period faint behind it; members as a thin second line  │
└──────────────────────────────────────────────────────────────────────────────┘
┌ New members still here ─────────────┐┌ Went quiet ─────────────────────────┐
│ after 7 / 30 / 90 days              ││ people, each opens their popup       │
└──────────────────────────────────────┘└─────────────────────────────────────┘
┌ Busiest hours (UTC−7) ──────────────┐┌ Busiest channels ───────────────────┐
└──────────────────────────────────────┘└─────────────────────────────────────┘
  supporting: Top contributors
```

- **Cut:** the **Time in voice** tile; **Messages per day**, whose figure moves into the Active chart's
  tooltip; **Minutes in voice per day**; **Joins and leaves per day**, which folds into the members line
  as marks; and the **Member count** chart as a separate panel.
- **Move:** **Moderation actions per day** to Team.
- If the rule changes as proposed below, this page stops existing. Its panels join VRChat's on Growth,
  Activity and Team, each labelled Discord.

---

## Findings

Ranked by how much they hurt the owner on Sunday morning. "Defence" quotes the spec or code where one
exists.

### F1. No figure is compared with the period before, and no page says its answer in words.
- **Hurts:** the owner, most. The head moderator second: "busier than usual?" is a rostering question.
- **Evidence:** every `StatStrip` (`MyGroup.tsx:52-63`, `MyTeam.tsx:45-59`, `Worlds.tsx:51-56`,
  `Instances.tsx:49-57`, `MyServer.tsx:43-58`) shows a bare number. `vrchat-30-desktop-dark.png`:
  "+54" with no reference. The only comparison is the insight's **Figures** table
  (`InsightBody.tsx:30-50`, "now" beside "before"), and insights are off until someone switches them on
  (`InsightsPanel.tsx:12-14`).
- **Defence:** none on comparison. On sentences, CLAUDE.md "UI text": *"A label names a control; a
  heading names a section."*
- **Instead:** every headline figure carries the same range before it, as a second number in `Stat`'s
  note slot ("+54 · before: +21"). Each page opens with one to two sentences put together from its
  figures by fixed rules, not by the AI model. Examples: "Growing. 352 members, up 54 in 30 days", or
  "Busiest: Saturdays 12:00–17:00". These are not explanations of the UI; they are the reading. The
  insights spec already defines "the stretch of the same length just before" (§1). Reuse that
  definition.

### F2. The VRChat page's first screen contradicts itself, and the member-count line hides where it comes from.
- **Hurts:** the owner, and the owner deciding whether to trust Modbot.
- **Evidence:** `vrchat-30-desktop-dark.png` and `vrchat-30-phone-dark.png` show "Most members —", "Most
  online at once —" and "No member count readings in this range", then a fully drawn member line. The
  peaks read only `group_member_count` (`GroupAnalyticsQuery.cs:111-121`). The chart splices in "the
  facts' daily observations from before the first reading" (`GroupMemberCountQuery.cs:20-26`), then
  draws both halves as one smooth curve (`MemberCountChart.tsx:117,128`, `type="monotone"`). So in this
  demo the line is most likely all carried-forward facts. That is inferred from the two queries; the
  demo's tables were not checked. The "Online" line climbs from 0 to 50 in an S-curve over two days,
  which no five-minute reading supports.
- **Defence:** the peaks spec §3.2: *"the highest number across a gap-ridden window is the highest Modbot
  saw"*. That is right about the peaks. It says nothing about the chart silently mixing two sources.
- **Instead:**
  - One source per mark. Readings are drawn solid as a staircase (`stepAfter`, as the head-count chart
    already is). Days carried from facts are drawn dashed. Days with neither are shaded.
  - Compute **Most members** from the same series the line draws, or drop the tile.
  - Never show a peak tile whose value is "—". Hide it and let the shading say why.

### F3. Charts sometimes draw as empty boxes under full numbers (a bug; cause inferred).
- **Hurts:** everyone. A blank chart under "Messages: 714" reads as "Modbot is broken".
- **Evidence:** `discord-30-desktop-dark.png`, `discord-30-desktop-light.png`,
  `discord-30-phone-dark.png`, `discord-90-desktop-dark.png`, `team-all-desktop-dark.png`, and
  `verify-discord-click-30-days.png` against `verify-discord-30-longwait.png`. The legend draws
  ("Joined · Left") and the plot does not. That means `ChartFrame` decided there was data
  (`ChartFrame.tsx:35`) and Recharts' `ResponsiveContainer` rendered nothing (`ChartFrame.tsx:43-48`).
- **Likely cause (inferred, not confirmed):** Recharts 3.10's `ResponsiveContainer` draws nothing until
  its `ResizeObserver` reports a width above zero. If the first measurement happens while the nested
  `PanelGrid` has not laid out, and no later resize arrives, the chart stays blank. That fits: it is not
  tied to one range, it depends on timing, and there are no exceptions.
- **Two things not ruled out:**
  - The captures were taken in headless Chrome with `captureBeyondViewport`, which resizes the page while
    it captures. The capture method may cause or worsen this.
  - `verify-blank-charts.js` listened only to `Console.messageAdded` and `Runtime.exceptionThrown`, not
    `Runtime.consoleAPICalled`. A Recharts "width(0) and height(0)" warning would not have been caught.
- **Cheapest check:** switch ranges 20 times in a normal Chrome window, with the console open for
  Recharts warnings.
- **Instead:** measure the frame's width in `ChartFrame` with its own `ResizeObserver` and pass
  `width`/`height` to the chart directly. Or give `ResponsiveContainer` an `initialDimension` and a
  `key` tied to the range, so it measures again after each data swap.

### F4. People-time is written as a length of time, which gives "Busiest hour: 24.4 h" and "Busiest day: 2.2 days".
- **Hurts:** the owner and the head moderator. These are the two figures meant for rostering, and they
  read as broken.
- **Evidence:** `instances-30-desktop-dark.png` and `instances-30-phone-dark.png` (`Instances.tsx:237-248`
  pass people-minutes to `minutes()`, `format.ts:46-52`). The same happens in "Time seen: 20.9 days"
  (in a 30-day range, `worlds-30-desktop-dark.png`) and "Time in voice: 5.2 days"
  (`discord-30-desktop-dark.png`).
- **Defence:** the peaks spec §2.1: *"Busiest is people-minutes, not the peak."* That is right as the
  ranking. It is wrong as the thing printed.
- **Instead:** rank by people-minutes, but print what a person understands:
  - **Busiest hour:** "24 people on average · Sep 26, 05:00–06:00"
  - **Busiest day:** "Sep 26 · 18 people on average over 7 h"
  - **Time in voice:** "about 4 hours a day" or per active person, or cut it.

  Add a `peopleTime()` formatter that never prints "days".

### F5. Every panel and tile has the same weight, so nothing is first.
- **Hurts:** the owner, who has a minute. The trust reader, who reads a uniform grid as padding.
- **Evidence:** `Stat` is one size everywhere (`shared.tsx:59-97`). Every chart is `chartHeight.regular`,
  160px (`theme.ts:32-36`; no page passes `tall`). The console look fixes the grid (§9.2: *"Readings
  always come in a `StatStrip`"*). `instances-30-desktop-dark.png`: eight tiles before any chart.
  `discord-all-desktop-dark.png`: five equal charts.
- **Defence:** `shared.tsx:14-17`: *"a template would pull them back towards the single dashboard they
  replaced."*
- **Instead:** a three-level model on every page, and it is a design:
  1. **Headline:** one sentence plus one figure, at 2.5× `--text-base`, with its comparison.
  2. **Lead chart:** full width, `chartHeight.tall`, the chart that proves the headline.
  3. **Supporting:** half-width panels at `small` height, or a flush row of plain figures.

  At most four tiles on a page. A second strip of tiles is a sign the page is answering two questions.

### F6. Coverage is a box of text above the chart or a card at the bottom, never inside the chart.
- **Hurts:** the trust reader (it is easy to scroll past) and the owner (a thin week and a quiet week
  look the same).
- **Evidence:** `CoverageNote` is the last panel on every page (`shared.tsx:190-235`; "Data covered" in
  `team-tooltip-dailybars-dark.png`). The thin markers are `PageMessage` boxes (`MyGroup.tsx:224-239`,
  `Worlds.tsx:43-49`, `Instances.tsx:78-82, 208-216`). No chart has any mark for a missing day, a partial
  today, or a night no companion saw.
- **Defence:** the peaks spec §3.1: *"Thin marks, it never hides."* Agreed. It should mark where the
  thinness is, not only that it exists.
- **Instead:**
  - Days with no data: shade them with a `ReferenceArea` at `--muted`.
  - Carried values: draw them dashed.
  - Today, which is partial: draw its bar outlined, not filled. `DailyBars` currently draws today's
    half-day as a real low bar, the "Sep 26" bar in `discord-30-*`.
  - Presence panels: shade the hours no companion reported.
  - Head-count panels: tint each day by its share of open time counted.

  Keep `CoverageNote` for "how far back", but move it behind an info control on the range bar.

### F7. The Team page's "Total" adds door work and admin to kicks and bans, then sorts moderators by it.
- **Hurts:** the moderator told they are "heavy-handed", and whoever reads the table as workload.
- **Evidence:** `TeamAnalyticsQuery.cs:121-136` sums every kind (`g.Sum(r => r.Value)`), then sorts by
  total, descending. `team-all-desktop-dark.png`: avery-stoat tops the table at 64, of which 35 are
  role changes. "Actions: 402" includes 216 role changes. The accountability spec §3.4 says the
  opposite: *"Not unbans (relief), not invites or approvals (welcomes), not role changes
  (administration). Counting those would make a moderator who runs the door look like one who runs
  people out of it."* The page and the review check disagree about what an action is.
- **Instead:** two columns, **Actions on people** (the §3.4 list) and **Door and admin**, never summed.
  The "Actions" tile and **Actions per day** use actions on people only, the number the reviews use.
  Kind columns go behind a row expander. That also fixes the 12-column table that overflows on phone
  and VR (`team-30-phone-dark.png`, `team-30-desktop-vr.png`).

### F8. "Actions per moderator" is a leaderboard, and a moderator sees the team before themselves.
- **Hurts:** the moderator reading about themselves; the head moderator, who is invited to reward
  volume.
- **Evidence:** sorted by total (`TeamAnalyticsQuery.cs:135`). Bold totals down one column
  (`team-30-desktop-dark.png`). No row is marked as the viewer's, though `ModbotUser.VRChatUserId`
  exists. Each moderator's usual is already computed for the reviews
  (`Modbot.Analytics/Reviews/ModeratorBaselines.cs`) and never shown.
- **Defence:** foundation §5.8.5: *"It surfaces patterns for human review. It never accuses."* The page
  keeps the letter of this, and a sorted volume table breaks its spirit.
- **Instead:**
  - Sort by **Last active** (which answers "who has gone quiet"), or by name. Never by volume.
  - Pin the viewer's own row at the top with "your usual month" beside it.
  - Show the team's middle value as a thin line through each count, not a rank.
  - Add **On (evenings)** from presence, so a moderator who stands in instances and rarely kicks reads
    as present, not idle.

### F9. Coverage gaps read as blame, and the limit that would excuse the moderator is not on the page.
- **Hurts:** the moderator; the head moderator, who gets a list where the answer should be a pattern.
- **Evidence:**
  - "Last moderator out" names a person as the fourth column (`MyTeam.tsx:91, 205-217`).
  - `TeamAnalyticsQuery.cs:29-31` says: *"A moderator without the client, in an instance no client is in,
    is invisible… Both limits are stated on the page."* They are not stated anywhere in `MyTeam.tsx`.
    The UI-text rule removed them.
  - "People left behind" comes from the companion and is *"a floor, not a count"*
    (`TeamAnalyticsContracts.cs`), while head counts, which are complete, now exist for every open
    instance (peaks spec §1.1).
  - The demo rows read "unknown · a companion stopped reporting · nothing more was reported" twice
    (`team-30-desktop-dark.png`). That is noise presented as a finding.
- **Defence:** foundation §10.1: *"the one analytics figure that suggests an action rather than
  describing a state."* True. The action it suggests is a rota change, and a list of incidents with a
  name on each does not lead there.
- **Instead:**
  - Draw gaps on a week grid of busy hours (Team sketch). Take the population from head counts and the
    cover from presence.
  - Drop the name column; a name appears only in the detail of one gap.
  - Mark hours where no companion was running as unknown, not uncovered. The grid's hatching says
    "unknown" without a paragraph.
  - Hide gaps whose end is unknown by default.

### F10. Instances spends six charts on one question.
- **Hurts:** the head moderator, who wants "when are we busiest" and has to cross-read six panels.
- **Evidence:** `Instances.tsx:113-180`: **Opened and closed per day**, **Most open at once, per day**,
  **Most people at once, per day**, **Busy time per day**, **Typical time open, per day**, **Most people
  seen in one instance, per day**. All are 160px and all share a day axis, next to the heatmap and the
  staircase. The four "per day" peak charts each answer "was that day busy", in four measures.
- **Defence:** the peaks spec §2.3 adds each for a reason. "Most people *seen*" is kept because *"the
  difference has to be visible in the label."*
- **Instead:** one **Busy time per week** chart (people-hours as bars, the daily peak as a dot in the
  tooltip). Delete **Most open at once, per day**: nobody rosters by instance count. Delete **Typical time
  open, per day**: a median time that moves daily is noise at 0–5 instances a day. Delete **Most people
  seen in one instance, per day**: head counts now answer it completely, and the presence version
  belongs to the person, not the chart. Delete the **Closed** bars: closed follows opened.

### F11. "Joined minus left, running" repeats the member count, badly.
- **Hurts:** the owner, who sees two growth lines that disagree in shape and scale.
- **Evidence:** `MyGroup.tsx:84-91`. It is a running total from the fact log's first day, drawn on an
  axis from zero, so it is a flat line at 300–360 (`vrchat-30-desktop-dark.png`). The **Member count**
  chart above it shows the same growth as a steep climb from 342 to 354. The file's own note
  (`MyGroup.tsx:21-23`) admits the number is not the member count and would *"climb from nothing"* on
  a large group.
- **Instead:** delete it. "Net change" belongs as the comparison beside the member count headline.

### F12. Worlds ranks by a figure only a moderator's companion can see, and headlines "Presence reports".
- **Hurts:** the owner deciding which worlds to keep opening. A world nobody with the companion visits
  ranks last however busy it was.
- **Evidence:** `Worlds.tsx:51-56, 58`. "Presence reports: 964" is a figure about Modbot, not about the
  community (`worlds-30-desktop-dark.png`). **Time seen** mixes "3.0 days" and "44.2 h" in one column.
  The legend's order (The Garage first) is not the table's order (Night Market first). The chart is
  five crossing smoothed lines.
- **Defence:** `Worlds.tsx:14-20`: *"the page says so rather than letting a zero pass as a
  measurement."* Honest, and fixable at the source.
- **Instead:** rank by people-time from head counts per world (complete; the instance's world is
  known). Keep **Visitors** (who) from presence as a column with its coverage mark. Demote **Presence
  reports** to that mark. One unit per column, always hours.

### F13. Time: UTC days, two range controls, no "all time" that scales, and time zones done by the browser's current offset.
- **Hurts:**
  - The head moderator: "Friday night" in UTC−7 is Saturday UTC, and for a group east of Greenwich an
    evening splits across two bars.
  - The owner with 18 months of data: "All time" draws 550 bars of 0–4.
- **Evidence:**
  - Ranges are whole UTC days (`useAnalytics.ts:11-18`, `format.ts:9-15` with `timeZone: 'UTC'`).
  - The member count and instance activity charts carry their own Day/Week/Month/All control
    (`vrchat-30-desktop-dark.png`: page says 30 days, the chart says Week).
  - The heatmaps shift every hour by *today's* offset (`Instances.tsx:261-271`, `MyServer.tsx:279-289`),
    so all data from before a clock change sits an hour off.
  - Discord's strip mixes a range figure (Messages) with now-figures that ignore the range (Members,
    Active in 30 days) without saying so (`MyServer.tsx:43-58`).
- **Defence:** the peaks spec §7: *"One control cannot honestly mean both, and the member count chart set
  the precedent."*
- **Instead:**
  - The readings charts follow the page range. The server already thins any window to 500 real readings
    (peaks spec §5), so 30 days is as drawable as a week. "Day" becomes drag-to-zoom on the chart.
  - Replace "All time" with **12 months**, plus a custom range picker (`DayField` exists).
  - Past 60 days, count in weeks automatically.
  - Add a group time zone setting that daily totals are cut by. A full rebuild from facts is the
    supported fix for any daily-totals change (foundation §5.4).
  - Shift heatmap hours with each hour's own offset, or bucket on the server by the group's zone.
  - Label the now-figures "now".

### F14. Sparse daily bars are a barcode.
- **Hurts:** the owner, who cannot see a trend in 30 hairline bars of 0–4, or in 365 bars.
- **Evidence:** "Joins and leaves per day" in `vrchat-30-desktop-dark.png`: green and orange hairlines,
  with left bars 1px wide beside joined bars. The same chart at a year is `discord-all-desktop-dark.png`,
  along with **Minutes in voice per day**. `DailyBars.tsx:44` (`barCategoryGap="20%"` and two series
  side by side at 30 bars in 560px).
- **Defence:** `DailyBars.tsx:12-14`: *"a line between two days implies values in between that were
  never measured."* Correct. Weeks are also measured.
- **Instead:**
  - When the largest daily value is under about 10 and the range is over 14 days, count by week.
  - Draw joined above zero and left below it, so one bar per week says both.
  - At 7 days, print the number on each bar.

### F15. Nothing on any chart can be clicked, although the places to go already exist.
- **Hurts:** the head moderator ("which nights", "which actions") and the moderator ("which of my
  actions").
- **Evidence:** no `onClick` in `DailyBars.tsx`, `DailyLine.tsx` or `Heatmap.tsx`. Tiles are text. Yet
  alerts already link to *"the members list filtered to the people who joined in that window
  (`/?joinedFrom=…&joinedTo=…`), the Flags page, the audit log, the Live page"* (insights spec §8.3).
- **Instead:**
  - A joined bar opens Members for that day.
  - An **Actions** bar opens the audit log for that day, and for that kind when stacked.
  - A heatmap cell opens the instances of that hour on those weekdays.
  - A moderator row opens their audit log, not only their popup.
  - Derived figures (people-time, medians) do not click: there is no record to open.

### F16. Insights and alerts live on one page, not beside the figures they describe.
- **Hurts:** the owner. The one place Modbot already writes a comparison in words is off to the side.
- **Evidence:** `AlertsCard` and `InsightsPanel` render only on VRChat (`MyGroup.tsx:42, 67`), and on
  Health. The **team** insight (insights spec §1: *"How much moderation happened"*) shows on the VRChat
  page, not on Team. Charts do not link to insights, and insights do not link to charts.
- **Instead:** each insight kind sits at the top of its own page, under the page's rule-made sentence.
  Each figure in the insight's table links to the chart it came from, with the range set. An alert card
  shows on the page its watcher belongs to (joins on VRChat, actions on Team, instance watchers on
  Instances), and the matching chart marks the alert's hour.

### F17. The light theme's green, amber and pink are below 3:1, and the promised labels or table do not exist.
- **Hurts:** the trust reader. It is also an accessibility failure.
- **Evidence:** `index.css:70-72`: *"Three of the light steps sit under 3:1 on white, which is why every
  chart that uses them ships visible labels or a table beside it."* Measured on white: series-3
  `#1baf7a` 2.82, series-4 `#eda100` 2.17, series-5 `#e87ba4` 2.69. They are used for Joined, Most
  people at once, Joined minus left, Most open at once, Typical time open, Active members, Join
  requests, Minutes in voice and Messages removed. None of those panels has data labels or a table
  (`vrchat-30-desktop-light.png`: the amber net line and the green joined bars).
- **Instead:** a **Table** toggle on every chart panel's strip, the same rows as the chart. That also
  gives screen readers the data and makes export trivial. Or darken the three light steps until they
  clear 3:1, and re-validate the hue order.

### F18. Line charts invent shapes, and ignore VR's stroke width.
- **Hurts:** the owner, who reads curves as trends.
- **Evidence:**
  - `DailyLine.tsx:62` draws every daily series with `type="monotone"`. Visitors per day becomes
    hills between single-day visits (`worlds-30-desktop-dark.png`); active members becomes waves.
    `MemberCountChart.tsx:117,128` smooths five-minute readings that are a staircase, while
    `InstanceActivityChart.tsx:124,135` correctly uses `stepAfter` for the same kind of data.
  - `DailyLine.tsx:66` hardcodes `strokeWidth={2}`. `index.css` sets `--chart-stroke: 3px` in VR
    (*"a 2px line at headset PPD is a suggestion"*) and `theme.ts:24` exposes it; no chart reads it.
  - `MemberCountChart.tsx:96` starts the members axis at 'auto'. It reads 342 to 354 at 30 days
    (the brief's "209" is Discord's `verify-discord-click-30-days.png`), and a 3.5% change is drawn as
    a 45-degree climb. The code's reasoning (`DailyLine.tsx:14-17`) is sound for levels, but the axis
    should say so.
- **Instead:**
  - `linear` for daily values, with dots when there are 31 points or fewer. `stepAfter` for readings.
  - `strokeWidth={chartTheme.strokeWidth}`.
  - A level chart that does not start at zero writes its change on the chart ("+12 · +3.5%") so the
    slope cannot be misread.

### F19. The heatmap has no key, and it cannot be read by touch or laser.
- **Hurts:** the head moderator on a phone or in VR, the two places this app is used away from a desk.
- **Evidence:** the cells answer only `onMouseEnter` (`Heatmap.tsx:125`). The value line appears under
  the grid (`Heatmap.tsx:79-91`, `instances-tooltip-heatmap-dark.png`). There is no shade key, and the
  dark theme's lower steps are nearly the same colour. The cell `aria-label` is "Mon 3: 5", which names
  no hour.
- **Instead:** tap or click selects a cell and keeps it. Print the number in the three busiest cells.
  Add a five-step key. Label cells as "Mon 03:00, 5 arrivals".

### F20. Small things that read as neglect.
- "1 actions" (`team-tooltip-dailybars-dark.png`). `ChartTooltip.tsx:32-33` prints the series name
  unchanged. Pass singular and plural forms.
- Wide tables overflow at 390px and in VR with no cue (`worlds-30-phone-dark.png`,
  `team-30-phone-dark.png`). Add a fade on the right edge, or pin fewer columns.
- Gaps show raw ids, "wrld_000030de-…" (`team-30-desktop-dark.png`), where every other table shows the
  world's name.
- `toLocalGrid` and `zoneLabel` are copied word for word in two pages (`Instances.tsx:261-279`,
  `MyServer.tsx:279-297`).

---

## Rules I would change

### Foundation §10.1, replaced

The present rule protects against one failure (the single metrics dashboard) by committing another (five
pages that each lay out ingredients). It also no longer describes the product. My Server was added by
M5, outside the table, and it repeats three of the table's questions for another platform.

> ### 10.1 Analytics answers first, then shows its working
>
> Analytics opens on **This week**: a short page of sentences, each with its number and the same number
> for the stretch before, answering the owner's three questions: *is the community growing, is it
> busier or quieter than usual, and was somebody on duty when it was busy.* Each sentence links to the
> page that holds its evidence. It is not a dashboard. It holds no chart that is not the proof of one of
> its sentences, and nothing on it may be there because it was easy to compute.
>
> Behind it, pages are split by the **decision** they support, not by the table or platform they read:
>
> | Page | Decision | Holds |
> |---|---|---|
> | **Growth** | Are we gaining or losing people, and who stays? | VRChat and Discord members, joins and leaves, who stayed by month joined, invites and requests |
> | **Activity** | When should we run things, and where? | when people are in instances and on Discord, busy time, worlds by time spent, peaks |
> | **Team** | Who is on, when is nobody on, and is the work shared? | the viewer's own work first, cover by hour of the week, actions on people, reviews |
>
> Every page opens with its answer as a sentence and one lead chart; everything else on it is visibly
> smaller and supports that chart. Figures from two platforms or two sources sit side by side, each
> labelled, never added together.

### Peaks spec §7, item by item

- **"A Peaks page of its own."** Keep the refusal, with a new reason. Replace with: *"No page of
  superlatives. A peak appears only where it serves a decision: the busiest hour and the fullest
  instance on Activity, as sentences; the busiest hour also in This week."*
- **"Folding the activity chart into the page's window."** Reverse it. Replace with: *"Readings charts
  follow the page's range. The server thins any window to at most 500 real readings, so a month is as
  drawable as a week. A shorter view is a zoom on the chart, not a second range control."* Two range
  controls on one page is the data model showing through, and the reader cannot tell which applies.
- **"Estimating what happened while nobody was counting."** Narrow it; do not drop it. Replace with:
  *"Modbot never fills a gap with a made-up value, and never shows a guess as a count. It may show two
  things, each labelled: the usual range for a figure, from the deployment's own history by the rule
  unusual-activity alerts already use (insights spec §8.2), drawn as a band behind the line; and 'at
  least n' where a figure is a floor."* The refusal as written leaves the pages with no sense of
  normal, and "normal" is the thing the owner is actually asking about. The alerts already compute it.
- **"Making the presence peak and the head-count peak one number."** Keep, unchanged. It is right.

### Console look §16, one rule added

> 10. A chart shows where its data is missing inside the plot: days with no data shaded, carried values
>     dashed, today's partial day outlined, hours no companion saw hatched. A sentence about coverage is
>     a fallback for a figure with no chart, not a substitute for marks on one.

This also settles the tension between CLAUDE.md's "no explanatory text" and honesty about coverage.
Marks are not text.

---

## Five ideas bigger than a fix

**1. A single overview page ("This week"): build it.** It is the page the Sunday owner needs and the
page §10.1 forbids. It is not the dashboard §10.1 feared, if it is held to sentences with their proof:
"Growing: 352 members, up 54 (before: up 21)", "Busier: 214 hours of people in instances, up 40%",
"Covered: 2 busy evenings had nobody on, both Fridays", "Worth a look: 1 open review, 3 alerts".
Each line links to its page. Cost: one new endpoint that calls the existing group, team and instances
queries twice, once for the range and once for the range before (the queries are bounded by the
window, so twice is cheap). Then one page of about 150 lines and the sentence rules. The hard part is
writing sentence rules that stay true when coverage is thin: "No head counts this week" must replace
the busy line, not sit under it. About a week of work. It should replace Team as the first entry under
Analytics.

**2. Answers first, a sentence at the top of every page: yes, made by rules, not by the AI.** A sentence
from fixed rules over the page's own figures is checkable, free, and works with AI off. The AI insight
sits under it as the longer reading, when switched on. This needs one line added to CLAUDE.md's
UI-text rule: a page's answer is content, not an explanation of the UI. Cost is small per page
(a function from the page's response to one or two sentences, with tests). The risk is sentences that
overclaim ("Growing" on +1). Give each a threshold, and fall back to plain figures below it.

**3. Clickable charts that open the records: yes for counts of records, no for measures.** A bar of
joins is a list of people; a bar of actions is rows in the audit log. Opening them turns analytics
from a report into a way in, and it is the head moderator's main missing move. People-time, medians
and peaks do not click: there is no record behind them, and pretending otherwise would be the
dishonesty this section has avoided. Cost: moderate. Members already reads `joinedFrom`/`joinedTo` from its address
(`lib/pageFilters.ts`). The audit log was not checked for day and kind filters in its address, and may
need them. Each chart needs an
`onPick(day, series)` prop, and the heatmap needs a selected cell. About a week.

**4. Coverage inside the chart: yes, and it is the single biggest honesty gain available.** Today the
section is honest in a box nobody reads. Shading missing days, dashing carried values, outlining
today and hatching hours no companion saw puts the honesty where the eye already is, and lets
several `PageMessage` boxes go. Cost: the API has to send per-day coverage, not only totals.
`MemberCountCoverage` has `daysWithReadings` as a count and needs the days. `InstanceCoverage` needs
minutes counted per day. `DailyBars` and `DailyLine` then take a `coverage` prop. Moderate: a few days
server-side and a few days in the chart parts. It also fixes F2's hidden splice.

**5. A moderator's own view of Team: yes, pinned above the team, for every moderator.** The page's
readers are its subjects, and the display takes the owner's side only. Show the viewer's own row
first: actions on people, their own usual month (already computed in `ModeratorBaselines`), evenings
on, and open reviews about them. Owners see the same block for themselves. A moderator who opens Team
after being told "the numbers say you're heavy-handed" then sees their own number against their own
usual and the team's middle value, with kinds split, before any ranking. The ranking itself should go
(F8). Cost: small. `ModbotUser.VRChatUserId` exists, and the per-moderator numbers are already in the
response. A moderator with no linked VRChat id sees a link-your-account row instead. Whether a
moderator may see the team table at all is a permission question for Sarmad. My view is yes, sorted
by last active.

---

## What is missing

- **Who stayed, by month joined, for VRChat.** It can be computed from join and leave facts. Discord's
  **New members still here** shows the shape; promote that and give VRChat the same.
- **Regulars.** Foundation §5.10 calls this "the question this whole section exists for", and it has not
  been built. "Is my group growing" is better answered by regulars than by members: 352 members with 20
  regulars is a different group from 352 with 120.
- **Compared with last month.** See F1.
- **Goals the owner sets.** The 1/3/5/10 gap choice is a threshold chosen fresh on every visit. Make it
  one saved setting ("we want a moderator on whenever N people are in"), shared with the
  `room-unwatched` alert's bar.
- **Sharing a chart.** Moderators paste into Discord all day, and link previews deliberately name no page
  (CLAUDE.md). The only way to share a chart is a picture. Add **Copy as picture** to each panel's strip.
- **Marks on the time line.** Calendar events have `StartsAt`/`EndsAt` (`CalendarEvent.cs:60-63`); alerts,
  ban waves and sync outages have times. None is marked on any chart.
- **Per-event breakdown.** For a calendar event, head counts give its peak, its busy time and the joins
  that night. That is the "what changed" the VRChat page claims and cannot answer.
- **A weekly digest.** The weekly insight already posts to Discord. Make it carry This week's sentences
  and a link, so the owner does not have to visit at all.
- **Anything that answers "why".** Joins by door (invite, request, open), leaves after a ban versus
  leaves on their own, activity around events. All are in the fact log. None is on a page.

---

## What to leave alone

- Coverage measured against open time, not calendar time (peaks spec §3.1). It is the most careful idea
  in the section.
- Head counts and presence kept apart and named for the difference. Nullable peaks, and "nothing
  recorded" kept apart from nought. The tie rule in `Peaks.cs`.
- The staircase for head counts, server-side thinning, and not calling VRChat on page load.
- The fixed hue order (fix the three light steps; keep the order), `RankedList` as plain elements, the
  held-height `Nothing` row and the hollow-square empty row.
- One `ViewAnalytics` permission for a figure and its peak.
- Insights stored with the exact figures the model saw, and alerts judged against the middle of matching
  earlier windows, not the average. These are the right foundations for the comparison and "usual"
  bands above; reuse them rather than building new ones.
