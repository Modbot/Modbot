# Modbot — Imports and facts Modbot already has

- **Date:** 2026-09-19
- **Status:** Implemented with this document
- **Covers:** why an import wrote everything twice; what "the same event" now means when one
  record knows where it happened and the other does not; how far apart two times can be and still
  be one moment; the `dedup` switch on the import endpoint
- **Changes:** import design §6.1 (the window was zero; the match required the same instance),
  foundation §5.7.1 (the same match, and the lock that guards it)
- **Related:** import design §6 (the re-upload key, which this does not touch), foundation §4.4
  (one clock)

---

## 1. What went wrong

A group uploaded its old export and the audit log came out in pairs:

```
-Glossy-  kicked alienbro911  out of an instance.
-Glossy-  kicked alienbro911  out of The Midnight Bar #11032.
```

Two facts for one kick. One of them knows which instance it happened in; the other does not, and
that is the whole story.

Two things stopped the duplicate check matching, and both had to be fixed.

### 1.1 The match required the same instance

Before writing a record, an import asks the fact writer whether Modbot already has the event
(import design §6.1). That question is answered by one query, and the query compared the world
and the instance with `=`:

```csharp
&& e.WorldId == fact.WorldId
&& e.InstanceId == fact.InstanceId
```

An imported record has no world and no instance. It cannot: a record in a file has a kind, a
time, a subject and an actor, and nothing in the file format says where the action happened, so
every fact an import writes has both columns null. A kick Modbot read from VRChat's own audit log
while it happened *does* carry them, because VRChat's entry names the instance.

So the two never matched. `null = 'wrld_…'` is not true, and every in-instance action in the file
was written a second time. Not a near miss — this check could never fire for a kick, a warn or
anything else that happens in an instance, which is most of what a moderation export contains.

### 1.2 Times had to agree to the tick

The import passed a window of zero, so two records were the same moment only if their timestamps
were equal to the tick. Two systems that wrote down one action almost never agree to the tick: an
export usually carries whole seconds, and Modbot's own record carries the fraction it observed.
Even with §1.1 fixed, a ban at `14:00:07` in the file would not have met the same ban at
`14:00:07.412` in the log.

## 2. A record that does not know where it happened

**Where an event happened is compared only when both sides know it.**

- one side null, the other set → not a difference, and not a reason to call them two events;
- both set and different → two events, and they stay two;
- both null, or both the same → as before.

The world and the instance are two separate comparisons, because a fact can know one and not the
other.

The reasoning is not about imports. A record that does not say where something happened is a
record that is quiet about it, and quiet is not disagreement. Reading it as disagreement is how
one kick became two rows in a moderator's audit log.

### 2.1 Why the shared query, and not just the import path

The check lives in `FactWriter.FindRecordedAsync`, which two paths use:

- `FactWriter.WriteAsync`, for reports from moderator clients — the only source deduplication
  applies to (`FactDeduplication.AppliesTo`);
- `IFactWriter.AlreadyRecordedAsync`, which only the import calls.

It was changed **in the shared query**, for both. The argument in §2 is about what an absent
instance means, not about where the fact came from, and a client that reports a kick before its
log has named the instance is in exactly the position the imported record is in. A second
definition of "the same event" living beside the first is what import design §6.1 already refused
to build, and this would have been that.

Nothing else moved. Discord's recorder, the VRChat audit-log sync and the member-list diff each
answer their own "have I got this already" question against `modbot_event` directly, with their
own rules — Discord matches on the audit entry id and falls back to a two-minute window, the
audit-log sync matches on the entry id and an exact shape, the diff matches on type and role.
None of them goes through `FindRecordedAsync`, so none of them changed. That is worth saying out
loud, because the name `AlreadyRecordedAsync` appears in all of them and it is easy to read as
one thing.

### 2.2 The lock had to widen with it

The client path is a check-then-insert made safe by a transaction-scoped advisory lock keyed on
the event. That key held the world and the instance. Once a report with no instance can match one
with an instance, two such reports keyed on their instances take two *different* locks and both
pass the check — the exact hole the lock exists to close.

So the key is now the subject, the platform and the type, and nothing else. It costs a little
serialisation: one person having the same thing happen to them in two instances at the same
moment now queues. That is not a thing that happens, and a lock that does not cover what the
query matches is not a lock.

## 3. Two seconds

**The import's window is two seconds either side** (`ImportRunner.SameMoment`), where it was
zero.

Why two:

- an export that writes whole seconds and a log that writes fractions disagree by up to one
  second, and two systems each rounding or truncating on their own can disagree by just under a
  second in either direction;
- a second on top of that covers a recorder that stamps the row rather than the action;
- it is far below the fifteen seconds it takes somebody to leave an instance and come back, which
  is the shortest gap between two genuinely separate actions against one person. Nobody is kicked
  twice inside two seconds, because after the first one they are not there.

The old spec argued for zero on the grounds that an imported time is a time somebody **wrote
down**, with no clock skew to absorb. That was right about the timestamp and wrong about the
conclusion: two people writing the same thing down at different precisions is not skew, but it
produces the same disagreement, and zero handles it no better.

Its real worry was the spreadsheet that dates warnings only to the day, giving three warnings the
same midnight. A window would swallow the second and third — if the window were what told them
apart. It is not, and §3.1 is why.

### 3.1 Records in one file are told apart by their key, never by their time

An import already remembers the events it has written during this run and never treats one of
them as something Modbot knew beforehand (import design §6.1). Three warnings at the same
midnight are three writes, because the second and third meet the run's own set, not the log.
Duplicates *inside* a file are §6's job — `externalId`, or a hash of the record — and they always
were.

That set used to compare instants exactly, which was fine while the query did too. With a window
it had to widen to match, or the two would disagree and a record a fraction after one this run
wrote would be reported as "Modbot already had this" when what Modbot had was the fact this very
import wrote a moment earlier. It now asks the same question over the same window.

This is also what keeps two warnings a second apart in one file as two warnings: they are two
records with two keys, and the run's own set lets both through.

## 4. The stored fact is not improved

When a record matches a fact that has no instance, and the record could have named one, the
stored fact is **left exactly as it is**.

Two reasons, and the first is enough on its own:

- an imported record can never name an instance. The file format has no field for it, and
  `ImportRunner.ToFact` writes none. On the import path there is nothing to fill in, ever;
- facts are immutable (foundation §5.2 — no update, no delete, a correction is a new fact).
  Reaching back into a stored fact to fill a column would be the first write in Modbot that edits
  history, bought for a case that cannot arise.

On the client path, where reports do carry an instance, the arriving report is not simply thrown
away either: `modbot_event_report` already records that a second client saw the same thing beside
the fact, so the sighting is kept even though the fact is not rewritten.

## 5. The switch

`POST /api/imports` takes **`dedup`**, `true` by default:

| | |
|---|---|
| `dedup=true` (default) | A record whose event Modbot already has from somewhere else is counted as **already known** and no fact is written. |
| `dedup=false` | Every record is written, whatever Modbot already has. `alreadyKnown` is `0`. |

It is a query parameter, or a form field beside `file`, exactly like `dryRun`; it is stored on the
import row and comes back in `GET /api/imports/{id}` and in the list, so a run can be read back
months later and explained. The permission is unchanged: **Import old data**, as before.

### 5.1 What it does not turn off

**It does not turn off the re-upload check.** Those are two different questions:

- *"have I imported this record before?"* — `import_record`, keyed by the upload's source label
  and the record's `externalId` or hash (import design §6). This runs on every import, with
  `dedup` on or off, and it is what makes uploading the same file twice safe. Turning it off is
  not offered.
- *"does Modbot already know this happened?"* — the check in §2 and §3, against facts from other
  producers. This is what `dedup` switches.

So `dedup=false` on a file that has already been imported still writes nothing: every record is
skipped by its key. `dedup=false` is for the case where a person looked at the log, decided a
match was wrong, and wants their own record in beside Modbot's.

A dry run with `dedup=false` counts what that run would write, the same as any other dry run.

## 6. What this deliberately does not do

- **No screen.** Import has no UI (import design §9) and this adds none.
- **No per-record switch.** `dedup` is for the whole upload. A file whose records need different
  treatment is two files.
- **No merging of what is already there.** Two facts already in the log stay two facts. This
  changes what gets written from here on; it does not go back and tidy a log that was written
  twice. Those rows are removed by purging the people concerned or by restoring a backup, as
  import design §10 already says for an import with the wrong data.
