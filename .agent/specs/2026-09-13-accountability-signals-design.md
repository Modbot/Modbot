# Repeat offenders and moderator pattern reviews — design

- **Date:** 2026-09-13
- **Status:** Implemented (M2.5, second half)
- **Covers:** Foundation §5.8.4 (repeat offenders) and §5.8.5 (moderator pattern detection) as
  they can be built from the fact log alone, before Modbot performs any action itself (M4).
- **Related:** Foundation §5.8, §5.9, §5.10.2; M4 design §8; `.agent/docs/reviews-and-repeat-offenders.md`

## 1. What this is, and is not

Both halves are **queries over the fact log**, cached in tables the detection run rebuilds. Nothing
here changes anything on VRChat, sends anybody a message, or decides anything about a moderator. A
review is a question for a person; a repeat-offender status is a count with its rule printed beside it.

Foundation §5.8.6 is the reason this ships now: audit-log ingestion already supplies `actor_id`,
`subject_id` and the action type for every kick, warn and ban done in VRChat's own UI, so the
subject-side and actor-side aggregation work from M2.5 onward. Only the *capture* side —
classification prompts, ban reports, the moderator's own explanation — waits for M4.

## 2. Repeat offenders (§5.8.4)

One row per person in `modbot_repeat_offender`, rebuilt from facts:

| Column | Meaning |
|---|---|
| `instance_kicks`, `warns`, `bans`, `removals`, `rejections` | All time, by kind. `removals` is removal from the group (VRChat's word for a group kick); `rejections` is join requests rejected or blocked. |
| `unbans` | Counted for the record. **Not an action against the person** — relief is not a strike — so it is not in `actions`. |
| `actions`, `actions_last_30_days`, `actions_last_90_days` | The sum of the kinds above, minus unbans. |
| `moderators`, `moderators_last_90_days` | Distinct actors. VRChat does not always name one; an unattributed action counts as an action and as nobody's. |
| `first_action_at`, `last_action_at`, `last_action_type`, `last_actor_id` | Recency. |
| `status` | `once`, `more-than-once`, `repeat`. |
| `counts_change_at` | The next instant a windowed count changes on its own (see §4). |

**Status rule.** `repeat` when `actions_last_30_days >= RepeatOffenderActionsIn30Days` (default 3);
otherwise `more-than-once` at two or more actions ever; otherwise `once`. The window is fixed at 30
days because that is the number moderators say ("fourth kick in thirty days") and a second knob would
only make the word harder to read. The threshold is a setting. The UI never shows the word without the
rule (§5.10.3's principle: a claim a moderator can check, not one they have to trust).

**Surfaces.** A "History" block on the subject pane, and the "People acted on more than once" tab on
the Bans page. The list is defined by its title: `actions >= 2`. Somebody acted on once is history on
their own pane, not a pattern worth a list. Both need `ViewProfile`.

The proactive half of §5.8.4 — showing the count *at the moment of action* — needs Modbot to be
performing the action, and is M4.

## 3. Moderator pattern reviews (§5.8.5)

### 3.1 Which of the spec's signals are built

| §5.8.5 candidate | Status | Signal |
|---|---|---|
| Same actor → same subject, repeatedly, across instances and sessions | **Built** | `same-person` |
| No other moderator has ever actioned that subject | **Built**, as the condition that lets `same-person` fire at a low bar | (part of `same-person`) |
| Actor's rate of *unclassified* actions relative to peers | **Waits for M4** — needs the classification M4 captures | — |
| Actor's action volume relative to peers over the same period | **Built** | `far-above-team` |
| Auto-resolution where consistent, corroborated classifications explain the pattern | **Waits for M4** — same reason | — |

### 3.2 `same-person` — "Keeps acting on one person"

Over the last `SamePersonDays` (30) days, one moderator's actions on one person are counted, with a
**place** per action: the instance where the fact carries one, else the world, else the UTC day.
Two kicks in one session are one place; the spec wants a pattern *across* sessions.

Fires when:

- `actions >= SamePersonActions` (3) **and** `places >= 2` **and** no other moderator has ever
  acted on that person — the spec's "isolated grudge" shape; or
- `actions >= SamePersonActionsWhenOthersActed` (8) **and** `places >= 2`, whether or not anybody
  else has.

The second bar is much higher on purpose. A user everybody is removing must not read as a grudge
(§5.8.5: "an isolated grudge looks very different from a user everyone is removing"), and the cost of
asking a volunteer to explain a persistent troll is real. But a single kick by somebody else should not
switch the check off entirely, so it raises the bar rather than removing it.

A moderator acting on themselves is never counted.

### 3.3 `far-above-team` — "Far more actions than the rest of the team"

Per UTC day, per moderator, the count of actions on people. Fires when the day's top moderator has:

- `actions >= FarAboveTeamMinActions` (10), and
- `actions >= FarAboveTeamMultiplier (4) × max(next busiest moderator that day, team's usual per moderator-day, 1)`.

Two comparisons, because either alone misfires. Against the usual only, a raid night — when everybody
is busy — opens a review on whoever happened to be busiest. Against the next busiest only, a
one-person team has nothing to compare against. The larger of the two is the bar.

**Usual** comes from `modbot_moderator_baseline`, rebuilt each run from the daily totals: per
moderator, actions on people over the last `BaselineDays` (90) days **up to yesterday**, divided by the
days they did any. Today is excluded so the burst being judged cannot raise the usual it is judged
against. The team's usual is total actions over total active moderator-days. The check does not run
until the team has `FarAboveTeamMinTeamDays` (7) days with any recorded action: there is no "usual"
on day one.

### 3.4 What counts as an action on a person

Instance kicks, warns, bans, removals from the group, join requests rejected or blocked — the same
list on both halves. Not unbans (relief), not invites or approvals (welcomes), not role changes
(administration). Counting those would make a moderator who runs the door look like one who runs
people out of it.

### 3.5 Thresholds

All of the numbers above live in one sparse `jsonb` column on the settings row
(`settings.review_thresholds`), read through `ReviewThresholds`, with defaults and a clamp on every
read so a hand-edited row cannot set a threshold to zero. The defaults err towards *not* opening a
review, as §5.8.5 asks — the costs are asymmetric. There is no settings page for them yet; that is
deliberate until real data from this detection running has been looked at (M4 design §11.4).

## 4. When it runs, and how it stays cheap

The run happens **after each daily totals run, on its schedule** (every fifteen minutes, and once at
startup), inside `DailyTotalsService`. It is not a separate timer because the baselines are summed
from the daily totals and must not be a run behind them.

An incremental run looks only at what arrived since the last one, by `observed_at` with the same
one-minute overlap the daily totals use:

- **Repeat offenders:** the people with a new fact, plus every row whose `counts_change_at` has
  passed. That column is the earliest action still inside a window, plus the window — the moment a
  windowed count changes with nothing new happening. Without it a row would read "3 in the last 30
  days" for as long as nobody touched the person again.
- **Baselines:** the whole table, every time. A few hundred rows from the daily totals.
- **Checks:** the moderators who acted since the last run, and the UTC days their actions fell on,
  limited to the last `SamePersonDays`. The catch-up walk hands over months-old history; a review
  about a day last spring is a question nobody can usefully answer now.

A rebuild does the whole log. Both produce the same rows, because every table is a cache and every
review is keyed so that finding the same pattern again is a no-op (§5).

The run is one transaction under an advisory lock, so two cannot interleave and a failure part-way
leaves the caches as they were.

## 5. The idempotence rule

A review is about **one moderator, one signal, and one thing** — the person for `same-person`, the
UTC day for `far-above-team`. That triple is the key, and the rule is:

1. **At most one open review per key**, enforced by a partial unique index. While it is open, a run
   that finds the pattern again refreshes its numbers in place (`updated_at` moves) and opens nothing.
2. **A closed review is never reopened by the facts that opened it.** A new review for the same key
   opens only on evidence newer than the closed review's `window_end`. For `same-person`, facts up to
   that instant are excluded from the count, so the count starts again after a close — one more kick
   after the review is not a pattern, three more across places is. For `far-above-team`,
   `window_end` is the end of the day, so a reviewed day stays reviewed however many more actions
   land on it.

This is what makes re-running detection, or rebuilding everything from scratch, safe to do at any
time.

## 6. Reviews are facts too

Opening writes `modbot.review.opened`; closing writes `modbot.review.closed`. The subject of both is
the moderator on the VRChat platform, because the review is about their VRChat actions and belongs in
their history beside them. Opening has no actor (Modbot did it); closing names the Modbot account and
carries the note. Both are moderation history in the audit log, and the closing row and its fact
commit together — a review closed with nobody named is the failure §5.8 exists to prevent.

## 7. Access

- `ReviewTickets` for everything under `/api/reviews`. Deliberately not one of the moderation flags:
  the people being reviewed should not be the people closing the reviews (M4 design §8.3).
- `ViewProfile` for the repeat-offender list and a person's History block. It is a person's history.

## 8. What waits for M4

- The unclassified-rate signal and auto-resolution by corroborated classification (§3.1).
- Showing the repeat-offender count at the moment of action, and the quiet nudge to the moderator
  themselves (M4 design §8.1).
- Asking the moderator to explain in the review, and notifying group owners at `Warning` severity
  (M4 design §8.2). Today the Reviews page is the whole workflow.
- Attribution once Modbot performs actions: VRChat records them against Modbot's account (§5.9.1),
  so the actor these checks see will be Modbot until the merge with Modbot's own facts exists.
- A settings page for the thresholds, once there is data to choose them by.
