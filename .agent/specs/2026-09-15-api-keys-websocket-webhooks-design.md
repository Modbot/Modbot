# Modbot — API Keys, the Live Event WebSocket and Webhooks

- **Date:** 2026-09-15
- **Status:** Implemented with this document
- **Covers:** API keys and how they authenticate against the existing `/api` surface; the event
  envelope; the live event WebSocket and long polling; outgoing webhooks, their signing, retries and address
  blocking; Settings → API
- **Implements:** foundation §6.3 (`ApiKey`), §7.3 ("read API access uses `ApiKey`"); accounts and
  access design §9 ("API keys", deferred there)
- **Related:** foundation §4.4 (one clock), §5.3 (facts), §5.5 (retention), §5.9.2–§5.9.4 (what is
  recorded, secrets are never the payload, the two logs); accounts and access design §3, §5;
  `.agent/docs/api.md`

---

## 1. What this adds

Everything Modbot knows is already behind `/api`, but only a person with a browser session can
reach it. A group that wants a bot of its own, a spreadsheet, or a second Discord integration has
no way in. This design adds four doors, all opening onto what already exists:

1. **API keys** — a credential for a program, used on the same endpoints the web app uses.
2. **The live event WebSocket** — every new fact, as it is written, to a connected program.
3. **Long polling** — the same stream by repeated requests, for a program that can do neither of
   the others (§5.6).
4. **Webhooks** — the same events, sent by Modbot to an address the operator registers.

## 2. What is settled, and what this narrows

| Earlier decision | Here |
|---|---|
| Foundation §7.3: "Read API access uses `ApiKey`, scoped separately from user sessions" | **Narrowed.** There is no separate read API. A key is scoped by the same permission bits a role is, and is accepted by every `/api` endpoint through the same authorisation (§3.4). A second, parallel set of endpoints would drift from the first the week it shipped. |
| `ModbotPermissions.ManageApiKeys` = bit 7, reserved since M0 | **Used as is.** No new bit. It covers webhooks too, and its label says so (§3.6). |
| Accounts and access §5: every request checks the account once | **Extended to keys.** A key is checked on every request, and so is the account that made it. |
| Foundation §5.9.2: "API key created/revoked" is an Auth event | Unchanged, and now written. |
| Discord moderation log: a cursor that is a fact id | **Reused** for the WebSocket and webhooks, with one addition: a rule for ids that commit out of order (§4.2). |

## 3. API keys

### 3.1 Shape

`api_key`: id, name, the visible start of the key, SHA-256 of the whole key, a permission
bitfield, the account that made it, created, optional expiry, last used, revoked (when and by
whom).

The key is `mbk_` followed by 32 random bytes in base64url. **Only the hash is stored.** The key is
shown once, in the response that created it, and never again. The first twelve characters are
stored in the clear so a list of keys can say which one is which — twelve characters of a
256-bit secret give nothing away.

A plain SHA-256 rather than a password hash, because the input is 256 random bits: there is no
dictionary to slow down, and a slow hash on every API request would be a cost with no benefit.
Lookup is by hash, on a unique index.

### 3.2 Permissions, and the cap

A key carries a set of permissions chosen at creation. **The set can be no more than the creating
account holds** — the same rule roles follow (accounts and access §3), for the same reason: a
permission to make keys must not be a permission to make yourself an administrator.

The cap is applied **again on every request**: what a key may do is its own permissions narrowed
to what its account holds *now*.

| Account holds Administrator | Key holds Administrator | Key may do |
|---|---|---|
| yes | — | exactly the key's permissions |
| no | yes | everything the account holds (the account lost Administrator after the key was made) |
| no | no | the permissions both hold |

So a moderator demoted on Tuesday does not keep Monday's powers through a key. A key whose account
is disabled, gone, or no longer linked to a VRChat account authenticates as nobody.

### 3.3 What a key cannot do

Some endpoints are about a *person*, not a program, and refuse a key whatever it holds:

- `/api/auth/*` — changing a password, a username, contact details, signing out everywhere,
  linking a VRChat account — except `GET /api/auth/me`, which answers "whose key is this".
- `/api/onboarding/*` — the setup wizard.
- `POST /api/companion-devices/pairing-code` — pairing a companion is a moderator's own decision
  on their own machine (M3), and a key that mints pairing codes turns one credential into another.
- `/api/v{n}/client/*` — the companion has its own device tokens (M3), and the two are never
  interchangeable.

The list lives in one place (`ApiKeyAuthentication.KeysMayNotUse`). A key sent to any of these is
a 401.

Beyond that the permission bits decide. A key cannot manage keys, users, roles or settings unless
it was given `ManageApiKeys`, `ManageUsers`, `ManageRoles` or `ManageSettings` — which the cap
means its creator must hold.

### 3.4 How a request is authenticated

The default authentication scheme becomes a small forwarding scheme. A request carrying
`Authorization: Bearer mbk_…` goes to the key handler; everything else goes to the cookie handler
exactly as before. The key handler builds **the same principal a session has** — account id,
account name, permissions claim, VRChat-linked claim — plus one claim naming the key. `RequiresFlag`,
the default policy and every hand-written check (`AuditVisibility`, the case-file checks) then work
unchanged, because they read claims and never ask which door the caller came through.

Things a key's request does are therefore attributed to the account that owns it, which is what
§5.8 needs: a person answers for what their key did.

The key handler answers a missing or bad key with 401 and `WWW-Authenticate: Bearer`. It never
says whether a key exists, was revoked or expired.

### 3.5 Last used

Written at most once a minute per key, so a busy integration does not turn every read into a
write.

### 3.6 Who manages keys

`ManageApiKeys` ("Manage API keys and webhooks"):

| Operation | Endpoint |
|---|---|
| List every key, with owner and state | `GET /api/api-keys` |
| Create a key for yourself | `POST /api/api-keys` |
| Revoke any key | `DELETE /api/api-keys/{id}` |

Anyone holding the permission sees and may revoke every key. Revoking only ever removes access, so
there is nothing to guard; seeing that a key exists (never the key) is what the permission is for.
Keys are created only for the caller. Revoked and expired keys stay in the list, marked, and are
never deleted: facts reference them.

### 3.7 Facts

`modbot.apikey.create` and `modbot.apikey.revoke`, subject the key's id on the Modbot platform,
actor the account, payload name, visible start and (for create) permission names and expiry.
Never the key or its hash (§5.9.3). Operational log, kept forever.

## 4. Events

### 4.1 An event is a fact

The WebSocket and webhooks send facts, as the fact log holds them. There is no second event system
and no event that is not a fact: anything a program can be told about is also in the audit log,
with the same id.

### 4.2 Reading new facts: the cursor, and ids that arrive out of order

The cursor is a fact id: "every fact after this one". Fact ids come from one sequence, but **a
lower id can commit after a higher one** — two transactions take ids 100 and 101, and 101 commits
first. A reader that saw 101 and moved past it would never see 100.

`FactFeed` handles this without a lock or a second table:

- Facts are read in id order after the cursor.
- While ids are consecutive, they are delivered at once.
- At a missing id, reading stops — unless the fact *after* the gap was observed more than
  **ten seconds** ago. The missing id was taken before that fact was written, so a transaction
  still open after ten seconds is the only way to lose it; a gap that old is a rollback or a failed
  insert, and is stepped over.

Gaps are rare (rollbacks, a failed insert), so the ordinary case costs nothing. Both times come
from `IModbotClock` (foundation §4.4), which is also what lets a test step over a gap.

### 4.3 Who sees what

The same two logs as the audit log (foundation §5.9.4, `AuditVisibility`): moderation facts need
`ViewAuditLog`, operational facts need `ViewOperationalLog`, Administrator sees both.

**One narrowing for a live feed.** Presence — `vrchat.instance.*`, `vrchat.avatar.*`,
`discord.voice.*` — additionally needs `ViewLiveRooms` (bit 20). The audit log shows an instance
join an hour later to anyone with `ViewAuditLog`; a live feed says where somebody is standing *now*,
which M3 §7.4 made its own permission for exactly that reason.

Visibility is decided per fact, from the permissions held **at that moment**, so a permission
removed mid-connection stops the next fact.

### 4.4 The envelope

Version 1. JSON, snake_case:

```json
{
  "version": 1,
  "id": "81234",
  "cursor": "81234",
  "type": "vrchat.group.member.ban",
  "type_raw": null,
  "label": "Banned",
  "category": "moderation",
  "source": "AuditLog",
  "occurred_at": "2026-09-15T14:32:07+00:00",
  "occurred_before": null,
  "observed_at": "2026-09-15T14:32:11.204+00:00",
  "subject": { "platform": "VRChat", "id": "usr_…", "kind": "Person" },
  "actor": { "platform": "VRChat", "id": "usr_…", "name": "Alice" },
  "world_id": null,
  "instance_id": null,
  "data": { }
}
```

- `id` and `cursor` are strings: fact ids are 64-bit, which a JavaScript number cannot hold. They
  are equal today; `cursor` is what a client stores and sends back, and is documented as opaque so
  it can change shape in a later version without breaking anyone.
- `actor` is null when nobody caused it. `name` is the name recorded in the fact at the time, or
  null — never looked up now.
- `data` is the fact's payload verbatim. Secrets are never in it by construction (§5.9.3).
- A new field may be added to version 1. Removing or renaming one is version 2.

### 4.5 Choosing events

A subscription names **types**: an exact type (`vrchat.group.member.ban`), a prefix
(`vrchat.group.member.*`), or `*` for everything visible. An optional **subjects** list narrows to
facts about those ids. Ids are opaque and never checked for shape (§3.1.1).

## 5. The live event WebSocket

### 5.1 Connecting

`GET /api/events/ws`, upgraded. Two ways to authenticate:

- **A key in the `Authorization` header**, for programs.
- **A ticket** in `?ticket=`, for browsers, which cannot set headers on a WebSocket. A ticket comes
  from `POST /api/events/tickets`, made with a key or a signed-in session. It lasts sixty seconds,
  works once, and stands for the caller who asked for it.

**A key is never accepted in the query string** — query strings are written to access logs,
proxies and browser history. **A session cookie alone is not accepted either**: browsers send
cookies with a WebSocket handshake from any site, so a cookie-authenticated socket would let any
page a moderator visits read the log (cross-site WebSocket hijacking). The ticket is a POST, which
the cookie's `SameSite=Lax` does not send cross-site.

Authentication problems are reported **after** the upgrade, as a close code, because a browser
shows a refused handshake only as "1006" with no reason.

### 5.2 Messages

Every message is one JSON text frame with a `kind` (server) or `op` (client).

Server → client:

| `kind` | When |
|---|---|
| `hello` | On connect: protocol version, heartbeat interval, the permissions in force |
| `subscribed` | After a subscribe: the types and subjects in force, and the cursor reading starts after |
| `event` | `{ "kind": "event", "event": <envelope> }` |
| `heartbeat` | Every thirty seconds, with the current cursor — which moves past facts the filter skipped, so a resume does not re-read them |
| `notice` | `history_trimmed`: the cursor points before the oldest fact still kept (§5.4) |
| `pong` | Answer to `ping` |
| `error` | A message the server could not use; the connection stays open |

Client → server:

| `op` | |
|---|---|
| `subscribe` | `{ "op": "subscribe", "types": [...], "subjects": [...], "cursor": "81234" }`. Must arrive within ten seconds of connecting. A second subscribe replaces the filter; its `cursor`, if given, moves the reading position. |
| `ping` | |

### 5.3 Close codes

| Code | Reason |
|---|---|
| 4000 | No subscribe in time |
| 4001 | Not authenticated (no key, bad key, used or expired ticket) |
| 4003 | The caller can see no events, or lost access while connected (key revoked or expired, account disabled) |
| 4008 | Too slow: a send did not complete in ten seconds |
| 4029 | Too many connections for this key or account |

### 5.4 Resuming

Without a cursor, a subscription starts **from now**, like the Discord log channel. With one, it
reads every fact after it — a page of 200 at a time, sent as fast as the client reads them — then
carries on live. The furthest back a cursor reaches is the oldest fact retention has kept (§5.5).
A cursor before that gets a `history_trimmed` notice and continues from the oldest kept fact.

### 5.5 Limits and back-pressure

- **Five connections per key**, and five per account for ticket connections. Held in memory; a
  restart forgets them, and so do the connections.
- **Heartbeat** every thirty seconds as a message, because browsers do not expose WebSocket pings;
  the server also sends protocol pings and closes a connection that stops answering them.
- **Access is re-checked** at every heartbeat.
- **No buffer.** Each connection reads the next page of facts only after the last one was sent, so
  a slow client cannot make the server hold anything. A send that has not finished in ten seconds
  ends the connection; 4008 is sent if the socket can still carry it, though a client that has
  stopped reading will usually just see the connection drop. The fact log is the buffer; the
  client reconnects with its cursor.
- **Woken, not timed.** A caught-up connection waits on `FactSignal`, a process-wide pulse. The
  fact writer pulses after each insert; because that can be before the insert's transaction
  commits, `FactFeedWatcher` also reads the newest fact id every half second -- one read for the
  whole process, not one per connection -- and pulses when it moves. Each connection still re-reads
  every three seconds if nothing pulses, so a missed pulse costs latency, never an event.

> **Revised 2026-09-15.** Connections first polled the log once a second each. Long polling
> (§5.6) made per-request timers a real cost -- sixty idle polls would be sixty reads a second --
> so both now wait on the signal.

### 5.6 Long polling

Added the same day, for a program that can neither hold a socket open nor receive webhooks.

`GET /api/events/poll?cursor=&types=&subjects=&wait=&limit=`

- **A key in the `Authorization` header only.** Not in the query string, and not a session cookie
  alone, for the WebSocket's reasons (§5.1). There are no tickets: a browser has the socket.
- **Same reader, same rules.** `FactFeed` with its gap rule, the same filter syntax (§4.5) and the
  same visibility (§4.3, including `ViewLiveRooms` for presence). A poll can be sent nothing the
  WebSocket would not send the same key, and miss nothing it would.
- **Answer at once or wait.** Events after `cursor` come back immediately, up to `limit` (default
  100, at most 500). With none, the request waits on the signal until one arrives or `wait` seconds
  pass (default 30, at most 60), then answers with an empty list. Access is checked again after
  every wake.
- **The answer** is `{ events, cursor, more, notice }`. `cursor` is where to carry on and moves past
  events not for this caller, as the WebSocket heartbeat's does -- so an empty answer can still move
  it, when everything read was filtered out; with nothing read it stays put. `more` is true when a
  wanted event did not fit, or reading stopped at a full page. A read looks at most ten pages past
  unwanted events before answering, so one poll against a long filtered backlog is bounded.
  `notice` is the WebSocket's `history_trimmed` notice.
- **No cursor** is from now.
- **Limits.** A poll holds one of its key's connection places **for as long as it runs, shared with
  the key's WebSocket connections** -- five in all -- rather than a separate cap: a key that may hold
  five live connections gains nothing by being allowed five more that do the same job. Past that,
  `429` with `Retry-After: 5`. A client that goes away cancels the request, which ends the wait and
  gives the place back at once.
- **`Cache-Control: no-store`.**
- **How long a request may run.** Kestrel has no request duration limit and Modbot adds no
  request-timeout middleware. Railway's edge lets an HTTP request run up to five minutes without
  data and fifteen with it, so sixty seconds is well inside, and the maximum stays at sixty.

## 6. Webhooks

### 6.1 Shape

`api_webhook`: name, address, event types and subjects (§4.5), on/off, the signing secret
(encrypted, §8.3 of the foundation — it has to be read back to sign), the account that set it up,
and delivery state: the cursor, when the current run of failures began, the next attempt, the last
error, why it was turned off.

`api_webhook_delivery`: the last fifty attempts per webhook — when, event, HTTP status, how long,
error.

### 6.2 Who

`ManageApiKeys`. Anyone holding it sees every webhook, may turn any off, and may delete any.
**Only the account that set a webhook up (or an Administrator) may change its address, events,
secret, or send a test**, because a webhook delivers what *its owner* may see (§6.4) — letting
somebody else repoint it would hand them the owner's view of the log.

### 6.3 Delivery

- One event per request: an HTTPS `POST` of the envelope (§4.4), `Content-Type: application/json`.
- **In order, at least once.** A webhook's events go one at a time in fact order; the cursor moves
  only after a delivery succeeded or was given up on. A crash between sending and saving sends that
  event again. Receivers de-duplicate on `Modbot-Event-Id`.
- A new webhook starts **from now**. One turned back on carries on from where it stopped.
- Ten-second timeout. Redirects are not followed.
- **The VRChat egress proxy is not used** (foundation §2.3.1: that proxy exists for VRChat's WAF,
  and a receiver is not VRChat).
- A webhook whose owner account is disabled, gone or unlinked is turned off, with that as the
  reason.

### 6.4 What a webhook may send

The events its types and subjects choose, narrowed to what **its owner** may see now (§4.3).

### 6.5 Signing

Headers on every request:

| Header | |
|---|---|
| `Modbot-Event-Id` | The envelope's `id` |
| `Modbot-Event-Type` | The envelope's `type` |
| `Modbot-Timestamp` | Unix seconds when this attempt was signed |
| `Modbot-Signature` | `v1=` and lowercase hex HMAC-SHA256 |

The signature is over `"{timestamp}.{body}"`, with the secret's UTF-8 bytes as the key. The
timestamp is in the signed text so a captured request cannot be replayed later with a new one;
receivers should refuse a timestamp more than five minutes old. The `v1=` prefix leaves room to
change the scheme and to send two signatures while a secret is being rolled.

The secret is `whsec_` and 32 random bytes in base64url, shown once at creation and once each time
it is rolled.

### 6.6 Retries

| Answer | What happens |
|---|---|
| 2xx | Delivered. The failure run is cleared. |
| Network error, timeout, 5xx | Retried with backoff: 10 s, 30 s, 90 s, 4.5 min, 13.5 min, 40 min, then hourly |
| 408, 429 | Retried; `Retry-After` is honoured, up to an hour. This is the receiver's rate limit, not VRChat's — foundation §4.3.1's "never retry a 429" is about VRChat and does not apply here. |
| Any other 4xx, a redirect | Not retried. The event is skipped and the attempt logged. |

Every answer that is not a 2xx counts toward the failure run. **A webhook failing continuously for
24 hours is turned off**, with the reason ("Failing since …: last error") shown in the list and
recorded as a fact. A receiver that answers 404 for a day is as gone as one that does not answer.

Backoff times are measured on `IModbotClock`, so a test drives a day of failures by moving the
clock.

### 6.7 Addresses Modbot will not send to

By default a webhook cannot reach loopback, private, link-local, carrier-grade NAT, multicast,
unspecified or reserved addresses (IPv4 and IPv6, including IPv4-mapped IPv6). Otherwise anyone
who can manage webhooks could make Modbot's server call its own internal network — the database's
admin port, a cloud metadata address — and read the answer's status in the delivery log.

The check happens **when the connection is made**, on the address the name resolved to, not only
when the address is saved. Checking the name at save time alone is beaten by a name that resolves
to a public address on Monday and `169.254.169.254` on Tuesday.

An operator setting, **Allow private addresses** (`ManageSettings`), turns this off for self-hosted
receivers on the same network. With it on, plain `http` addresses are accepted too; with it off,
only `https`.

### 6.8 Facts

`modbot.webhook.create`, `.change`, `.secret.change`, `.delete`, and `.disable` (turned off after
failing; actor none). Subject the webhook's id, platform Modbot. Payload the name, address and event
types — never the secret. Operational log, kept forever. **Deliveries are not facts**: a busy
webhook would write one per event forever, and the delivery log answers every question a person
asks about them.

## 7. Incoming webhooks — not built

An incoming webhook is a Modbot URL an outside service posts to. Nothing Modbot does today needs
one that an API key does not already provide: a service that can make an HTTP request can send
`Authorization: Bearer` and call the endpoint for what it wants done, with the permission check,
the facts and the attribution that endpoint already has. An incoming-webhook URL is a credential
in a URL — logged by every proxy in between — with an ad-hoc payload format per sender. If a
specific sender that cannot set a header turns up (some SaaS products only do signed POSTs), it
gets its own endpoint then, verifying that sender's signature.

## 8. What is logged

| What | Where |
|---|---|
| Key created, revoked | Fact |
| Webhook created, changed, secret rolled, deleted, turned off after failing | Fact |
| Allow private addresses changed | Fact (`modbot.settings.change`) |
| Each webhook delivery attempt | `api_webhook_delivery`, last fifty |
| WebSocket connect, disconnect and close reason | Serilog, never facts — connection churn is noise (§5.9.2 "System") |
| A request made with a key | Last used on the key; nothing per request |

## 9. Settings → API

Three sub-tabs, `#api/keys`, `#api/webhooks`, `#api/events` (`#api/websocket` still opens the last,
which was its name before long polling joined it):

- **Keys** — the list (name, start of key, owner, permissions, created, last used, expiry, state),
  *Create key* with a name, expiry and permission checklist limited to what the person holds, the
  key shown once with a copy button, *Revoke*.
- **Webhooks** — the list with state and last error, create and edit (name, address, event types,
  subjects, on/off), the secret shown once, *Roll secret*, *Send test*, the delivery log, and the
  *Allow private addresses* switch.
- **Events** — the WebSocket and long polling addresses, and a WebSocket test view: an optional key,
  event types, cursor, *Connect*, and the events as they arrive.

Labels only (CLAUDE.md). The explanations are here and in `.agent/docs/api.md`.

## 10. Deferred

- Recording which key (not only which account) did something in each fact's payload.
- Delivering several events per webhook request.
- Per-key rate limits on the REST API.
- Incoming webhooks (§7).

## 11. Testing

Key hashing, lookup and revocation; expiry; the cap at creation and at use (a demoted creator's key
loses the permission on the next request); keys accepted by existing endpoints with exactly their
permissions and refused on `/api/auth/*`; `ManageApiKeys` gates. The fact feed's gap rule. The
WebSocket over `TestServer`: header key, ticket once only, no cookie-only socket, subscribe and type
filter, subject filter, visibility, resume from a cursor, connection limit, woken by a written fact.
Long polling: an immediate answer, a wait ended by a written fact (and by the watcher for a fact
written without a pulse), an empty answer at the timeout, paging with `more`, filters, visibility,
key-only access, the shared limit and its 429, a dropped client freeing its place, and a late
lower id. Webhook signing checked
against an independent HMAC; retry, backoff, `Retry-After`, 4xx skip and the 24-hour turn-off with a
fake clock and a fake HTTP handler; the address rules and the connect-time block.
