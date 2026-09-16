# Modbot Cloud — Event Backup

- **Date:** 2026-09-15 (revised twice the same day; see §0)
- **Status:** Building
- **Covers:** the desktop client's event backup to Modbot Cloud, its settings on the client's PC, and the
  event storage it sends to in Modbot Cloud (`src/Modbot.Cloud`)
- **Depends on:** foundation §4.4 (one clock), §5.3 (fact schema), §5.5 (retention); client protocol
  §4–§5; M3 §3
- **Narrows:** M3 §3.1 and M5.5.1 ("instances outside the managed group are never reported") and central
  services §1.1 ("no facts held centrally"). See §10.

---

## 0. What changed, and why

The first version of this spec sent **every raw VRChat log line** to Cloud. That misread the request.
The maintainer's words:

> "Modbot deployed instances feed structured JSON logs to Modbot Cloud for remote support and backups,
> and Modbot Clients also send their parsed events to Modbot Cloud as a backup instead of just Modbot,
> but it has all instances logged instead of just that group's instances."

So there are two halves of Modbot Cloud, and this spec builds one:

| Half | What | Status |
|---|---|---|
| **Client event backup** | Each desktop client sends the presence events it already parses — the same ones it sends a paired Modbot server — for **every** instance, not only the group's. | This spec. |
| **Server log feed** | Each Modbot deployment sends its own structured JSON logs to Cloud, for remote support and backups. | **Built**, 2026-09-16, in `2026-09-16-logs-alerts-showcase-design.md`. It lives in the engine database beside these events, partitioned by month. |

Raw log lines were never asked for. Nothing in Cloud or the client sends, stores or reads them.

### 0.1 Second correction: who decides where the backup goes

The second version let a paired Modbot server decide for its clients. The server read
`MODBOT_CLOUD_ENDPOINT` and `MODBOT_CLOUD_DISABLED` and handed them to its clients on
`GET /api/v{n}/client/time` as a `cloud` object and an `instanceId`; any paired server saying `disabled`
stopped the client sending, and a client held sending until its paired servers had answered. The
client also had a switch, "Send all logging to Modbot Cloud as backup", on its Settings page. That was
wrong too. The maintainer's words:

> "Modbot Client -> sends directly to cloud.modbot.co all instance events regardless of group to
> cloud.modbot.co, and sends each group's own logs to modbot.group-endpoint.com (whatever their paired
> server URL is). Also remove the setting from UI and add it to settings.json and reading from
> environment variable MODBOT_CLOUD_ENDPOINT (and settings.json) and MODBOT_CLOUD_DISABLED env var"

So:

- The client has **two independent flows**: every instance event, whatever the group, straight to Cloud;
  and each paired group's own events to that group's paired server, unchanged.
- **Nothing about Cloud comes from, or depends on, a paired server.** The `cloud` object and `instanceId`
  are gone from the time answer, and an unpaired client and a paired client behave the same toward Cloud.
- **The switch is gone from the screen.** The client's Cloud settings live only on its own PC:
  `settings.json` and two environment variables (§3.1).
- A server's own `MODBOT_CLOUD_ENDPOINT` and `MODBOT_CLOUD_DISABLED` now mean only where that server talks
  to Cloud for its own purposes, and whether it does (central services §1.1).

## 1. What this is

The desktop client reads VRChat's output log and turns it into presence events: someone joined, was
already here, left, changed avatar, or VRChat's log stopped. Today those go to a paired Modbot server, and
only for that server's group's instances.

With the backup on — and it is on unless the person using the PC turns it off — the client also sends
**the same events, for every instance it sees**, straight to Modbot Cloud. Why: a backup of what the
client observed that does not depend on any one group's server, and later, trends of what happens across
VRChat.

The maintainer's decisions:

- On by default. No onboarding step, no prompt, no first-run dialog, and no switch on the screen.
- Every instance the moderator is in, not only group instances.
- Works the same whether the client is paired with no Modbot server, one, or several. A paired server has
  no say in it.

## 2. What is sent

Each event is exactly the client protocol's event (protocol §4.2), built by the same mapper:

| Field | Notes |
|---|---|
| `clientEventId` | Random, made once, kept through every retry and restart. Cloud's de-duplication key (§4.3). |
| `type` | `InstanceJoined`, `InstancePresenceObserved`, `InstanceLeft`, `AvatarChanged`, `LogStopped`. |
| `occurredAt`, `occurredBefore` | Corrected to Cloud's clock as far as the client has measured it (§5). |
| `subjectId` | The VRChat user id. |
| `worldId`, `instanceId` | The instance, with its `nonce` already thrown away when the location was read. |
| `groupId` | The owning group, or **null** for a public, friends-only, invite or private instance. This is the one difference from what a server gets. |
| `data` | The display name at that moment, and the avatar name for an avatar change. |

The phantom-burst rules apply exactly as for a server (M3 §7.1): what Cloud gets is the client's parsed
facts, not its guesses. Events from log lines that were already in the file when the client started are
not sent, to Cloud or to a server.

Per batch, alongside the events: `batchId`, `clientVersion`, `sentAt` (the PC's own clock when sending,
uncorrected), and `clockOffsetMs` and `clockConfidence` (the client's measured correction to Cloud's
clock). No file name or offset: the event id is enough to de-duplicate. No `modbotServerId` either (§3.3).

**Never sent:** raw log lines, instance nonces, pairing or device tokens, Modbot's own logs, file paths,
the machine name and the Windows account name.

### 2.1 When

- **Events observed while the backup is on are sent.**
- **The settings are read when the client starts** (§3.1). A client that starts with the backup off sends
  nothing, queues nothing, and deletes anything a previous run left queued.
- **Starting again with it on** sends from that moment. Nothing observed while it was off is ever sent.
- A batch closes at **500 events, 256 KB, or 60 seconds after its first event**, whichever comes first,
  and is posted gzipped to `POST /api/v1/events`. A quiet room sends nothing.

## 3. Where it goes, and who decides

### 3.1 The address, and on or off

Decided only on the client's PC, from two places, for each of the two values separately:

| Order | Endpoint | On or off |
|---|---|---|
| 1 | environment variable `MODBOT_CLOUD_ENDPOINT` | environment variable `MODBOT_CLOUD_DISABLED` |
| 2 | `settings.json`: `"cloud": { "endpoint": "…" }` | `settings.json`: `"cloud": { "disabled": true }` |
| 3 | `https://cloud.modbot.co` | on |

```json
{ "cloud": { "endpoint": "https://cloud.modbot.co", "disabled": false } }
```

- `settings.json` is the client's existing settings file, `%APPDATA%\Modbot\settings.json`. The client
  never writes the `cloud` field; a person does.
- `MODBOT_CLOUD_DISABLED`: `1`, `true`, `yes` or `on` is off; `0`, `false`, `no` or `off` is on. Any other
  word is not taken as either, and `settings.json` decides, so a typo never turns sending back on.
- **An endpoint that is not a full address the client will talk to** — HTTPS, or plain HTTP to this PC
  only, the same rule pairing uses — is ignored, the default is used, and the client's log says so.
- **Read once, at start.** The client does not watch `settings.json`, so a change applies at the next start.
- **No paired server is asked.** The time answer carries `serverTime` only (protocol §5). Pairing,
  unpairing, pausing and a server that has not answered change nothing about the backup.
- A server's own `MODBOT_CLOUD_ENDPOINT` and `MODBOT_CLOUD_DISABLED` are about that server (central
  services §1.1) and never reach its clients. The client reads the same names from its own PC's
  environment.

### 3.2 Device identity

On first send to an address the client registers an **install**:
`POST /api/v1/installs { clientVersion, platform }` → `201 { installId, secret, serverTime }`.

- The secret is stored in `%APPDATA%\Modbot\cloud-installs.json`, **encrypted to the Windows account with
  DPAPI** like pairing tokens, under its own key purpose. The install id and address are readable.
- One install per Cloud address. Every batch carries `Authorization: Bearer <installId>.<secret>`; Cloud
  keeps only a SHA-256 of the secret.
- A `401` makes the client forget the install and register again, with backoff.
- Registration is limited to **10 an hour per IP address**. Cloud does not store the address.

### 3.3 The paired server's id

Cloud's batch format still accepts an optional `modbotServerId`, and Cloud keeps it on the install. The
client sends none. It only ever came from the `instanceId` on the time answer, which is gone (§0.1), and
nothing about the backup is tied to a paired server.

## 4. Storage

### 4.1 Two databases

| Variable | Database | Holds |
|---|---|---|
| `DATABASE_URL` | Cloud's main database (`CloudContext`) | installs and secret hashes, admin sessions, settings. Later: accounts, instance registry, term lists, showcases. |
| `DATABASE_ENGINE_URL` | The event storage (`EngineContext`) | backed-up events, per-install clocks, daily and hourly totals, and the server log feed (§0). |

Both are required; Cloud refuses to start and names whichever is missing. Each has its own migrations
(`Data/Migrations`, `Engine/Migrations`) and history table (`__EFMigrationsHistory`,
`__engine_migrations_history`), and each is migrated at start. `/health/ready` checks both.

**No foreign keys between them.** Engine rows name an install by id only. A removed install's events are
**left to retention** (§6).

### 4.2 Tables in the event storage

```sql
client_event                   -- one row per event per install; never updated
  install_id          uuid        not null
  client_event_id     text(64)    not null
  received_at         timestamptz not null   -- Cloud's clock
  sent_at             timestamptz not null   -- the client's clock when it sent
  occurred_at         timestamptz not null   -- when it happened, in Cloud's time (§5)
  occurred_before     timestamptz null
  clock_adjustment_ms integer     not null   -- what Cloud added to the client's own time
  type                text(128)   not null   -- vrchat.instance.join … or modbot.unrecognised
  type_raw            text(128)   null       -- the client's own word, when unrecognised
  subject_id          text(128)   not null
  world_id            text(128)   not null
  instance_id         text(256)   not null
  group_id            text(128)   null
  client_version      text(32)    not null
  data                jsonb       not null
  PRIMARY KEY (install_id, client_event_id)  -- de-duplication
  INDEX (type, occurred_at)                  -- the trends index
  INDEX (install_id, received_at DESC)       -- admin: one install's recent events
  INDEX (received_at)                        -- retention

install_clock      (install_id) PK          -- the clock record, updated per batch
event_day_total    (day, install_id) PK     -- events stored per install per day: the admin chart
event_hour_total   (type, hour) PK          -- events per type per hour: trends
```

- **Types are the server's fact types** (`vrchat.instance.join`, `vrchat.instance.presence`,
  `vrchat.instance.leave`, `vrchat.avatar.change`, `vrchat.instance.log-stopped`), so a trend in Cloud and a
  chart on a server mean the same thing. A type Cloud does not know is kept as `modbot.unrecognised` with
  the client's word in `type_raw` and its data whole (foundation §5.3.1).
- The totals are counters, written in the same transaction as the events and only for rows actually stored.

### 4.3 De-duplication, and why the table is not partitioned

The key is **`(install_id, client_event_id)`**. The client makes an event's id once and writes it to its
outbox with the event, so a retried batch, a batch sent twice, or an outbox that closed the same events
twice after a crash all carry the same ids. Cloud inserts with `ON CONFLICT DO NOTHING` and counts only what
was inserted: `{ "stored": 37, "duplicates": 11 }`.

A partitioned table cannot hold a unique key that leaves out its partition column, and a retry can land in
a later month than the first attempt. The first version of this spec worked around that for raw lines with
a per-file offset marker. Events have a proper id, so the table is simply **not partitioned** and the key
is exact. That is affordable because events are few (§11): a year for a hundred clients is a few million
rows, and retention deletes a day's worth at a time (§6). If volume ever passes about a hundred million
rows, partition it and move de-duplication to a separate key table.

## 5. Time

Three times per event, and one clock record per install, reusing the server's approach (foundation §4.4,
protocol §5).

| Time | Whose clock | Trusted for |
|---|---|---|
| `received_at` | Cloud's (`TimeProvider`) | order and retention. Never taken from a client. |
| `sent_at` | the client PC's, uncorrected | measuring that PC's clock. |
| `occurred_at` | the log's time, read in the PC's time zone, then in Cloud's time | when it happened. |

- The client measures its offset to Cloud with `GET /api/v1/time` (the same SNTP estimate, `ServerClock`),
  every two hours while it has something to send, corrects every event's time by it before sending — as it
  does for a server — and sends the offset and its confidence with each batch.
- Cloud also measures for itself: `received_at − sent_at`, the PC's clock error plus one request's delay.
- The client's measure **stands** when it says `good` or `fair` and agrees with Cloud's within five
  minutes. Otherwise Cloud undoes it and applies its own. `clock_adjustment_ms` records what Cloud added —
  zero when the client's measure stood — so the client's own value is `occurred_at − clock_adjustment_ms`.
- `install_clock` keeps, per install: the reported offset and confidence, the observed difference, the
  offset applied, and whether the two disagree. A disagreeing clock is **flagged, not refused**.

Cloud never reads the system clock; the client never does either.

## 6. Retention

One setting in Cloud admin, stored in the main database:

| What | Default | Why |
|---|---|---|
| Events | **365 days** | Storage no longer argues for a short window: events are about a thousandth of the raw log's size (§11). But every event names another player and where they were — private instances included — held by the project, not by their group, so they are not kept forever. A year is a full year of backup, and enough to rebuild the totals when a definition changes. |
| Daily and hourly totals | forever | Counts only, with no player names or ids. |

`0` keeps events forever. Retention deletes events whose `received_at` is past the window, **10,000 rows
at a time** so no single statement holds a long lock, once a day. `install_clock` rows untouched for longer
than the window go too.

This is deliberately not foundation §5.5's "keep everything by default": that rule is for a group's own
data on its own server.

## 7. Failure and offline

- **Durable outbox** in `%APPDATA%\Modbot\cloud\`: the open batch as JSON rows, closed batches gzipped, sent
  oldest first and deleted when Cloud answers `200`. Survives restarts and any length of time offline.
- **Cap: 20 MB on disk**, oldest batches deleted first and counted. At a few hundred bytes an event before
  compression, that is hundreds of thousands of events.
- In front of it, an in-memory queue of **10,000 observations**; if it fills, the oldest are dropped and
  counted.
- **Backoff** on no network, `5xx` and `429`: exponential with jitter, two seconds up to five minutes, and
  `Retry-After` when Cloud sends one. This is Cloud's limit, not VRChat's cold stop.
- `400` and `413` are permanent for that batch: it is deleted and counted.
- **Never slows the moderation pipeline.** The reader hands observations to the queue and returns. Building
  events, writing the outbox, compressing, registering and sending run on their own task.

## 8. Limits

| Limit | Value | Answer |
|---|---|---|
| Events per batch | 1,000 | `413` |
| Request body, compressed | 1 MB | `413` |
| Request body, after decompressing | 4 MB | `413` |
| Event data | 4,096 bytes of JSON | stored as `{}` |
| Batches per install | 30 a minute | `429` + `Retry-After` |
| Events per install | 50,000 an hour | `429` + `Retry-After` |
| Registrations per IP address | 10 an hour | `429` + `Retry-After` |

The per-install limits are held in memory, per Cloud process.

## 9. What trends will read

Nothing public reads this yet. Trends will read **`event_hour_total (type, hour)`** — "events of type X per
hour across all installs" is a primary key range scan — and fall back to **`client_event (type,
occurred_at)`** for anything the totals do not answer, and to rebuild them.

## 10. Privacy facts for the privacy policy

1. **The Modbot Client sends its presence events to Modbot Cloud by default**: who joined, was already
   there, left, or changed avatar, and when VRChat's log stopped. It is on unless you turn it off in the
   client's `settings.json` or with the `MODBOT_CLOUD_DISABLED` environment variable on your PC, and
   restart the client. Starting with it off deletes what was queued and not yet sent.
2. **It covers every instance you are in**, including public, friends-only, invite and private instances,
   not only instances of groups you moderate. A Modbot server still only receives its own group's.
3. **Each event names another person**: their VRChat user id, their display name at that moment, and the
   avatar name they switched to, with the world id and instance id where it happened.
4. **It never sends VRChat's raw log**, and never an instance's `nonce`, so nothing Cloud holds is enough to
   join a private instance.
5. **Your own VRChat user id and display name** are in the events too, as the subject of your own arrival.
6. **Your client is identified by a random install id**, not your name, VRChat account or machine. It is
   not linked to any Modbot server you are paired with. Cloud does not store IP addresses.
7. **Events are kept 365 days** by default. Counts with no names or ids are kept indefinitely.
8. **Only the Modbot Cloud administrator can read events**, after signing in to Cloud admin. There is no
   public view, and admin shows world and instance as plain text, never as join links.
9. **Only the person running the client can turn this off**, or send it to a different Modbot Cloud, on
   their own PC. A Modbot server operator cannot turn it off or redirect it for their moderators.
10. **Modbot deployments sending their own logs to Cloud** is a separate feature, built on
    2026-09-16. Its statement is §6 of `2026-09-16-logs-alerts-showcase-design.md`.

This narrows two earlier statements, on purpose and at the maintainer's request:

- M3 §3.1 and §5.5.1 say events about instances outside the managed group are dropped on the moderator's PC
  and never reported. That still holds for every **Modbot server**. The Cloud backup carries them.
- Central services §1.1 says the project holds no facts centrally. Cloud holds these events.

## 11. Volume and cost — estimated, not measured

From the one real log in `.agent/research/vrchat-log-events.md`: about **64 minutes**, 17,123 lines,
754 of them `[Behaviour]`, 88 recognised. The client's parser and phantom-burst rules reduce the whole
session — a home world and a busy group instance — to **32 events** (`RealSessionTests`). That is roughly
**30 events an hour**, against about 16,000 lines an hour of raw log.

| | Estimate |
|---|---|
| One event on the wire | ~350 bytes of JSON, ~100 gzipped |
| One stored row with its four indexes | ~0.5 KB |
| One install, 3 hours a day | ~90 events a day → ~33,000 a year → ~17 MB at the 365-day window |
| 100 installs | ~1.7 GB at steady state → ~$0.40 a month at $0.25/GB |
| 100 installs in busy public instances (~10× the sample) | ~17 GB → ~$4 a month |

Unverified: events per hour in a crowded public instance, hours a day moderators play, and the real row
size. Measure from the first week of data.

## 12. Deploying Cloud

- Image: `docker build -f src/Modbot.Cloud/Dockerfile .` from the repository root.
- Railway: its own service, not in `.railway/railway.ts`. Root Directory empty,
  `RAILWAY_DOCKERFILE_PATH=src/Modbot.Cloud/Dockerfile`, health check `/health/ready`.
- Two Railway Postgres services: `DATABASE_URL` references the main one, `DATABASE_ENGINE_URL` the event one.
- `ROOT_API_KEY` unlocks `/admin`. `PORT` is set by Railway and defaults to 8080.

## 13. Not built here

The server log feed (§0), accounts, instance registration, term lists, showcases, and any public analytics.
What a Modbot server itself will use Cloud for, and the server's own `MODBOT_CLOUD_ENDPOINT` and
`MODBOT_CLOUD_DISABLED`, are planned in central services §1.1.
