# People already in an instance

**2026-09-19**

A companion that starts while VRChat is already running knows who is in the instance around it and
never tells anybody. This is the change promised but not made in *Who is watching, and who
reported it* (§2, "What was not done, and why"), and it closes the gap that spec named.

Client only. No protocol change, no new fact type, no server change, no migration.

## 1. The defect

A public group instance announced in Discord: **People here now: 36 people**, and under **Who is
here**, five names.

Two things ruled out before looking any further:

- **Not the card's cap.** `InstanceCard.NamesListed` is 20 and `NameList` appends "and N more"
  when it truncates. Five names and no such line means the list really held five.
- **Not two views of one number.** "People here now" is VRChat's own head count for the instance.
  The names come from what companions reported. The two disagreeing is exactly what a missing
  set of presence reports looks like.

## 2. Where the person was lost

One person, traced from VRChat's log to the Discord field.

| Step | What happens to them |
|---|---|
| `VRChatLogTail.ReadPending` | Their `OnPlayerJoined` is in the part of the file that was already written when Modbot started, so the line comes out marked `IsReplay`. |
| `InstanceSessionTracker.Observe` | Buffered with the rest of the arrival burst, then turned by `CloseBurst` into an `ObservedPresence` of kind `PresenceObserved`. Correct, and the right kind. |
| **`PresenceObserver.Poll`** | **Dropped.** `if (!line.IsReplay) observations.Add(observation);` — the observation exists and is thrown away, because it came off a replayed line. |
| `EventRouter` → `ServerConnection` | Never reached. |
| `EventsHandler` → `FactWriter` | No fact is written; the server has never heard of this person. |
| `InstanceWatching.WhoWasThere` | Nothing to count them from. |
| `InstanceAnnouncer.NamesAsync` → `InstanceCard` | They are not in `InstancePeople.Here`, so their name is not on the card. |

The loss is one line of the client, and everything downstream of it is behaving correctly.

### Why the rule that dropped them is right about everything else

Replayed lines are read and not reported for a good reason: a client that reported them would
re-submit an evening of arrivals and departures on every restart, dated hours ago, every time
anybody quit and reopened Modbot. That rule stays exactly as it is.

What it was wrong about is that a log is not only a list of things that happened. It also ends
with a **roster**, and a roster is not history — those people are standing in the instance now.
Nothing later in the log will ever name them again: VRChat writes a line when somebody arrives and
a line when they leave, and writes nothing at all about somebody who is simply there. So a
companion started in a busy instance reported the instance as holding whoever wandered in
afterwards, and nobody else. Five of thirty-six.

### Why the server was not at fault

`InstanceWatching.WhoWasThere` already counts `InstancePresenceObserved` as somebody being
present, alongside `InstanceJoined`, and has since it was written. The watching rule that used to
make this instance read as unwatched was fixed on 2026-09-19 (*Who is watching, and who reported
it* §2): a watch now starts at the first fact a client reports in an instance, whatever it is
about. Both halves of the server were ready for these facts; nothing was sending them.

## 3. The change

`PresenceObserver` restates the roster **once**, on the first line VRChat writes *after* this
client has caught up with the file, as `PresenceObserved` dated at that line.

`InstanceSessionTracker.SeenAgain` already does exactly this, for the case where a stopped log
starts growing again. It is the same statement for the same reason, so it is the same call.

### Why it waits for a live line

A log that VRChat stopped writing on Friday still ends with a roster. Restating it on Monday would
put a dozen people back into an instance that closed days ago, and would start a watch on it.

Waiting for a line written after the client caught up is the whole guard, and it needs no clock and
no comparison against one: if VRChat is not running, the line never comes and nothing is restated.
If VRChat is running, it comes within about ten seconds — the file carries an `[IK Debug Log]`
frame-rate line roughly that often, and the largest gap between consecutive lines of any tag across
the observed session is eleven seconds. The same signal `InstanceStaleAfter` is built on.

### What it says, and what it does not

Everyone is `PresenceObserved`: **present at this time, arrived at some earlier time nobody saw**.
The moderator included.

That is not a rounding-down. It is the only honest reading:

- The other people really were already there when the moderator walked in, for an unknown length
  of time. This is the same fact their arrival burst would have carried had the client been running.
- The moderator's own arrival *is* in the replayed history with a real timestamp, and it is still
  not reported. Reporting it would date the watch at a moment this client was not running and was
  seeing nothing, which is a claim about coverage, not about a person. Their time in the instance
  is therefore under-stated rather than over-stated, which is the direction to err.

Nothing invents an arrival. `PresenceKind.Joined` still means a client watched somebody walk in,
which is what the auto-invite feature and the kick-duration display read.

### Coverage stays what it was

This adds no coverage. An instance no companion is in still reports nobody, because the report
comes from a companion's own log and there is none. What it recovers is the people a companion
*did* see, in a log it *did* read, and then dropped.

### Duplicate suppression is untouched

No rule is relaxed. Two moderators in one instance both restating the same roster is the ordinary
case and was already handled twice over:

- `FactWriter` deduplicates on subject, type and a five-second window, so two restatements landing
  together are one fact plus a supporting report. Restatements minutes apart are two facts, which
  is correct — they are two observations.
- `InstanceWatching.WhoWasThere` keys people by id and keeps the **earliest** fact of the current
  stay, so however many clients report somebody, they are in the list once, at the earliest time
  anybody placed them there.

### Pointing the reader at a different folder

Changing the VRChat log folder in settings leaves the reader following nothing and replays whatever
log is sitting in the new folder — "the same as at a fresh start", as the settings page has always
said. It was not the same: `PresenceObserver` recognised a new session by the *file* changing and
tested that there had been a previous file, which after a folder change there had not. So the
tracker carried the old folder's session across, and now would also have skipped the restatement.

The test is now simply "the file being followed is not the one that was being followed", which
covers both. On the genuine first pass there is nothing to forget and it costs nothing.

## 4. What is still not known

- **People who were in the instance before any companion was.** Unchanged and unfixable from the
  log: a companion reads its own moderator's log, and that log begins when VRChat launched. If
  nobody from the team was in an instance for its first hour, Modbot does not know who was in it
  during that hour, and this change does not pretend otherwise.
- **How long any of these people had been there.** Restated as "already here" and left there. The
  head count and the name list can still disagree for an instance whose companion arrived after
  most of it did — but now they disagree by who was there before *any* companion, not by who was
  there before the companion's *process* started.
- **A server paired in the middle of a stay** hears nothing about the instance until the next
  arrival or departure. Pairing a new server mid-instance is rare, it is a per-connection
  restatement rather than a per-client one, and it is worth doing separately if anybody meets it.
- **Facts already recorded.** Nothing is filled in retroactively; this reaches a deployment when
  its moderators run a companion carrying it.
