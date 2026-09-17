# Modbot — User Profile Sync

- **Date:** 2026-09-13
- **Status:** Implemented with this document; the rate and the queue's tiers were set by the
  maintainer the same day
- **Covers:** the `vrchat_user` table, the producer that fills it from `users.read`, the refresh
  queue and its one ordering rule, the sticky "18+ verified" flag, and the small surface that shows
  all of it
- **Implements:** foundation §4.2.5 ("User profile sync — the expensive one") and its "freshness
  is visible, never implied"; the profile snapshot the evidence design §12 relies on
- **Related:** foundation §3.1.1 (opaque ids), §4.3 (rate limiting), §5.3 (facts), §5.10
  (`modbot_subject_profile`, which this is **not**); research `vrchat-user-object-findings.md`;
  `.agent/docs/profiles.md`

---

## 1. What this is

`GroupMember` carries membership and no profile. Bio, status, pronouns, avatar, join date, trust
tags and age verification live on the user object, and VRChat hands that out **one user per
request** on `users.read`. Foundation §4.2.5 accepted that cost and asked for three things: a lane
of its own, a priority that puts freshness where it counts, and an age on every field shown. This
document is how those three were built, plus one rule the foundation did not have — the 18+ flag
that never turns itself off — and why.

### 1.1 Not `modbot_subject_profile`

Foundation §5.10's table is **derived from facts** and rebuilt from them on a schedule; a bug in it
is a re-run. The table here holds what VRChat returned, which is in no fact and cannot be rebuilt: a
bio the person has since rewritten exists nowhere else. The two are different things with different
guarantees, so the table is `vrchat_user`, the entity is `VRChatUser`, and nothing in either name
says "profile" in the §5.10 sense.

---

## 2. The table

`vrchat_user`, one row per VRChat user Modbot has ever seen, keyed on the opaque id (`text`, no
length, no shape check — §3.1.1). Created **lazily with only the id** the first time the id appears
anywhere Modbot looks; the profile columns fill in when the sync gets to them.

| Group | Columns |
|---|---|
| Profile as last fetched | `display_name`, `bio`, `status`, `status_description`, `pronouns`, `current_avatar_image_url`, `current_avatar_thumbnail_image_url`, `profile_picture_url`, `date_joined`, `tags` (jsonb), `last_platform` |
| Age verification, last seen | `age_verification_status` (text: `18+`, `hidden`, `verified`, or whatever comes next), `age_verified` |
| Age verification, remembered | `is_18_plus_verified` (**sticky**, §4), `is_18_plus_verified_at`, `is_18_plus_verified_source` (`vrchat` / `manual`), `is_18_plus_verified_by_user_id` |
| When | `first_seen_at`, `last_seen_at`, `last_refreshed_at`, `refresh_error`, `refresh_error_at`, `not_found_at` |
| Everything | `raw_profile` (jsonb): the body as it arrived, minus the fields Modbot never keeps |

Two indexes, on `last_refreshed_at` and `last_seen_at`: the queue's top-up (§3.5) is ordered on
those two and asked once a minute against a table that can hold 150,000 rows.

**Never stored, even in `raw_profile`:** `location`, `travelingToLocation`, `travelingToInstance`,
`instanceId` (instance secrets, foundation §5.3), `note` (the account's private note), `friendKey`.

**A 404 marks the row, it does not delete it.** The history that mentions the person is still real.
`not_found_at` is set, one `vrchat.user.profile.not-found` fact is written, and the row is left
alone for a week before it is asked about again — a deleted account does not come back, and a daily
request to confirm that would be the aggressive retry §4.3 warns against.

---

## 3. The queue

### 3.1 The tiers

Set by the maintainer on 2026-09-13. Highest first:

| Tier | `RefreshReason` | Who | Ordered within the tier by |
|---|---|---|---|
| 1 | `SeenInInstance` | a client reported them in an instance — a join, a presence report, an avatar change | most recent sighting first |
| 2 | `OpenedInModbot` | somebody has their profile open in the web app | most recent request first |
| 3 | `SeenInFactLog` | they did something the fact log recorded — joined, banned, kicked, or did the banning | most recent sighting first |
| 4 | `ProfileIsOld` | their profile is older than `StaleAfter` (6 h), or older than their last sighting | **oldest refresh first** |
| 5 | `NeverRefreshed` | their profile has never been fetched | most recent sighting first |

Tier 4 is the one tier ordered by age rather than recency, because its whole reason is age.

### 3.2 One order, one place

The ordering is a single comparison, `RefreshOrder`, unit-tested on its own:

> Tier first. Within a tier, most recent key first — except `ProfileIsOld`, oldest key first. Then
> the user id, so the order is total and two entries never compare equal.

Every refresh, whichever direction it arrived from, is decided by that comparison and nothing else.
There is one queue (`UserRefreshQueue`) and one producer taking from it; the three ways in — the
producer's own discovery (§3.3), the web app (§3.4), and the database top-up (§3.5) — all land in
the same sorted set. Separate loops per source would each need a share of the lane, and the lane
has one order.

**One entry per person.** A request for somebody already waiting either promotes them (a higher
tier arrived) or is absorbed (the same or a lower one). A moderator clicking a profile ten times is
one fetch; a person whose join, avatar change and presence report arrive in the same second is one
fetch.

**An entry answered before it is spent is dropped.** Each entry remembers the time of the sighting
or request that made it (its *key*), and at dequeue the row's `last_refreshed_at` is checked: a
refresh at or after the key already answers a sighting, a later refresh answers an "old profile"
complaint, any refresh answers "never refreshed". That check is against the key and not against when
the entry was queued, so an overlap re-read that re-queues last minute's sighting cannot earn a
second fetch.

### 3.3 Discovery from the fact log

Each pass reads the facts written since a cursor on the settings row
(`user_profile_events_read_through`), by id — the fact log's primary key leads with it, so this is
an index range on a table that must not be scanned once a second. Everyone a fact mentions gets a
row: the subject when the fact's **type** says its subject is a person, and the actor always, since
an audit-log actor is always a person.

By type, never by the shape of the id. "Does it start with `usr_`" is precisely the check §3.1.1
forbids, because legacy ids start with nothing in particular. `UserSightings` holds the list of
types whose subject is a person; a type not on it contributes no subject; the profile sync's own
facts contribute nothing at all, or every refresh would queue the next one.

A sighting inside `RecentWindow` (30 min) goes to the queue at tier 1 or 3; an older one only
updates `last_seen_at`. The one-off audit-log catch-up hands this pass a month of history, and a
person banned in March is not a fresh sighting — the top-up finds them in the backlog.

Ids are handed out at insert and committed slightly later, so a fact can land below a cursor that
has already passed it. Once a minute the pass re-reads a few hundred ids behind the cursor; the
sightings upsert is idempotent (`LEAST` / `GREATEST` on the two timestamps), so the re-read costs a
statement and nothing else. Missing one anyway costs only that the person is discovered by their
next fact rather than this one.

### 3.4 "Fresh enough" — protecting the lane from a click

Opening a profile in Modbot calls `POST /api/vrchat-users/refresh?id=…`, which queues at tier 2
and returns immediately. It does **not** queue when the profile was fetched within
`FreshEnoughWhenOpened` (30 s, a setting): the answer is `FreshEnough` and the screen shows what is
stored. A presence sighting has its own, shorter gap (`FreshEnoughWhenSeenInInstance`, 10 s) — the
strongest signal there is, but not one fetch per fact. Tiers 3–5 have no gap: their reason is that
the profile is not fresh.

The web app shows the stored profile immediately with "last refreshed X ago" and "refreshing…",
then polls the profile endpoint (2 s with a little backoff, giving up after a minute) until
`last_refreshed_at` moves. If it cannot move — the lane is cold-stopped, the account answered 404 —
the stored data stays and the reason is written beside it in plain words. Polling is the mechanism
for this milestone; there is no realtime channel.

### 3.5 Top-up from the database

Tiers 3–5 are durable: they can be re-derived from `last_seen_at` and `last_refreshed_at`. Once a
minute, and whenever the queue has emptied, the pass loads a batch per tier (100 each, ordered the
way the tier is ordered) and offers them all to the queue. The database supplies candidates; the
comparison decides. A restart therefore loses only the two transient tiers, both of which are
re-created within seconds by the next presence report or the next click if they still matter.

### 3.6 Why not persist the queue

Entries are requests, not history. The durable ones come back from the table; the transient ones
are worth nothing a minute later. A persisted queue would be a second table to keep in step with
the first for the sake of surviving the one event — a restart — after which its contents are stale
anyway.

---

## 4. The sticky "18+ verified" flag

**The maintainer's requirement, verbatim in spirit:** once Modbot has observed a user as 18+
verified even once, the flag stays true and cannot be cleared by any sync. Only a moderator with
the right permission can clear it, and that is recorded as a fact naming who did it.

**Why.** VRChat users choose whether their age verification is visible. A person who showed `18+`
yesterday can show `hidden` today, and to the API they then look unverified. But VRChat's
verification confirms 18-or-over and nothing else, and it does not un-happen. So `hidden` is not
"unverified" — it is "not saying" — and a flag that followed the visible status would forget a fact
Modbot had already learned, at the person's own discretion, silently.

**The rule, as built (`VRChatUserProfiles`):**

- `RecordProfileAsync` **may set** `is_18_plus_verified` to true and **may never set it false**.
  Any positive signal counts: a status of `18+`, the obsolete `verified`, or `ageVerified` true
  (research §2). The first time it is set, one `vrchat.user.age-verified` fact is written. A later
  `hidden` changes `age_verification_status` on the row — the screen shows both — and writes nothing
  about the flag.
- `SetAgeFlagAsync` is the only writer of false. It takes the Modbot account making the change and
  an optional reason, records `modbot.user-profile.age-flag.cleared` (or `.set`) with that account
  as the actor and the reason in the payload, and marks the row `manual`.
- After a manual clear, a sync that sees `hidden` leaves the clear alone. A sync that sees `18+`
  **again** sets the flag again: a new sighting is new evidence, and the moderator's decision was
  about the earlier one.
- The rule applies to **every** VRChat user Modbot records, whichever path wrote the row. There is
  one writer, and the account-linking work that lands next will record its user object through the
  same `VRChatUserProfiles`.

**Permission.** `ModbotPermissions.EditAgeVerification` (bit 18), granted on purpose rather than
bundled: clearing the flag is a decision about a person's record, and it deserves its own line on
the staff page.

---

## 5. Rate

The foundation spec (§4.2.5) wrote the users lane at 1 req/s. **The maintainer raised it to
3.5 req/s on 2026-09-13.** `VRChatRateLimits` sets `users.read` to that as both hard maximum and
default; the profile sync's `PacingFloor` is derived from the same constant, and the sync-pacing
settings can lower it like every other rate and never raise it (§4.2.1).

Everything else about the lane is unchanged from §4.2.5: its own lane, exempt from the group
endpoints' 2 req/s ceiling; a 429 **cold-stops the lane, is never retried, and applies the
multiplicative decrease to the global bucket anyway**, because a limit hit anywhere is evidence the
whole model is optimistic. The limiter already did the last part for exempt classes; the producer
tests now pin it. `users.search` is untouched: one request per 3.5 seconds, interactive only, never
swept.

| Group size | Full sweep at 3.5 req/s | (at the foundation's 1 req/s) |
|---|---|---|
| 8,000 | **~38 minutes** | ~2.2 hours |
| 16,000 | **~76 minutes** | ~4.4 hours |
| 150,000 | **~12 hours** | ~1.7 days |

A typical group refreshes every profile many times a day. The queue exists because even at this
rate a 150,000-member group cannot refresh everyone continuously, and because "who is here right
now" should not wait behind "whose profile is oldest".

**Settings** (`settings.sync_pacing`, keys `userProfile*`): `IntervalSeconds` (floored at the lane's
cap), `StaleAfterSeconds`, `RecentWindowSeconds`, `FreshEnoughWhenOpenedSeconds`,
`FreshEnoughWhenSeenInInstanceSeconds`, `RateLimitedIntervalSeconds`. The windows are not rates and
may move either way; shortening one makes Modbot choosier, not faster.

---

## 6. Facts

All under the person as subject, none with an actor except the two manual ones; all kept forever
(retention is a prefix test and none of these prefixes is presence).

| Type | When | Payload |
|---|---|---|
| `vrchat.user.profile.first-seen` | first successful fetch | `{baseline: {displayName, pronouns, dateJoined, ageVerificationStatus, ageVerified, tags, trustRank}}` — `trustRank` is Modbot's own field, the rank read off `tags` (research `2026-09-16-vrchat-trust-ranks.md`), carried whenever `tags` is |
| `vrchat.user.profile.changed` | a watched field differed from the last fetch | `{changed: {field: {old, new}}}` — the shape the audit-log mapper lifts under `changed`, so the timeline has one diff shape. A tag change that moves the rank carries `trustRank: {old, new}` beside `tags`, as the rank's names (`User`, `KnownUser`). `occurred_at`/`occurred_before` span the two refreshes (§5.3). |
| `vrchat.user.profile.not-found` | the first 404 for the id | `{detail}` |
| `vrchat.user.age-verified` | the flag was set by a sighting | `{ageVerificationStatus, ageVerified, source: "vrchat"}` |
| `modbot.user-profile.age-flag.set` / `.cleared` | a moderator changed the flag | `{reason, previousSource, previouslySetAt, ageVerificationStatusLastSeen}`; actor is the Modbot account; source `Manual` |

**Watched fields:** display name, bio, status description, pronouns, both avatar URLs, profile
picture override, join date, tags (as a set), age verification status and flag. **Not watched:**
online status and last platform, which flip with every session and would drown the log. The full
object is on the row; the fact carries the diff.

---

## 7. What is shown

- `GET /api/vrchat-users/profile?id=…` (`ViewProfile`): the stored profile, `lastRefreshedAt`,
  `stale`, the flag with its source and who set it, what VRChat showed last, and whether a refresh
  is pending, in flight, or blocked and why. The id is a query parameter, never a path segment —
  a legacy id can contain anything.
- `POST /api/vrchat-users/refresh?id=…` (`ViewProfile`): §3.4.
- `PUT /api/vrchat-users/age-verified?id=…` (`EditAgeVerification`): §4.
- The subject pane (foundation §10.2) shows all of it above the person's recorded history, with
  "last refreshed X ago" first and a stale label when it applies. The health page gains a
  "User profiles" row: people known, never refreshed, not found, waiting per tier, refreshes in
  the last hour, last 429.

---

## 8. Not built, and open

- **Profile screening** (foundation §4.2.6) runs on every refresh and is not built. This sync is
  where it will hang: `RecordProfileAsync` sees every bio as it changes.
- **The Members page** is still empty: there is no member sweep, so there is no list to open
  profiles from. The pane is reachable from Bans and the Audit log.
- **A live run** has to confirm the five points in research §5 — above all what `ageVerified`
  returns for another user whose status is `hidden`, which decides how often the sticky flag is
  doing work rather than restating what VRChat already said.
- **The overlap re-read** assumes a fact commits within a few hundred ids of being allocated.
  A long-open transaction on the fact log could beat it; the cost is a late discovery, not a lost
  fact.

---

## 9. Decision log

| Decision | Reason |
|---|---|
| Separate table, not §5.10's | Not derivable from facts; different guarantee. |
| Lazy rows with only an id | Everyone in the fact log is a person Modbot will want to ask about; the row is where `last_seen_at` lives before any fetch. |
| Sightings by fact type, not id shape | §3.1.1. |
| One in-memory queue, one comparison | The maintainer's instruction; separate loops would each need a slice of the lane. |
| Tier 4 ordered oldest-first, the rest newest-first | Age is tier 4's reason; recency is everyone else's. |
| Drop-at-dequeue judged against the key | So re-reads and duplicate sightings cannot buy a second fetch. |
| Fresh-enough gaps: 30 s opened, 10 s presence | Protects the lane from a click and from a burst of facts about one person; both are settings. |
| Sticky flag set by any positive signal, cleared only by hand | The maintainer's requirement; `hidden` is not "unverified". |
| A re-sighting after a manual clear sets it again | New evidence; the clear was about the old evidence. |
| Status and last platform kept but not diffed | Session noise; a fact per flip would bury the changes that matter. |
| Raw body stored minus secrets and the note | Answer later questions without another fetch; never persist an instance nonce. |
| 404 marks, never deletes; retried weekly | The history is real; a daily confirmation is the aggressive retry §4.3 forbids. |
| 3.5 req/s | The maintainer, 2026-09-13; the foundation's 1 req/s is superseded. |
