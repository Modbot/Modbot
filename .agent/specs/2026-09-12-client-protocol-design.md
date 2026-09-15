# Modbot Client Protocol

- **Date:** 2026-09-12
- **Status:** Draft, awaiting review
- **Covers:** the wire contract between `Modbot.Client` (and the overlay) and a Modbot server
- **Depends on:** foundation §2.7.3 (API versioning), §4.4 (`IModbotClock`), §5.3 (fact schema), §5.7.1 (dedup)
- **Consumers:** M3 client and overlay

---

## 1. What this protocol is for

One moderator's Windows client talks to **several** Modbot servers at once (M3 §5.5), each possibly on
a different version, over a home internet connection, while VRChat is running.

Everything below follows from those four facts.

### 1.1 Design constraints, in priority order

1. **Never lose a fact.** A dropped connection must cost latency, not data. The client buffers and
   replays (M3 §5.3), so the protocol has to make replay safe.
2. **Never block VRChat.** The client shares a machine with a game that wants every core and every
   spare gigabyte. Chatty, synchronous, or memory-hungry designs are disqualified.
3. **Degrade, don't fail.** A server that is slow, down, or mid-deploy must produce stale overlay
   data, never a blank one (M3 §6.0.3).
4. **One client, many servers, different versions.** No design may assume a single peer or a single
   version.

### 1.2 Why not a websocket

A persistent duplex connection is the reflex answer and it is the wrong one here.

Presence events are **bursty and low-volume**: a burst on instance entry, then a trickle. Holding
open connections to three servers for hours to carry a few events per minute costs reconnection
logic, heartbeats, and a proxy-compatibility surface, in exchange for latency nobody is measuring.

More decisively: **a websocket makes the offline case the exception.** With batched HTTP, "buffer and
send later" is the same code path as "send now" with a different timer — replay is not a special
mode. With a socket, offline handling is a second implementation that only runs when things are
already going wrong, which is exactly when you want the least-exercised code path to not exist.

Plain HTTP also crosses corporate proxies, captive portals and VPNs that break websockets, and those
are real conditions on a moderator's laptop.

**Decision: batched HTTP POST, client-initiated, with long-poll only where the overlay genuinely
needs push (§6).**

---

## 2. Transport and framing

| | |
|---|---|
| Transport | HTTPS. Plain HTTP is refused by the client, not merely warned about. |
| Encoding | JSON, `application/json`, UTF-8 |
| Compression | `Content-Encoding: gzip` on batches above ~4 KB |
| Auth | `Authorization: Bearer <device token>` (§3) |
| Versioning | `/api/v{n}/client/...`, with `n` negotiated per server (§2.1) |

All timestamps are **RFC 3339 with an explicit offset**, and are **server time** — the client applies
its measured clock offset before sending (§5). VRChat's own log has no timezone at all, which is the
problem this rule exists to contain.

### 2.1 Version negotiation happens once per server, at pairing

The client reads `GET /api/version` (unauthenticated, already built), compares the server's
`apiVersionMinimum..apiVersion` against its own supported range, and stores the highest mutually
supported version alongside that server's pairing.

It re-negotiates when a request returns `409 Conflict` with `code: "api_version_unsupported"` —
which is what a server returns after being upgraded past the client's range, rather than failing
requests in a way the client would read as transient.

**No content negotiation headers, no per-request version.** The version is a property of the pairing.

---

## 3. Authentication

**Device tokens**, per M3 §4: ingest-scoped, one per device **per server**, individually revocable.

### 3.1 Pairing

```
  moderator, in a browser          client                          server
  ───────────────────────         ──────                          ──────
  opens <server>/pair, signed in
  page asks for a code       ─────────────────────────────▶  POST /api/client-devices/pairing-code
                             ◀─────────────────────────────  { code, expiresAt }   (cookie auth)
  page builds the pairing token:
    base64url {"server": origin, "code": code}
  "Open in Modbot" =
    modbot-client://pair?token=…  ──▶  Windows starts the client
                                       with the link; a running copy
                                       receives it over a local pipe
                                  client checks the token, then
                                  POST /api/v1/client/pair
                                  { code, clientVersion, platform }
                                                            ──▶  validates code
                                                            ◀──  { deviceToken,
                                                                   managedGroupId,
                                                                   serverTime }
```

- The **code** is short, single-use and lives five minutes; the **token** is long and never
  displayed. A token that a human has to read or retype ends up in a Discord message.
- **The pairing token is the code plus the address, and nothing longer-lived.** It travels in a
  URL, and URLs land in browser history, shell logs and Windows' record of protocol launches. A
  code found there later is worthless; a device token would not be. `server` is the browser's own
  origin, because the address the moderator reached the page at is one that reaches the server,
  while a server behind a proxy often does not know its own public name.
- **The same token has a second route.** "Copy pairing token" on the page and a paste box in the
  client carry the identical bytes, for a browser that will not hand a `modbot-client://` link to
  another program. One code path in the client; the link is unwrapped and then treated as a paste.
- **The client checks the token before any request.** An `https` origin only (plain `http` to
  loopback is the one exception, for testing on the same PC), no path, query, fragment or user
  name; a code that looks like a code; a size bound checked before decoding. A link is untrusted
  input from wherever the browser got it.
- **There is no device name.** A moderator's own label for their machine told the operator
  nothing they could act on; whose device it is (from the code), platform, version and last-seen
  answer every question the settings list is asked. Older clients that still send `deviceName`
  are not refused — the field is ignored.
- **Pairing starts from the client too.** "Pair with a server" opens the pairing page, by default
  `https://my.modbot.co/go?redir=/pair` (central services §2), which forwards a signed-in moderator to their
  own server's `/pair`. A group can point the button at its own server through the client's
  optional `settings.json`. The client never needs to know the address; the token carries it.
- The client is **single-instance**. The copy Windows starts to deliver a link hands it to the
  running copy over a named pipe (current user only, bounded, one message per connection) and
  exits. The scheme is registered under `HKCU\Software\Classes\modbot-client` on every start —
  per-user, no elevation, and the only registry key the client touches. **Confirmed working on
  Windows 11 on 2026-09-14**: a `modbot-client://` link from a browser reaches a running client,
  which is the one part of this design that could not be proved by a test and had to be tried.
- `managedGroupId` comes back at pairing because the client needs it to route events **locally**
  without asking anyone (M3 §5.5.1). Asking a server "do you own this instance?" is itself the leak
  the routing rule exists to prevent.
- `serverTime` seeds the clock offset (§5) so the first report is already corrected.

### 3.2 Token handling

Stored per server using DPAPI (`CurrentUser` scope), never in plaintext. Revocation is server-side
and immediate; the client treats `401` as terminal for that pairing and surfaces it rather than
retrying — a revoked moderator's client must stop, visibly.

---

## 4. Ingest

### 4.1 One endpoint, one batch

```http
POST /api/v1/client/events
Authorization: Bearer <device token>

{
  "batchId": "0f1c…",              // client-generated UUID, stable across retries
  "clientVersion": "2026.9.0",
  "clockOffsetMs": -412,           // the client's own measured correction (§5)
  "clockConfidence": "good",
  "events": [ … ]                  // 1..500
}
```

Response:

```json
{ "accepted": 37, "deduplicated": 11, "rejected": [ { "index": 4, "reason": "unknown_group" } ] }
```

**Partial acceptance is the normal case, not an error.** A batch where 11 of 48 events were already
reported by another moderator's client is a completely successful request — deduplication is
expected (M3 §5.1), not a failure to report.

### 4.2 Event shape

```json
{
  "clientEventId": "b7e2…",        // stable across retries; the idempotency key
  "type": "InstanceJoined",        // | InstancePresenceObserved | InstanceLeft | AvatarChanged | LogStopped
  "occurredAt": "2026-09-12T20:14:07.412+00:00",
  "occurredBefore": null,          // non-null ⇒ it happened somewhere in (occurredAt, occurredBefore]
  "subjectId": "usr_…",            // opaque; never validated for shape (foundation §3.1.1)
  "worldId": "wrld_…",
  "instanceId": "39911",
  "groupId": "grp_…",              // the routing decision, made locally
  "data": { }                      // type-specific, small
}
```

`InstancePresenceObserved` is the one that stops phantom bursts becoming fake joins (M3 §7.1): it
means *this person was here when I arrived*, arrival time unknown and earlier. It carries no
`occurredBefore` upper bound because there isn't one — the lower bound is unknown, not the upper.

`LogStopped` (added 2026-09-14) means *VRChat's log stopped growing while I was in this instance*.
The subject is the moderator themselves, `occurredAt` is VRChat's timestamp on the last line the
log wrote, and `data` carries the moderator's display name when it is known. The server stores it
as `vrchat.instance.log-stopped` and uses it to end that moderator's watch of the room (M3 §7.4).

- **Sent once per stop.** The client notices after `PresenceObserver.InstanceStaleAfter` (two
  minutes, about twelve of the frame-rate lines VRChat writes every ten seconds — research note
  §1.0) and says so once. It says nothing more while the log stays stopped.
- **Not a heartbeat.** Nothing is sent while the log is growing. The rejected "still here" report
  stays rejected, for the reason recorded on the server's `DeviceLocations`: a moderator's
  whereabouts are a by-product of what they observe, never something reported on a timer.
- **Only for a stop worth reporting.** The log must have grown while the client was running (a
  file that was already dead at startup is last night's session), the moderator must be settled
  in an instance, and their own id must be known.
- **When the log starts again** in the same file — a slept laptop, a paused VM — the client sends
  its current roster once as `InstancePresenceObserved`, dated at the first new line, moderator
  included. The server ended the watch at the stop, and this is how it learns the watch started
  again. A new file is a new session and is not restated.

#### 4.2.1 Old servers and new event types

A server that does not recognise a `type` rejects **that event** as `malformed_event` in the
`rejected` list and accepts the rest of the batch with a `200`. The batch is never refused as a
whole over one event, because `type` is read as a string rather than an enum.

The client treats a `200` as the server's final word on every event in the batch — accepted,
already known or refused — and removes all of them from its buffer. So a new client talking to an
old server loses exactly the `LogStopped` lines, retries nothing, and reports everything else as
before. An old client talking to a new server simply never sends the event: its moderators' watches
end on their own leave, on the room closing, or on their presence showing up somewhere else.

### 4.3 Idempotency is the client's job, deduplication is the server's

Two different mechanisms, often confused:

| | Scope | Handles |
|---|---|---|
| **`clientEventId`** | one client | the same client retrying after a timeout |
| **Windowed dedup** (§5.7.1) | across clients | six moderators reporting one join |

A retried batch is safe because every event carries a stable id. A batch from a *different* moderator
observing the same join is deduplicated by the ±5 s window. Neither substitutes for the other, and
the server applies both.

### 4.4 Batching and backoff

- Send when the buffer reaches ~50 events, **or** 30 seconds have passed, **or** 2 seconds have
  passed since the oldest unsent arrival, departure, "already here" or `LogStopped` was buffered —
  whichever comes first. Instance entry produces a burst and goes at once; people coming and going
  go within seconds, grouped with anything that lands in the same two seconds; an idle instance
  sends nothing extra, because nothing is waiting. Avatar changes do not start the two-second wait.
  *(The two-second rule was added 2026-09-14 for the Live page, which a thirty-second wait made half
  a minute stale. The client's loop ticks once a second, so "within about two seconds" is two to
  three in practice.)*
- Cap batches at 500 events, and the on-disk buffer by both size and age (M3 §5.3).
- Retry with exponential backoff and jitter, capped at ~5 minutes.

**This backoff is unrelated to §4.3's cold-stop rule**, and the distinction matters: §4.3 governs
*VRChat's* punitive limiter, where retrying extends the penalty. A Modbot server is ordinary software
under the operator's control, so ordinary backoff is correct here. Do not copy the cold-stop logic
across.

- On `429` from the *server*, honour `Retry-After` — Modbot sends it even though VRChat does not.

---

## 5. Clock synchronisation

The client corrects to server time before sending (foundation §4.4), using the SNTP round-trip
estimate:

```http
GET /api/v1/client/time  →  { "serverTime": "2026-09-12T20:14:07.412+00:00" }

offset = ((t1 − t0) + (t2 − t3)) / 2
```

- Re-measured on pairing, on reconnect, and every few hours. Smoothed across samples; outlying
  round-trips discarded rather than averaged in.
- `clockOffsetMs` and `clockConfidence` ride on every batch, so the **server can see how much to
  trust the timestamps** instead of guessing.
- The client never steps the machine clock. The offset applies to reported timestamps only.
- The server records its own `observedAt` regardless, which is authoritative for ordering.

This is what makes the ±5 s dedup window viable at all: against raw machine clocks the window would
have to exceed worst-case skew, which would swallow the 15-second genuine rejoin it must preserve.

---

## 6. Overlay reads

The overlay renders from the client's **local cache**, never a live request (M3 §6.0.3). The client
keeps that cache warm.

```http
GET /api/v1/client/context?instanceId=…   →  roster with flags, prior-action counts, staff markers
GET /api/v1/client/user/{subjectId}       →  profile summary for one person
```

Both are **read-only and small**. The profile summary is deliberately not the full web profile: an
overlay card shows prior actions, roles, join date and current flags, and nothing that needs
scrolling in a headset.

**The roster is only as good as the watch** (M3 §7.4, changed 2026-09-14). It lists everyone present
from facts reported during the room's current watch, and nobody at all when no moderator is
watching. It used to be "last fact per person wins" over twelve hours, which kept everyone the last
moderator saw "present" for up to twelve hours after they left, because nobody is told that anyone
else left once the last moderator walks out.

### 6.1 Push, only where it earns it

One case genuinely needs push: **a flagged user joins the instance a moderator is currently in.** By
the time a 30-second poll notices, the moment has passed.

That is a **long-poll** (`GET /api/v1/client/alerts?wait=30`), not a websocket — one endpoint, one
concern, and it degrades to a slow poll rather than to nothing when a proxy interferes.

**Everything else polls.** Roster refresh, flag updates and health all ride the normal batch cycle.

---

## 7. Errors

| Status | Meaning | Client behaviour |
|---|---|---|
| `200` | Accepted, possibly partially | Drop accepted events from the buffer |
| `400` | Malformed batch | **Do not retry.** Log, drop, alarm — a retry loop on a permanent error is how buffers fill forever |
| `401` | Token invalid or revoked | Stop this pairing, surface it to the moderator |
| `409` | API version unsupported | Re-negotiate (§2.1) |
| `413` | Batch too large | Halve and retry |
| `429` | Server rate limit | Honour `Retry-After` |
| `5xx` | Server trouble | Backoff and retry; keep buffering |

Every error body is `{ "code": "...", "message": "..." }` with a stable machine-readable `code`.
**The client branches on `code`, never on `message`** — message text is for humans and will be
reworded.

---

## 8. What this protocol deliberately does not do

- **No server→client commands.** The server never tells the client to do anything. The client
  observes and reports; it does not act (M3 §10). This keeps the client's behaviour fully described
  by its own source, which is what foundation §3.2's comment rule promises a suspicious reader.
- **No moderation actions.** A moderator acting from the overlay goes through the normal
  authenticated API as themselves, not through the device token — an ingest token cannot ban anyone
  (M3 §4).
- **No log content.** Parsed events only, never raw lines (M3 §3.1). *(2026-09-15: still true. The
  client also sends these same parsed events, for every instance, straight to Modbot Cloud as a
  backup — `2026-09-15-cloud-log-backup-design.md`. That is a separate flow, set only on the client's
  PC, and not part of this protocol.)*
- **Nothing about Modbot Cloud.** A server never tells its clients where their Cloud backup goes or
  whether to send it. *(2026-09-15: a revision that day added a `cloud` object and `instanceId` to
  `GET /client/time` for exactly that; they were removed the same day, before any release, and the
  answer is back to `serverTime` alone (§5). The fields were only ever additions a client could
  ignore, so the API version is unchanged.)*
- **No cross-group data.** A pairing sees exactly one group's context.

---

## 9. Open questions

1. **Long-poll timeout tuning** against real proxies and load balancers, which often cap idle
   connections below 30 s.
2. **Batch size in practice.** 50 events / 30 s / 2 s is a guess; the right numbers come from a
   busy instance with six moderators, not from reasoning.
3. **Whether `clientEventId` should be content-derived** (a hash of the logical event) rather than
   random. Content-derived would make retry idempotency work even across a client restart that lost
   the buffer's ids — worth it if restarts during a pending batch prove common.
4. **Overlay alert delivery when several servers all have something to say at once.** Rare, but the
   headset has room for one card.
