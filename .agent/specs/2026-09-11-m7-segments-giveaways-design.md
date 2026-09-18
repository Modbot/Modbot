# Modbot M7 — Segments, Cohorts & Giveaways

> **What of this is built, as of 2026-09-17.**
>
> **§4 (Giveaways) is built**, and with it the parts of §2, §5 and §6 a giveaway needs:
> the predicate model and its builder UI (§2.1), honesty about precision (§2.3), the performance
> rules (§5) and the privacy rules (§6) — including the retention refusal §6 names as the most
> likely quiet correctness failure. The design as built is
> [`2026-09-17-giveaways-design.md`](2026-09-17-giveaways-design.md); §2.6 there records how a
> segment feature reuses the rule model rather than growing a second one.
>
> **Not built:** §2.2 (saved, named, live segments), §3 (export, announce, bulk target), and the
> predicate families in §2.1 that a giveaway had no use for — active streak, lapsed, new this month.
> §7's non-goals stand unchanged.

- **Date:** 2026-09-11
- **Status:** Draft, awaiting review; §4 built (see the note above)
- **Covers:** M7 — the segment query builder, saved cohorts, exports, bulk targeting, giveaway draws
- **Depends on:** M0 (facts, daily totals), M2.5 (profile, metrics), **M3 (presence data — the reason this is interesting)**, M5 (Discord facts)

---

## 1. What M7 is

One query engine with three faces:

> *"Members who spent more than 10 hours in our worlds in the last 30 days, have no bans, and joined
> before June."*

- **Answer it** → a list, on screen.
- **Act on it** → export, announce to, or bulk-target the result.
- **Draw from it** → pick a random winner, verifiably.

This is the milestone where the fact log stops being infrastructure and starts being the product.
Everything before it recorded history; this is where a group *uses* it.

It was originally sequenced last. Moving the client to M3 unblocked it five milestones early
(foundation §14.1), because presence data is what makes the interesting questions askable.

---

## 2. The query model

### 2.1 Predicates over facts and daily totals

| Dimension | Source | Examples |
|---|---|---|
| Membership | current-state tables | joined before/after, current roles, membership status |
| Moderation | facts | ban count, kick count, classifications, never-actioned |
| Presence | facts + daily totals (M3) | hours in instances, distinct days seen, last seen, first seen |
| Discord | facts + daily totals (M5) | voice minutes, message volume, server tenure |
| Derived | daily totals | active streak, lapsed, new-this-month |

Combined with and/or/not, and expressed in a **builder UI first** — not a query language. The
audience is a community manager, not an analyst. A saved segment may expose its underlying query for
those who want it, but nobody should have to learn a syntax to run a giveaway.

### 2.2 Segments are saved, named, and live

A segment is a definition, not a snapshot. Re-running it next month gives next month's answer, which
is what makes "our regulars" a thing you can track rather than a list that decays.

Snapshots are taken explicitly, and a draw always snapshots (§4.2).

### 2.3 Honest about precision

Foundation §5.3's `occurred_before` and `source` carry through into results. A segment built on
presence data must not silently mix precisely-timed client facts with coarse polled samples (M6 §3.2)
and present the total as exact.

Where a result depends on low-precision data, the UI says so. "About 10 hours" is a useful answer;
"10.0 hours" that is actually a polling artefact is worse than no answer, because someone will make a
decision on it.

---

## 3. Acting on a segment

- **Export** — CSV/JSON, with an explicit consequence warning: exported data leaves Modbot's retention
  and purge guarantees entirely, and the operator now owns it.
- **Announce** — a Discord message to linked members in the segment, rate-limited and preview-first.
- **Bulk target** — hand the segment to M4's bulk actions, inheriting every safeguard in M4 §5
  (preview, typed confirmation, serialised execution, grouped reversal).
- **Draw** — §4.

Bulk-targeting a segment is the most dangerous operation Modbot offers: a wrong predicate plus bulk
ban is a group-destroying event. It therefore requires the segment's **preview list** to have been
viewed in the current session before execution is offered — you must look at who you are about to
action.

---

## 4. Giveaways

### 4.1 Why this is a feature and not a toy

Weighting a giveaway by time spent in your worlds rewards the people who actually show up, and it is
impossible without recorded presence history. It is also the single most legible payoff of the fact
log for a community — most members will never see a profile, but they will notice that the raffle is
fair.

### 4.2 Fairness has to be demonstrable

A draw nobody can verify is a draw nobody trusts, and a rigged-looking giveaway does more community
damage than no giveaway.

- **The entrant snapshot is frozen and stored** at draw time — the exact list, with each entrant's
  weight, permanently retained.
- **The seed is published.** Modbot commits to a seed before drawing and reveals it after, so the
  result is reproducible by anyone with the snapshot and the seed.
- **The draw is a fact**, with its parameters, and is not re-runnable in place. Re-drawing creates a
  new, visibly distinct draw rather than overwriting the old result.
- Exclusions (staff, prior winners, banned members) are part of the recorded parameters, not applied
  invisibly afterwards.

### 4.3 Weighting

Uniform, or weighted by any numeric the segment exposes — hours in instances, distinct days seen,
voice minutes. Weights are shown per entrant in the snapshot, with a cap available so one very
dedicated person cannot hold most of the probability mass.

---

## 5. Performance

Segment evaluation is the heaviest read in Modbot: predicates spanning millions of partitioned fact
rows and a daily total table.

- Prefer **daily totals over raw facts** wherever a daily total answers the question. "Hours in the last 30
  days" is a daily total sum, not a scan of presence events.
- Evaluation is **bounded and cancellable**; a pathological predicate is stopped and reported rather
  than holding a connection until something times out.
- Results are paged, with the count computed separately so the UI can show a total without
  materialising everyone.
- Saved segments may cache their last result with a visible computed-at timestamp — never presented
  as live when it is not.

---

## 6. Privacy

Segments are the point where recorded history becomes *actionable targeting*, which is exactly where
foundation §5.5's position earns out.

- Segment results respect **purge-user**: a purged user cannot reappear in a segment.
- Retention limits what can be asked — but only where an operator has set a window, since nothing
  is pruned by default (foundation §5.5). A segment reaching further back than the surviving facts
  is not answerable, and Modbot **says so** rather than silently returning a partial answer that
  looks complete. This is the most likely place for a quiet correctness failure and needs an
  explicit test. The default configuration is the one where it cannot happen, which is a reason to
  test the configured case deliberately rather than to assume it is rare.
- Exports carry the warning in §3. Once exported, none of the above applies any more.

---

## 7. Non-goals

- A general-purpose SQL console. Predicates are structured and bounded.
- Automated recurring actions driven by segments ("auto-kick anyone inactive 90 days"). Segments
  inform humans; they do not fire moderation on a schedule.
- Cross-group segments. Federation is M8.
- Prize fulfilment, delivery, or tracking. Modbot picks a winner; the group handles the rest.

---

## 8. Open questions

1. **Builder UI shape** — the recurring hard problem in this class of feature. Worth prototyping
   against three real questions a group actually asks before committing to a design.
2. **Whether segment definitions should be exportable/importable** between deployments, so groups can
   share useful ones. Attractive, and a way for the project to ship good defaults.
3. **Draw seed commitment mechanics** — publishing a hash beforehand and the seed afterwards is
   stronger than publishing the seed alone, and costs little. Probably worth it.
4. **Daily totals coverage**: which predicates need new daily totals to stay fast, and whether those can be
   filled in from retained facts at the time M7 is built — they can only be filled in across the window
   the presence retention still covers.
