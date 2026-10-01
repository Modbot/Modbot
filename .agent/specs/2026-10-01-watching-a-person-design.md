# Modbot — Watching a person

- **Date:** 2026-10-01
- **Status:** Built with this document (task TASK-020 of the 2026-09-28 task list, approved by the
  owner on 2026-10-01). The choices in §8 were made by the builder and are open to the owner.
- **Covers:** a moderator's "keep an eye on this person": the watch itself, how it makes somebody
  Flagged, the notification when a watched person arrives, the check-back day and its reminder, and
  the time window for lifted bans on the Flagged rules
- **Related:** flagged rules design (2026-09-26), notes design (2026-09-18), notifications design
  (2026-09-18), live updates design (2026-09-16), foundation §4.5 and §5.8.2

---

## 1. Why

Every signal that made somebody Flagged was automatic: kicks and bans, warns, a Nuisance rank, an
AutoMod flag. A moderator who knew somebody was trouble had no way to say so that anything acted on.
A note is read only by whoever opens the person, a lifted ban flagged the person for ever, and
nothing told anybody when a person they were worried about walked in.

## 2. What a watch is

A watch is one moderator's "keep an eye on this person", on one account (VRChat or Discord), with:

| Field | Required | Meaning |
|---|---|---|
| Reason | yes, at most 200 characters | Why, in their own words. Shown only to *See the audit log*: the Notes tab, the watch endpoints, Now's follow-ups and the audit log. The roster chip and the join card say "Watched" alone (§4). |
| Ends | no | The day it stops on its own. Without one it stands until somebody stops it. |
| Check back on | no | The day somebody should look at the person again (§5). |

**A watch never acts.** Nothing on VRChat or Discord changes. It makes the person Flagged, it tells
the team when they arrive, and it can remind somebody to check back.

One watch stands per account at a time. Watching somebody already watched is refused; stop the old
one first. A watch whose end day has passed is over for every reader at once, and the background
pass (§6) writes its end into the log within a minute.

## 3. Where it is kept, and who may touch it

- A table, `person_watch`, holds the current state, because "which of these 240 people are
  watched?" is asked on every roster read. A partial unique index on `(subject_platform,
  subject_id) WHERE ended_at IS NULL` keeps one standing watch per account.
- Three facts keep the history in the moderation log, beside the person's notes:
  `modbot.watch.add` (Watch started), `modbot.watch.end` (Watch stopped; `ended: "expired"` and no
  actor when it ran out) and `modbot.watch.followed-up` (Followed up). Each carries the reason, so
  one entry answers what the watch was for.
- **Reading** is `ViewAuditLog`, as for notes: the history is facts in that log.
- **Starting** is `WriteNotes`. A watch is the same kind of act as a note, one moderator's word about
  a person, and a separate permission would be one more box to tick for the same people. *The owner
  may object; a permission of its own is a small change.*
- **Stopping and following up** are open to whoever started it, and to anyone with `WriteNotes`, as
  taking back a note is. Somebody who may change a watch but not read the audit log (they started
  it with only *Write notes*, or lost the audit log since) is answered with the reason left out.
- A purge of a person deletes their watches with their facts, and the purge preview counts them
  under **Removed** as **Watches**.
- The three watch facts are in the audit log's **Moderation** show filter, beside notes.

Endpoints: `GET /api/watches` (`?due=true` for follow-ups due), `GET /api/watches/person?vrchat=&discord=`,
`POST /api/watches`, `POST /api/watches/{id}/stop`, `POST /api/watches/{id}/followed-up`.

## 4. Flagged, and told when they arrive

- **A new Flagged rule, always on.** A standing watch on the person's VRChat account, or on a Discord
  account linked to it (the way AutoMod flags are followed), makes them Flagged. Reason text:
  `Watched`, **the word alone**. The Flagged reasons reach readers who may not read the moderation
  log: a paired companion (its token comes from *Pair a companion*), the Live page, the live stream
  and Now's instance rows (*See live instances*), and the chat and MCP place tool. A watch's reason is
  a moderator's words about a person, which only *See the audit log* may read, so it is shown only
  where the caller holds that: the person's **Notes** tab, the watch endpoints and Now's follow-ups.
  (Changed in review, 2026-10-01; the first build put the reason on the chip.) It is listed
  **first**, before the automatic rules, because a moderator chose to say it. It
  has no switch on the settings card: a switch that silently ignored every watch would be a second
  way of stopping them that nobody could see from the person.
- Because it is a Flagged rule, everything that shows Flagged follows with no code of its own: the
  companion roster and person card, the red **Flagged user joined** card with its sound and voice
  line, the live stream's `flagged_join`, the Live page, the count beside Live, and **In the group's
  instances** on Now.
- **A notification when they arrive.** The companion's ingest already picks out genuine arrivals
  (first report of a join, never a presence-observed). For each one who is watched it raises
  `modbot.watch.joined` through the notification pipeline: severity **Warning**, to everybody who
  holds both *See live instances* and *See the audit log*. **It says nothing about the person**:
  "A watched person joined a group instance.", linking to `/live`. No name, no id, no reason,
  because a notification row is kept after a purge has erased the person and goes out by email and
  Discord message; whoever opens Live sees who under their own permissions. The key is the watch
  and a hash of the instance's id (a VRChat instance id can carry its owner's user id), so somebody
  who leaves and comes back all evening is said once inside the quiet time, and the same person in
  another instance is said again.
- Discord instance cards are left as they are: they mark nobody Flagged today, and a "who is here"
  list in a channel the public may read is no place for a moderator's reason.

## 5. Check back on

A watch may carry a day to check back. From that day:

- **Now** lists it under **Needs a decision** as a **Follow-up** row: the person, the reason, how
  long it has been due, and **Followed up** for anyone who may change the watch. The card's header
  says how many are due.
- The person's popup shows **Follow-up due** on the watch and **Watched · follow-up due** on the chip.
- The background pass raises `modbot.watch.follow-up-due` once, severity **Warning**, saying only
  "A follow-up on a watched person is due." and linking to Now (`/`), for the same reason, to whoever set
  the watch. Everybody else sees it on Now.

**Followed up** clears the day and writes `modbot.watch.followed-up`; the watch carries on. To be
reminded again, stop the watch and start a new one with a new day.

The day is the start of the day picked in the moderator's browser; the end day is its end.

## 6. The background pass

`WatchReminderService` runs `WatchPass` once a minute: it closes every watch past its end day
(`ended_at` = the end day, the `modbot.watch.end` fact with no actor) and raises each follow-up
reminder not raised yet. Nothing a moderator sees waits on it.

## 7. Lifted bans get a time window

The **Kicks and bans** rule counted every ban for ever, including bans lifted years ago. A new
setting under it, **Lifted bans stop counting**, with **Days after lifting** (1 to 3650):

- Off (the default) keeps the old rule exactly.
- On, a ban followed by an unban counts until that many days after the unban. A ban that stands, and
  a kick, always count: nobody undid them.
- Read from the ban and unban facts in order: an unban lifts every ban before it not lifted yet.
- `priorActions` on the wire still counts every kick and ban, as it always has.

Stored in the same sparse `settings.flag_rules` document as `liftedBansForDays` (null = always), so
no migration.

## 8. Choices made while building, open to the owner

| Choice | Made | Other way |
|---|---|---|
| Who the arrival notification goes to | Everybody with *See live instances* and *See the audit log*, Warning | Only whoever set the watch; or Information (daily summary only) |
| Who the follow-up reminder goes to | Whoever set the watch | Everybody with *Write notes* |
| Where follow-ups live | On the watch | Also on case files and join requests (not built: the case file is still one ban's write-up, and join requests are never stored) |
| Where the Watch control sits | Top of the person's **Notes** tab, with a **Watched** chip in the row under the name | A button beside **Note** at the foot on a phone |
| Watch switch on the Flagged card | None | A switch like the other rules |
| A page listing everybody watched | Not built; the API lists them (`GET /api/watches`) | A list page, or a **Watched** filter on People |

## 9. Tests (written, not run)

- `FlagRulesTests`: a watch flags first as the word alone, never with its reason; a watch past its
  end day or stopped does not; a watch on a linked Discord account flags the VRChat person; lifted
  bans stop counting after the days set and a standing ban never does; the card saves and refuses
  the days.
- `WatchTests`: start, the permission, refusals (no reason, past days, twice), replacing one that ran
  out, stop, someone else's watch (403 also for an id that does not exist), a reader without the
  audit log refused, the follow-up being due, said once, and cleared, the pass closing an expired
  watch, and the arrival notification; neither notification holds the person's id, name or the
  reason in any field.
- `LiveStreamTests`: a watched person's join is a `flagged_join` reading "Watched", with the reason
  nowhere in what a See-live-instances reader is sent.
