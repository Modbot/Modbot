# Member and ban sync — design

**Milestone:** M1 of the foundation spec. **Status:** built 2026-09-13.

Two producers read VRChat's member list and ban list for the managed group, keep a current copy
of each in the database, and record what changed as facts — but only what the audit log did not
record first. The Members page and the Bans page read the copies.

This document carries the decisions. The foundation spec (`2026-09-04-modbot-foundation-design.md`)
carries the rules they follow: §4.2 pacing, §4.2.2 desynchronised scheduling, §4.2.4 persistence,
§4.3 rate limiting, §5.3 the fact schema.

## 1. What a sweep is

A **sweep** is every page of `GET /groups/{id}/members` (or `/bans`) from offset 0 until VRChat
returns an empty page. Neither endpoint has an offset cap — measured to 100,000,000 (audit-log
research §7.1) — so plain offset paging is the right shape and nothing needs filters or sort
windows.

Each pass of the producer reads **one page and saves once**: the rows, the sightings, and the
cursor on the settings row together (§4.2.4). A restart resumes from the cursor rather than from
the front. The pass that reads the empty page does the end-of-sweep work: it marks every row not
listed this sweep as gone, writes the first sweep's snapshot fact, and settles the changes that
have been waiting for the audit log (§4 below).

The list never includes the account doing the asking, so the bot's own membership is never a row
and is never marked as left.

### 1.1 Pages overlap

Offset paging over a live list is not stable. A member leaving while the sweep is between two
pages shifts everyone after them one slot earlier, and the person who was first on the next page
is now last on the page already read — never listed, and wrongly marked as gone. So the offset
steps by `pageSize − 5`: up to five departures between two pages cost nothing, and the duplicates
the overlap produces are upserted and cost nothing either.

The false leave this cannot prevent — more than five departures in two seconds, or an ordering
change on VRChat's side — is caught by §4: a leave is not written until the next sweep, and a
member back on the list with the same join date has the leave dropped before it was ever a fact.

### 1.2 A list that suddenly answers nobody

A sweep that lists nobody where the last one listed hundreds is far more likely to be VRChat
answering an empty first page it should not have than a group emptying out. Marking everyone as
left on that evidence would write hundreds of false leaves, so the sweep records a failure, marks
nobody, and starts over after the rest.

## 2. The tables

`group_member` and `group_ban` are **current state, not history**. The history is the fact log;
either table can be rebuilt by the next sweep. They exist so the Members page can answer "who is
in the group right now, with which roles" without re-reading fifty pages of VRChat's list.

- The key is `(group_id, user_id)`, not `user_id` alone. The appliance manages one group today
  and the table does not bake that in — the same reason §4.3.1 keeps the resource dimension.
- **Nothing is ever deleted.** A member who leaves gets `left_at`; a lifted ban gets `lifted_at`.
  The facts about a person still refer to them, and "when did they leave" is worth answering from
  the row as well as from the log. Coming back clears the column.
- The list entry is kept as VRChat sent it, in `raw` (`jsonb`), so a question nobody has asked
  yet can be answered without another sweep. `roles` is a sorted JSON array with a GIN index for
  the role filter. `manager_notes` is kept where the bot's role may read it.
- `waiting_facts` holds changes noticed but not yet written (§4).

Every id is opaque text stored as received (§3.1.1). VRChat's own times — `joined_at`,
`banned_at` — are kept as VRChat stated them; Modbot's own stamps come from `IModbotClock`.

## 3. Poll rate

| | Members | Bans |
|---|---|---|
| Endpoint class | `groups.members` | `groups.bans` |
| Time between pages | **2 s** | **3.5 s** |
| Rest between full sweeps | **15 min** | **30 min** |
| Entries per page | 100 | 100 |
| Pacing floor | 2 s | 2 s |

The foundation spec's provisional table (§4.3.4) records the previous implementation at 1,500 ms
between member pages and 3,500 ms between ban pages. **Members run at 2 s, not 1.5 s**, because
§4.2 caps `groups.members` at one request per 2 seconds, §4.3.4 says §4.2 wins where the two
disagree, and the limiter already enforces the 2 s — a loop asking every 1.5 s would only wait on
its own bucket, which is the "spins against a bucket it cannot drain" bug the audit-log options
warn about. Bans keep the 3,500 ms they ran at in production: gentler than the cap, which is what
the standing rule asks for on an endpoint with little history behind it.

A typical 5,000-member group is about 53 pages with the overlap, so a pass is under two minutes
and the sweep spends roughly a tenth of its class budget on average. The rest is what leaves the
room §4.2 reserves for a moderator's own requests.

Every number is on the sync pacing document, lowerable through the settings screen like every
other rate (§4.2.1). The page delay is floored at the class cap. The rest is floored there too,
with the same honesty §4.2.1.2 gives for the audit log's maximum interval: shortening it makes
Modbot poll *more*, the limiter's cap still binds whatever it asks for, and pretending the field
is downward-only would be the kind of claim somebody later discovers by reading the code.

Scheduling follows §4.2.2. Page ticks come from a desynchronised schedule at the page delay, so a
process start lands at a random phase and a fleet restarting together re-spreads. The rest is a
jittered wait, ±10%. The sync class checks the rest against the stored completion time as well,
so a process restarting in a loop cannot begin a fresh sweep on every boot.

Both producers pass through `IVRChatGate` on their own endpoint classes, so a 429 on one list
cold-stops that sweep and nothing else (§4.3.1). A rate-limited page leaves the cursor where it
is; the next pass, after the stop lifts, reads the same page. Nothing is retried.

## 4. Facts: the audit log records first

The audit log is authoritative and exact — it states who did a thing and when. A list diff can
state neither; all it knows is that a row appeared or disappeared between two sweeps. When both
see the same join, the audit log's fact is the one that should exist, and the daily totals must
not count the join twice.

The audit log deduplicates on VRChat's entry id, which an inferred fact does not carry. So the
deduplication happens on the sweep's side, **before it writes**, and it needs the audit log to
have had its turn first. The rule:

> A change the sweep notices — a join, a leave, a role assigned or taken away, a ban, an unban —
> is put on the row as a **waiting change**, with the moment it was noticed. It is written as a
> fact only once the audit log has polled at least two minutes *after* that moment, and only if
> the audit log holds no fact of the same kind about the same person from around the time the
> change happened. If the audit log has never polled, or has been silent for an hour, the sweep
> does not wait for it.

"Around the time" is an hour of slack before the earliest the event could have happened: VRChat's
`joinedAt` when it stated one, otherwise the start of the window. A leave is also matched by an
audit-log removal or ban, and by a row on the ban list from the same window: somebody who
vanished from the member list because they were banned did not leave. Role changes are matched on
the role id the audit entry carries, so two roles assigned in one window are two facts.

What this buys, besides no double counting:

- **The false leave from a page-boundary miss never becomes a fact.** A member skipped between
  two pages is marked gone at the end of that sweep and is back on the next with the same join
  date; the waiting leave is dropped. A later join date means they really left and came back, and
  both facts are written.
- **A member first listed with a join date from before the previous sweep started** was there all
  along and got missed. That is a miss on Modbot's side, not a join; no fact is written.

An inferred fact is `source = SyncDiff`, has no actor — a list cannot say who did it — and carries
`"source": "list-diff"` in its payload so a reader can weigh it against an audit-log fact. Its
time is exact when VRChat stated one (`joinedAt`, `bannedAt`) and a window from the previous
sighting to the moment noticed when it did not (§5.3).

### 4.1 The first sweep writes a snapshot, not joins

The first full sweep of each list writes **one fact about the group** —
`vrchat.group.members.snapshot`, `vrchat.group.bans.snapshot` — with the headcount and the date.
Recording 4,700 "joined" facts would say 4,700 people joined on the day Modbot was installed,
which is false and would put a spike in every chart forever. Rows are inserted with no waiting
change, and inference starts from the second sweep.

## 5. Profiles

Every member and every banned person the sweep lists is recorded as a sighting through the
profile writer, so the existing profile sync fetches their profile in due course and the Members
page fills in names and pictures over time. The sweep never fetches a profile itself.

The sighting is dated at the **join or the ban, not at "now"**. Stamping 5,000 people as seen
this minute on every sweep would push them all to the front of the profile queue and drown the
people who are actually active (user profile sync design §3.2). A genuinely new member's join
date is recent and lands in the recent window on its own; a long-standing member's is old and
they are fetched from the "never refreshed" backlog like anyone else.

## 6. What the pages show

**Members** is the swept table, current members by default, newest joiner first, with a search
box (display name or id, case-insensitive, literal), a role filter, a status filter (members /
people who left / both) and paging — all on the server. Each row shows the picture and name where
the profile has been fetched and the id where it has not, roles as badges, the join date, the
sticky 18+ mark, and when Modbot last saw them. "Last synced" is the end of the last full sweep.
Before the first full sweep has finished the page says the list is partial rather than showing
twelve people as the group.

**Bans** has two tabs. *Ban list* is the group's, from the ban sweep: everyone VRChat says is
banned now, whenever that was — the one to check before concluding somebody is not banned. *What
the audit log recorded* is Modbot's memory of the bans it watched happen, with who issued them,
and it still states its window permanently: it is the only one of the two that knows who did the
banning.

The subject pane gains membership: member since, roles, representing, manager notes, left on,
and ban standing, each with how old that reading is.

**Sync health** shows both sweeps: phase (sweeping, resting, cold-stopped, retrying, idle), last
full sweep, pages walked, rows changed, changes recorded and left to the audit log, next pass.

## 7. Left out on purpose

**Invites and join requests are not swept.** `groups.invites` has a provisional budget of
0.29 req/s "matched to the conservative neighbour" with no data behind it, and join requests have
no row in the table at all. The standing instruction is to ask about the rate limit before
building against an endpoint Modbot has not used before (§4.3.4), so both wait for that answer.
The tables, the sweep loop and the waiting-change mechanism are all reusable when it comes.

**`GET /api/members/{id}` is `GET /api/members/membership?id=`.** A path segment would need a
route constraint, and a route constraint is a format check on an id (§3.1.1). The profile
endpoint made the same choice for the same reason.
