# Linked facts, and the repeat offender rules as settings

**Status:** implemented
**Date:** 2026-09-18
**Supersedes nothing.** Narrows foundation spec §5.8.4 and §5.8.5 — what "an action" counts as, and
which rules are the operator's rather than the build's.

---

## 1. The problem, in the maintainer's words

> Certain events fire two events at the same time and we need to be able to have linked audit logs
> so they count as one. For example a user banned while in an instance will get a ban log and a kick
> log around the exact same time, so it counts as two actions. We need a setting where we can adjust
> the "Repeat offender threshold" and also "Repeat offender types" so we can filter out instance
> mutes from instance kicks.

Two problems with one cause. Modbot counts **facts**, and some single human decisions leave more
than one fact behind. A moderator who bans somebody standing in one of the group's instances presses
one button; VRChat writes `group.user.ban` and `group.instance.kick`, a second apart. Every count in
Modbot then says two actions by that moderator, and two marks against that person. A group that sets
the repeat-offender threshold to three is really running it at one and a half.

The second half is the same complaint from the other end: not every kind of action is a strike, and
which ones are is the group's judgement, not Modbot's.

### 1.1 There is no instance mute

The maintainer's example names instance mutes. **Modbot does not record one, because VRChat's group
audit log does not have one.** The re-walk of 1,241 real entries
(`.agent/research/vrchat-audit-log-findings.md` §6) shows two per-person instance events:
`group.instance.kick` (408 rows) and `group.instance.warn` (39 rows). Nothing else. Foundation spec
§5.8.1 lists "warn / mute" as one row of a table about *future* Modbot-performed actions (M4), which
is where a mute would come from if VRChat ever exposes one.

So the setting is built to separate **any** kinds an operator wants separated, and instance kicks
and warns are already separate kinds. Nothing was invented to satisfy the example.

---

## 2. What is one decision

A pair of facts is one decision when **a person or a rule did one thing and two producers wrote it
down**. Three families of that, each found by reading what the producers actually write rather than
by guessing:

| Main fact | Follower | Why they arrive together |
|---|---|---|
| `vrchat.group.member.ban` | `vrchat.group.instance.kick` | VRChat throws a banned person out of the instance they were in, and logs both. |
| `vrchat.group.member.remove` | `vrchat.group.instance.kick` | The same, for a removal from the group. |
| `vrchat.group.member.ban` | `modbot.action.ban` | Modbot's record of who pressed the button, beside VRChat's record that it happened. VRChat attributes everything Modbot does to Modbot's own account, which is why both exist (§5.9.1). |
| `vrchat.group.member.remove` | `modbot.action.kick` | The same. |
| `vrchat.group.member.unban` | `modbot.action.unban` | The same. |
| `vrchat.group.member.ban` | `modbot.ai-moderation.group-ban` | AutoMod's record of which rule decided, beside VRChat's record. |
| `vrchat.group.member.remove` | `modbot.ai-moderation.group-remove` | The same. |
| `discord.member.ban` | `discord.member.leave` | Discord raises `guildMemberRemove` alongside `guildBanAdd`. |
| `discord.member.kick` | `discord.member.leave` | The same. |
| `discord.member.timeout` | `modbot.ai-moderation.timeout` | AutoMod's record of the rule, beside Discord's record of the timeout. |

The table lives in `LinkedActions.Pairs`. It is deliberately **not** open-ended: a pair is added when
somebody has evidence that two producers write one decision down twice.

**Mains and followers are disjoint sets.** No type in the table is both. A decision is therefore two
facts deep at most (one main, any number of followers) and never a chain where one link drags a
third fact in behind it. A test enforces it.

### 2.1 Which fact is the main one

The main fact is the one that counts; the follower is the one that stops counting.

- **Between a ban and its kick, the ban.** They are not equally important: the ban is the decision
  and the kick is how it took effect. A group that later reads "one ban" is reading the truth.
- **Between an upstream fact and Modbot's own record of the press, the upstream one.** Not because
  it is more interesting — Modbot's record is the one that names the human — but because it is what
  every existing count already counts. Choosing the other way round would move numbers on a
  deployment that changed nothing, which §2 of this spec exists to avoid.

---

## 3. How the link is stored

```sql
modbot_linked_fact
  fact_id           bigint       primary key   -- the follower
  occurred_at       timestamptz  not null      -- the follower's time
  main_fact_id      bigint       not null      -- the fact that counts
  main_occurred_at  timestamptz  not null
  linked_at         timestamptz  not null      -- from IModbotClock

  INDEX (main_fact_id, main_occurred_at)
  INDEX (occurred_at)
```

Four decisions are worth stating.

**It is a table beside the facts, not a column on them.** A shared id on each fact is the obvious
shape and it cannot be written: filling it in when the partner arrives means updating a fact row,
and `ModbotEvent`'s first rule is that facts are never mutated and never updated in place. A link is
also not something a source said — it is something Modbot worked out — so it belongs with the daily
totals and the repeat-offender rows, in the layer that is a cache of the fact log and can be thrown
away and rebuilt from it (§5.2).

**Only the follower gets a row.** The main is the fact every count already counts, so "does this
fact count" is one lookup on the primary key, and "what else happened in this decision" is one
lookup on the second index. A row for the main as well would buy nothing and could disagree with
itself.

**Both times are stored.** `modbot_event` is partitioned by `occurred_at`, so a join that names only
an id reads every partition the table has. Carrying the partner's time turns every lookup into one
or two partitions.

**No foreign key.** The fact table is partitioned and retention drops it a partition at a time; a
constraint would turn that into a per-row cascade. The rows here are pruned alongside the facts
instead — by `FactLinker.PruneAsync` when a partition is dropped, and by `UserPurger` before it
erases a person (§5.5). A row that outlives its facts is inert, but it is not left around.

### 3.1 Finding the link, in any order

Every fact, as it is written, looks **both ways**: if its type is a follower it looks for a main, and
if its type is a main it looks for followers already waiting. Whichever of the pair lands second
writes the row, and neither fact is touched to do it. So the case the maintainer will actually hit —
the companion reports the instance kick now, VRChat's audit log reports the ban at the next poll —
links exactly as well as the other order, across different `source` values.

The check runs inside `FactWriter`, after the insert, and returns before touching the database for
any type that is in no pair — which is nearly every fact, because presence reports are the bulk of
the log. A failure there links nothing and loses nothing: the fact is already written, and the
detection run relinks what it finds unlinked. A link that cost a fact would be the wrong trade.

### 3.2 What stops two decisions becoming one

Three rules, and they are the whole answer to "a moderator who bans someone and then, ten minutes
later, bans them again after an unban has made two decisions".

1. **Ten seconds.** `LinkedActions.Window`. VRChat writes a ban and its instance kick from one
   request, so live data has them inside a second; Modbot's record of a press and VRChat's record of
   the result are a round trip apart. Ten seconds covers both and is two orders of magnitude short of
   the ban-unban-ban case. Nearest in time wins, with the earlier row breaking a tie — the same rule
   the deduplication window follows (§5.7.1).
2. **A fact whose time is only known to a window never links.** §5.3's whole point is that a sync
   diff knows only that something happened between two polls. "At the same moment as" is not a
   question an inferred time can answer, and guessing it would quietly merge decisions that were
   minutes apart.
3. **A fact is never taken into a second decision.** The follower's id is the primary key, so a
   second main cannot steal a follower another main already claimed.

A fourth rule is about people rather than time: **two facts whose actors disagree are not one
decision.** Actors are compared only *within a platform*, because the VRChat fact names Modbot's
service account while Modbot's own fact names the moderator — the same decision seen from two sides,
not two people. Two moderators acting on one person in the same second are two decisions and count
as two.

### 3.3 The links in history, and the ones added later

`modbot_review_run_state.link_version` says which set of pairs the log has been read under;
`FactLinker.Version` says which set this build has. Behind, the next detection run reads the whole
log and finds every decision in it, then stores the new number.

That is how **existing rows are migrated**: every deployment starts at zero, so the first run after
the upgrade links the history it already holds, and the standings are rebuilt from it in the same
transaction. It is also how a release that learns about a new pair applies it backwards instead of
only to what arrives next. **Raise `FactLinker.Version` whenever `LinkedActions.Pairs` changes.**

The migration itself (`AddLinkedFacts`) creates an empty table and the column. No data migration:
the pairs live in C# and a copy of them in SQL would drift.

---

## 4. What changes in the counting

`RepeatOffenderCounter` leaves follower facts out **once, at the top**, rather than in each column:

- **Every column on a repeat-offender row counts decisions, not facts.** A ban that also kicked the
  person out of an instance is `1 ban, 0 kicks, 1 action` — not `1 ban, 1 kick, 1 action`, which
  reads as broken arithmetic and makes people distrust the page.
- `PatternChecks.SamePersonAsync` leaves them out too. This is the check that asks a volunteer to
  explain themselves, and "acted five times" must not mean "pressed the button three times".

The facts themselves are untouched and every one of them is still in the audit log.

---

## 5. The audit log shows a decision, not two rows

A follower fact is **not a row of its own**. It is carried inside the entry for its main, in
`AuditEntry.Linked`, and the row shows a badge with how many facts the decision left. Opening the
row shows the main's detail as before and a **Same decision** block underneath, each linked fact as
its own sentence that opens into its own full detail. Every fact stays visible; nobody has to notice
that two rows a second apart are one thing.

**A follower is only hidden when its main would have been shown.** Filter the log down to instance
kicks and every kick appears, including the ones that came with a ban — the main is not on that list,
so hiding the follower because of something the filters just excluded would read as a bug. The same
applies to the source chips. Everything else about paging is unchanged: the keyset cursor is still
the last row actually returned.

Cost: two extra queries per page, however many rows it has, and the names for the linked facts are
resolved in the naming pass that was already happening.

---

## 6. The two settings

Both live on the settings row, in the same sparse `ReviewThresholds` document the other detection
numbers use, and both are edited in **Settings → Moderation → Repeat offenders**.

| Setting | Stored as | Default |
|---|---|---|
| **Repeat offender threshold** | `repeatOffenderActionsIn30Days` | 3, clamped to 2–100 |
| **Repeat offender types** | `repeatOffenderTypes`, a list of fact types | absent — every kind |

`ReviewThresholds.CountedTypes` resolves the second: the chosen kinds, narrowed to ones that can
count, or **every kind** when nothing was chosen or nothing chosen is recognised. So a settings row
written before the setting existed, an operator who never opens the card, and a hand-edited row that
names nonsense all count exactly what every deployment counted yesterday. A list nobody can be
counted under would freeze every status at *Once*, which is worse than the default and much harder
to notice.

Choosing every kind stores **nothing** rather than a copy of the list, so a later release that adds
a kind of action adds it here too. That is what the sparse document is for.

The API refuses an empty list and refuses a type that is not one of the kinds that can count. The
kinds offered are `ActionsOnPeople.Types`; the labels are `FactLabels`.

**Which kinds count towards the status is the operator's to set; which kinds get their own column is
not**, because those columns are the record of what happened.

### 6.1 Changing either rebuilds the standings

`modbot_repeat_offender` is derived from facts under the rule in force when it was computed, so
saving a new rule and leaving the rows until the next scheduled run would show an operator the rule
they just replaced. The `PUT` runs `ReviewJob.RebuildAsync` — the recompute that already exists —
before it answers. The web card then re-reads what the server returned.

A save that changes neither number rebuilds nothing.

---

## 7. What was left out

- **The moderator pattern thresholds.** `SamePersonActions`, `FarAboveTeamMultiplier` and the rest
  are still document-only. The maintainer asked for the repeat-offender rules; a settings card with
  eight numbers on it would be a different request.
- **Deduplicating a fact against its own second report.** That is §5.7.1's job and is unchanged.
  Linking is about two *different* facts, not two reports of one.
- **Linking across a purge.** A person's link rows go when their facts do.
