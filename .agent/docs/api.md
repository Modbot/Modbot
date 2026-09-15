# The API: keys, live events and webhooks

Everything the Modbot web app shows comes from Modbot's HTTP API, and a program of your own can use
the same API. There are three ways in:

- **API keys** — call any `/api` endpoint from a script or bot.
- **The live event WebSocket** — stay connected and receive every new event as it happens.
- **Long polling** — the same events, by asking in a loop, when a WebSocket or webhook will not do.
- **Webhooks** — Modbot sends each new event to an address you choose.

All of them live under **Settings → API**. Managing them needs the **Manage API keys and webhooks**
permission.

This is your own deployment's API. There is no central Modbot service: every address below starts
with your Modbot's own address, for example `https://modbot.example.com`.

The full list of endpoints, generated from the server itself, is at `/api/reference` on your
deployment.

## 1. API keys

### Making one

In **Settings → API → Keys**, click **Create key**, give it a name and tick what it may do. The key
is shown **once**. Copy it then — Modbot keeps only a fingerprint of it and cannot show it again.
If you lose it, revoke it and make another.

A key looks like `mbk_Qm9kYm90IGlzIG5vdCBhIHJlYWwga2V5IGF0IGFsbA`. The list shows the first
twelve characters so you can tell your keys apart.

### Using one

Send it in the `Authorization` header:

```
curl -H "Authorization: Bearer mbk_…" https://modbot.example.com/api/audit
```

Never put a key in an address (`?key=…`). Addresses end up in logs.

### What a key may do

- A key can hold **only permissions you have yourself**.
- On **every request**, a key is limited to what your account holds *at that moment*. If somebody
  removes a permission from you, your keys lose it too. If your account is disabled, your keys stop
  working.
- Anything done with a key is recorded as done **by you**.
- A key **cannot** change your password, username or contact details, sign you out, link a VRChat
  account, run the setup wizard, or pair a desktop client — whatever permissions it holds.
- A key can have an **expiry date**. After it, the key stops working.
- **Revoke** a key in the list and it stops working on its next request.

A request with a missing, wrong, expired or revoked key gets `401`. A request the key has no
permission for gets `403`.

## 2. Events

An **event** is one entry in Modbot's log: a ban, a join, a settings change. The live WebSocket and
webhooks send each new one, in this shape:

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

| Field | |
|---|---|
| `version` | The shape of this message. Fields may be added to version 1; nothing is removed or renamed without a new version. |
| `id` | The event's id, the same one the audit log shows. A string, because it can be larger than a JavaScript number holds. |
| `cursor` | Where you are. Store the last one you handled; send it back to carry on from there. Treat it as opaque text. |
| `type` | What happened, as dotted words: `vrchat.group.member.ban`, `modbot.user.login`. |
| `type_raw` | For `modbot.unrecognised`: the word VRChat used, which Modbot has no name for yet. |
| `label` | A short English label for the type. |
| `category` | `moderation` or `operational` — which of the two logs it belongs to. |
| `source` | Where it came from: `AuditLog`, `SyncDiff`, `Client`, `Discord`, `Manual`, `Modbot`. |
| `occurred_at` | When it happened. |
| `occurred_before` | Null when the time is exact. Otherwise it happened somewhere between `occurred_at` and this — for example, somebody noticed missing between two syncs. |
| `observed_at` | When Modbot learned of it. |
| `subject` | Who or what it happened to. `kind` is `Person`, `Instance`, `Group`, `Role`, `Account` or `Other`. |
| `actor` | Who did it, or null. `name` is the name recorded at the time. |
| `world_id`, `instance_id` | Where, for events in a VRChat instance. |
| `data` | Everything else Modbot recorded about the event. It differs by type. |

Ids (`usr_…`, `wrld_…`, Discord ids) are text. Do not assume a format.

### Which events you receive

The same rules as the audit log:

- Moderation events (bans, joins, roles, case files…) need **See the audit log**.
- Operational events (sign-ins, settings changes, sync problems…) need **See the operational log**.
- Presence — who joined or left an instance, avatar changes, Discord voice — also needs
  **See live instances**.

For a key, that is the key's permissions. For a webhook, it is the permissions of the person who
set it up.

### Choosing events

A subscription (WebSocket) or a webhook names **types**:

| Pattern | Matches |
|---|---|
| `vrchat.group.member.ban` | exactly that type |
| `vrchat.group.member.*` | every type starting `vrchat.group.member.` |
| `*` | everything you may receive |

and, optionally, **subjects**: a list of ids. With subjects, only events about those ids are sent.

`GET /api/events/types` lists the types you may receive, with labels.

## 3. The live event WebSocket

### Connecting

The address is `wss://modbot.example.com/api/events/ws`.

- **From a program:** send your key in the `Authorization: Bearer mbk_…` header on the connection.
- **From a browser**, which cannot set that header: first `POST /api/events/tickets` (signed in, or
  with a key), which answers `{ "ticket": "…", "expiresAt": "…" }`. Then connect to
  `wss://modbot.example.com/api/events/ws?ticket=…`. A ticket works once, within sixty seconds.

A session cookie on its own does not open the socket.

### Messages

Every message is one JSON text frame. The server sends `kind`, you send `op`.

1. The server sends `hello`:
   ```json
   { "kind": "hello", "version": 1, "heartbeat_seconds": 30, "permissions": ["ViewAuditLog"] }
   ```
2. Within **ten seconds**, send `subscribe`:
   ```json
   { "op": "subscribe", "types": ["vrchat.group.member.*"], "subjects": [], "cursor": "81230" }
   ```
   All three fields are optional. No `types` means `*`. No `cursor` means *from now on*.
3. The server answers `subscribed`, with the cursor it will read after:
   ```json
   { "kind": "subscribed", "types": ["vrchat.group.member.*"], "subjects": [], "cursor": "81230" }
   ```
4. Then events, as they happen:
   ```json
   { "kind": "event", "event": { "version": 1, "id": "81234", … } }
   ```

The server also sends:

| `kind` | |
|---|---|
| `heartbeat` | Every thirty seconds: `{ "kind": "heartbeat", "cursor": "81290" }`. The cursor moves past events your subscription skipped, so store it too. |
| `notice` | `{ "kind": "notice", "code": "history_trimmed" }`: your cursor was older than anything this deployment still keeps, so some events are gone. Reading carries on from the oldest one kept. |
| `error` | A message it could not use. The connection stays open. |
| `pong` | The answer to `{ "op": "ping" }`. |

Send `subscribe` again at any time to change what you receive.

### Carrying on after a disconnect

Keep the `cursor` of the last event or heartbeat you handled. When you reconnect, send it in
`subscribe`. You receive every event after it that you may see, then carry on live. How far back
this reaches depends on your deployment's data retention (see `data-retention.md`).

Events arrive at least once and in order. If you reconnect with an older cursor, you will see some
again: use `id` to skip the ones you have handled.

### When the server closes the connection

| Code | Why |
|---|---|
| 4000 | No `subscribe` within ten seconds |
| 4001 | Not authenticated: no key, a wrong key, or a used or expired ticket |
| 4003 | You can receive no events, or your access was removed while connected (key revoked or expired, account disabled) |
| 4008 | Too slow: you stopped reading and a send could not finish in ten seconds |
| 4029 | Too many connections: at most five per key (long polls waiting on the same key count too), or five per account for browser tickets |

## 4. Long polling

If your program cannot keep a WebSocket open and cannot receive webhooks — a script behind a
firewall, a platform that only makes ordinary HTTP requests — it can ask for events in a loop
instead. Each request returns at once when there is something new, and otherwise waits until there
is.

```
GET https://modbot.example.com/api/events/poll?cursor=81230&types=vrchat.group.member.*&wait=30
Authorization: Bearer mbk_…
```

Send your key in the `Authorization` header. A key in the address, or a signed-in browser session
on its own, is refused with `401`.

| Parameter | |
|---|---|
| `cursor` | Where to carry on from: the `cursor` of the previous answer. Leave it out on the first request to start from now. |
| `types` | Event types, as in section 2. Repeat the parameter or separate with commas. Leave it out for everything. |
| `subjects` | Subject ids, the same way. |
| `wait` | How many seconds to wait when there is nothing new. Default 30, at most 60. `0` answers at once. |
| `limit` | The most events in one answer. Default 100, at most 500. |

The answer:

```json
{
  "events": [ { "version": 1, "id": "81234", "type": "vrchat.group.member.ban", … } ],
  "cursor": "81234",
  "more": false,
  "notice": null
}
```

| Field | |
|---|---|
| `events` | New events, oldest first, in the shape from section 2. Empty when the wait ran out. |
| `cursor` | Send this back as `cursor` next time. It also moves past events that were not for you, so an empty answer can still move it on. |
| `more` | `true` when there are more events waiting: ask again straight away. |
| `notice` | `{ "kind": "notice", "code": "history_trimmed", … }` when your cursor was older than anything this deployment still keeps, as on the WebSocket. Otherwise null. |

You receive exactly what the WebSocket would send the same key: the same events, in the same order,
filtered the same way. Events arrive at least once; use `id` to skip any you have handled.

A request that is waiting holds one of the key's five connection places, shared with its WebSocket
connections. A request past that gets `429` with a `Retry-After` header saying how many seconds to
wait. Closing the request gives the place back at once. Answers are sent with
`Cache-Control: no-store`.

A loop in Node.js:

```js
let cursor = null

for (;;) {
  const url = new URL('https://modbot.example.com/api/events/poll')
  url.searchParams.set('types', 'vrchat.group.member.*')
  url.searchParams.set('wait', '30')
  if (cursor) url.searchParams.set('cursor', cursor)

  const response = await fetch(url, { headers: { authorization: `Bearer ${process.env.MODBOT_KEY}` } })

  if (response.status === 429) {
    await new Promise((r) => setTimeout(r, Number(response.headers.get('retry-after') ?? 5) * 1000))
    continue
  }
  if (!response.ok) throw new Error(`Modbot answered ${response.status}`)

  const page = await response.json()
  for (const event of page.events) console.log(event.id, event.type, event.subject.id)
  cursor = page.cursor // store it somewhere that survives a restart
}
```

Or once, with curl:

```
curl -H "Authorization: Bearer $MODBOT_KEY" \
  "https://modbot.example.com/api/events/poll?cursor=81230&wait=30"
```

## 5. Webhooks

A webhook is an address on your own server that Modbot sends events to.

### Setting one up

In **Settings → API → Webhooks**, click **Create webhook** and give it a name, the address, the
event types and (optionally) subjects. The **signing secret** is shown once — copy it into your
receiver. **Roll secret** makes a new one and shows it once; the old one stops being used at once.

**Send test** sends an event of type `modbot.webhook.test` right away and shows what your server
answered. The **delivery log** shows the last fifty attempts.

Only the person who set a webhook up, or an administrator, can change it or send a test. Anyone with
the permission can turn it off or delete it.

### What Modbot sends

One `POST` per event, with the event (section 2) as the JSON body, and these headers:

| Header | |
|---|---|
| `Content-Type` | `application/json; charset=utf-8` |
| `Modbot-Event-Id` | The event's `id` |
| `Modbot-Event-Type` | The event's `type` |
| `Modbot-Timestamp` | When this attempt was signed, in Unix seconds |
| `Modbot-Signature` | `v1=` followed by the signature, in lowercase hex |

Events go **in order** and **at least once**. Answer with any `2xx` status within **ten seconds**.
If your server might see an event twice, use `Modbot-Event-Id` to skip repeats.

A new webhook starts with events from the moment it is created. A webhook turned back on carries on
from where it stopped.

### Checking the signature

The signature is HMAC-SHA256, keyed with the secret, over the timestamp, a full stop, and the raw
body exactly as received:

```
signature = hex( HMAC_SHA256( secret, timestamp + "." + body ) )
```

Check it before trusting anything in the request, compare in constant time, and refuse timestamps
more than five minutes old so a captured request cannot be replayed later.

Node.js:

```js
import { createHmac, timingSafeEqual } from 'node:crypto'

export function isFromModbot(secret, headers, rawBody) {
  const timestamp = headers['modbot-timestamp']
  const header = headers['modbot-signature'] ?? ''
  if (!timestamp || Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false

  const expected = 'v1=' + createHmac('sha256', secret).update(`${timestamp}.${rawBody}`).digest('hex')
  return header.split(',').some((part) => {
    const a = Buffer.from(part.trim())
    const b = Buffer.from(expected)
    return a.length === b.length && timingSafeEqual(a, b)
  })
}
```

Python:

```python
import hashlib, hmac, time

def is_from_modbot(secret: str, headers, raw_body: bytes) -> bool:
    timestamp = headers.get("Modbot-Timestamp", "")
    if not timestamp or abs(time.time() - int(timestamp)) > 300:
        return False
    signed = timestamp.encode() + b"." + raw_body
    expected = "v1=" + hmac.new(secret.encode(), signed, hashlib.sha256).hexdigest()
    return any(hmac.compare_digest(part.strip(), expected)
               for part in headers.get("Modbot-Signature", "").split(","))
```

Use the body **as received**, before any JSON parsing: re-serialising it changes the bytes and the
signature will not match.

### When your server does not answer

| Your server answers | Modbot |
|---|---|
| `2xx` | Moves on to the next event. |
| No answer, a network error, or `5xx` | Tries the same event again after 10 seconds, 30 seconds, 90 seconds, 4½ minutes, 13½ minutes, 40 minutes, then every hour. Later events wait. |
| `408` or `429` | Tries again, waiting as long as your `Retry-After` header asks (up to an hour). |
| Any other `4xx`, or a redirect | Does not try that event again, logs it, and moves on. Redirects are not followed. |

If a webhook has not had a `2xx` for **24 hours**, Modbot turns it off and shows why in the list.
Fix your server, then turn the webhook back on.

A webhook is also turned off if the account that set it up is disabled.

### Private addresses

By default Modbot will not send webhooks to private, loopback or link-local addresses —
`localhost`, `10.x.x.x`, `192.168.x.x`, `169.254.x.x` and the like — and only to `https` addresses.
This stops the webhook form being used to reach machines on Modbot's own network. The check is made
when Modbot connects, on the address the name actually resolves to.

If your receiver runs on the same private network as Modbot, someone with **Change settings** can
switch on **Allow private addresses** on the Webhooks page. That also allows plain `http`.

Webhooks do not go through the VRChat proxy.

## 6. What is recorded

- Creating and revoking a key, and creating, changing, rolling the secret of, deleting or
  automatically turning off a webhook, are recorded in the operational log with who did it. Keys and
  secrets never are.
- Each webhook keeps its last fifty delivery attempts.
- Individual API requests are not recorded; each key shows when it was last used.

## 7. Incoming webhooks

Modbot does not have URLs for other services to post into. A service that needs to tell Modbot
something can use an API key with the endpoint for what it wants done, and gets the same permission
checks and the same record of who did it as the web app.
