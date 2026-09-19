# Who is watching, and who reported it

**2026-09-19**

Two changes that share a cause: a client-reported fact has always known which client sent it, and
nothing built on that knowledge except a revocation story nobody has used. The first change makes
watching rest on it. The second stops throwing away the part of it that deduplication discarded.

Supersedes the watching rule in the Live design, and adds one derived table to the fact log's
storage (foundation §5).

## 1. The defect

An instance with a head count of eight, with two of the maintainer's companions standing in it,
read **Nobody watching** — and so showed nobody at all, because the roster is only believed while
somebody is watching.

### What the rule required

`InstanceWatching` counted a moderator as watching when a presence fact in the instance satisfied
all three of:

1. the fact carries a device id,
2. that device is in the device-to-owner map,
3. the device owner's linked VRChat account **is the subject of the fact**.

Condition 3 is the one that failed.

### Why it could not be met

The moderator's own presence is reported exactly once per stay, in the arrival burst:
`InstanceSessionTracker.CloseBurst` emits the local user's own `OnPlayerJoined` as an exact
arrival and everybody else's as "already here". Nothing repeats it afterwards.

`PresenceObserver.Poll` reads the log lines that were already in the file when the companion
started, so that it can work out which instance the moderator is sitting in, and **reports none of
them** — otherwise every restart would resubmit hours of old observations. So a companion started
while VRChat is already in an instance parses its own arrival burst, learns everything from it,
and sends none of it. From then on it reports every arrival and departure it sees, from inside
that instance, and the server never receives one fact about the moderator.

Condition 3 was therefore unmeetable for that client for the whole of that stay. Not a regression:
watching has only ever worked for a companion that was already running when its moderator walked
in.

### The evidence

The deployed instance `01a0b72c-cdcf-7a19-8eab-d8f4b758d5e7` (Cyber Bar #75595, opened 00:59:21,
head count 8, `watching: []`) held nine facts and no more — the read was not truncated. Every one
was `source: Client` with the same `deviceId`. Every one was about somebody else. Not one was an
`InstancePresenceObserved`, which is the fingerprint of a missing arrival burst: a live arrival
always produces a burst of "already here" facts for the people already in the instance.

Two hypotheses were ruled out rather than argued away:

- **An empty or mismatched owner map.** Pairing goes through `POST /api/companion-devices/pairing-code`,
  which takes the default authorization policy, and that policy carries `VRChatLinkedRequirement`.
  A device can only ever be issued to an account with a linked VRChat account, so the map cannot be
  missing a paired device's owner.
- **Facts not carrying a device id.** They do; the deployed facts show it.

## 2. The decision: the client is what is watching

The ownership rule was too strict for its purpose.

Watching exists to answer one question: **can the people list be trusted?** It can be trusted while
a client is in the instance reporting what it sees, and cannot be once no client is. That depends
on a client being there. It does not depend on whose account the reports happen to be about.

A companion only ever reports what its own moderator's VRChat log shows, and that log only shows
the instance they are standing in. **A fact in an instance from a client is that client being in
that instance.** Nothing else can produce one.

This is not a new idea in the codebase. `TeamAnalyticsQuery.CoverageGapsAsync` has counted cover as
"recognised moderators present **and clients reporting**" since it was written, with a client's
stretch running from its first fact in an instance. Two parts of Modbot disagreed about what counts
as cover; the analytics one was right.

### The rule now

A **stretch** belongs to a client, not to a moderator.

- **Starts** at the first fact that client reports in the instance, whatever it is about.
- **Ends** at whichever comes first:
  - that client reporting its own moderator's leave,
  - that client reporting a stopped log,
  - that client reporting from a different instance,
  - the moderator's own presence turning up in a different instance (which may be reported by
    anybody's client),
  - the instance closing.
- Overlapping stretches are one unbroken watch, as before.
- The watcher list groups stretches by moderator: one moderator running two PCs in one instance is
  one line, dated at the earlier of them. A reader wants to know who is there, not how many
  machines they run.

Two consequences worth stating, because both are visible:

- **A watch starts a second or two earlier than it used to.** It now starts at the first line of
  the arrival burst rather than at the moderator's own join inside it. That is the more accurate
  answer — the client was there for both — and the ten-second arrival-burst allowance already
  covered the gap, so who is listed as present does not change.
- **A moderator seen by somebody else's client makes that other moderator the watcher.** It always
  should have: their client is the one in the instance.

### What was not done, and why

The client could restate its roster once when it starts mid-instance — `SeenAgain` already does
exactly that when a stopped log resumes. That would also recover the people who were in the
instance before the companion started, which the server still cannot know about.

It was left out of this change deliberately. It needs a companion release to reach anybody, it
fixes nothing for facts already recorded, and it would leave the ownership rule — which is wrong
for its own reasons — in place. The server-side rule fixes every deployment at once, retroactively,
with no client update. Restating the roster on start remains worth doing for the people-already-
there gap, as its own change.

## 3. Supporting reports

### What deduplication kept

Nothing. `FactWriter.WriteAsync` found the existing fact under a transaction-scoped advisory lock
and returned `FactWriteResult(existing, WasDeduplicated: true)` without writing anything. The
losing report was counted in the ingest response — `EventBatchResponse.Deduplicated` — and then
dropped. Four to six moderators in an instance all report the same arrival; five of those reports
left no trace anywhere.

That is right about the fact and wrong about the evidence. Two independent clients agreeing is
stronger than one client saying so, and it is exactly what a moderator wants when an entry is
disputed.

### What is recorded now

`modbot_event_report`: one row per **extra** client per fact.

| Column | Why |
|---|---|
| `fact_id` | The fact already recorded when this report arrived. |
| `occurred_at` | The stored fact's own time, not the arriving report's — they can be up to a window apart and fall either side of a month boundary, and this is the bound retention prunes by. |
| `device_id` | Which client reported it. |
| `reported_at` | When the report reached the server, from `IModbotClock`. |

Primary key `(fact_id, device_id)`.

**It cannot make one event look like several.** No second fact is written; nothing counts from the
table — no arrival, no minute, no action, no daily total. A fact with five supporting reports is
one event to everything that adds anything up. It is derived data beside the fact rather than an
edit to it, for the reason `modbot_linked_fact` is: facts are never mutated.

**It cannot make one client look like two.** The client already named on the fact is skipped, so a
client that retries a batch — which resends reports the server has already seen — records nothing.
The key stops a retry in a later batch doing it either.

The first reporter stays where it has always been, in the fact's own payload under `deviceId`. Only
the extras need a row, which keeps the common path — one client, one fact — free of an extra write.

Rows are pruned where link rows are pruned: with the partition their facts were dropped with
(`RetentionPruner`), and with a purged person's facts (`UserPurger`).

## 4. Showing it

The device id has always been in the payload the audit log sends to the browser. A device id is a
machine: it names nobody and can be acted on by nobody. What reaches the screen is the account the
device was issued to.

**One line on the entry, not a tab.** A tab would be empty on every entry no client reported, which
is most entries outside an instance's own log, and hiding two names behind a disclosure to reveal
two words is worse than reading them. `Reported by Ada, Ben` sits under the sentence.

The audit log, the person popup's Logs tab and the instance popup all build on one entry type
(`AuditEntry`) and one naming pass (`AuditNaming.ResolveAsync`), so the resolution is written once
and reaches all three. Two queries per page however many entries it holds, and none at all for a
page with no client-reported fact on it. The audit log's expanded row — which already exists —
additionally lists each reporter with the time its report arrived.

A device Modbot can no longer put an account to is left out rather than shown as an id.

### Permission

The same gate as the entry, and no wider. Every client-reported fact is a **moderation** entry in
`AuditVisibility`, so anybody who can read one already holds `ViewAuditLog`; the instance popup
additionally requires `ViewAnalytics` for the popup and `ViewAuditLog` for its log. Naming the
reporter reveals nothing that the entry's own payload did not already carry to the same readers,
and Modbot account usernames already appear in the log as subjects of Modbot's own entries.

## 5. What this does not change

- Deduplication itself: still a windowed range check under an advisory lock, still one fact per
  event (foundation §5.7).
- Who is listed as present, and their arrival times.
- How analytics count time, cover or actions.
- What the companion sends. No protocol change, no client release needed.
