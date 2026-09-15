# Modbot Cloud — Log Backup

- **Date:** 2026-09-15
- **Status:** Building
- **Covers:** the client setting "Send all logging to Modbot Cloud as backup", and the event storage it
  sends to in Modbot Cloud (`src/Modbot.Cloud`)
- **Depends on:** foundation §4.4 (one clock), §5.3 (fact schema), §5.5 (retention); client protocol
  §4–§5; M3 §3
- **Reverses:** M3 §3.1 ("never transmitted: the raw log line"), client protocol §8 ("no log content"),
  and central services §1.1 ("no facts, no log content" held centrally). See §10.

---

## 1. What this is

The desktop client reads VRChat's output log. With this setting on — and it is on unless the moderator
turns it off — the client also sends **every line it reads, from every instance**, to Modbot Cloud.

Why: so the project can later draw trends of what happens in VRChat across everyone who runs the client,
and so a line nobody understands today can be read again when a newer parser does. Nothing in this spec
builds the trends. It builds the storage they will read.

These are the maintainer's decisions:

- On by default. No onboarding step, no prompt, no first-run dialog.
- Every instance the moderator is in, not only group instances.
- Works whether or not the client is paired with a Modbot server.

## 2. What is sent

For every line of `output_log_*.txt` the client reads:

| Field | Example | Notes |
|---|---|---|
| `file` | `output_log_2026-09-03_20-26-45.txt` | The file **name** only. Never the folder, which holds the Windows account name. |
| `offset` | `1843320` | Byte offset of the line's first byte in that file. |
| `text` | `2026.09.03 20:27:14 Debug - [Behaviour] OnPlayerJoined …` | The raw line. The user profile folder (`C:\Users\<name>`) is replaced by `%USERPROFILE%` wherever it appears. Cut to 16,384 characters. |
| `loggedAt` | `2026-09-03T20:27:14` | The time **as written** in the line, with no offset. Null for a line with no timestamp (stack trace lines). |
| `utcOffsetMinutes` | `120` | This PC's UTC offset at that time, so `loggedAt` can be read as an instant. |
| `event` | `{ "type": "PlayerJoined", "data": { … } }` | The client's own parse of that one line, when it has one. |

Per batch, alongside the lines: `batchId`, `clientVersion`, `sentAt` (the PC's own clock at the moment of
sending, uncorrected), `clockOffsetMs` and `clockConfidence` (the client's measured correction to Cloud's
clock, §5), and `modbotServerId` (§3.3).

**Never sent:** pairing tokens and device tokens, Modbot's own log files (`%APPDATA%\Modbot\logs`), the
machine name, the Windows account name, folder paths, and any file other than VRChat's output logs.

### 2.1 Which lines, and when

- **Lines read while the setting is on are sent.**
- **Lines already in the log when the client starts** are sent only for the part of that file the client
  had not queued last time — so a client restarted in the middle of a session backs up the lines written
  while it was closed, and a first start does not upload hours of old log.
- **Turning it off** stops sending at once and deletes everything queued and not yet sent.
- **Turning it back on** sends from that moment. Lines written while it was off are never sent.

### 2.2 How often

Lines are grouped into a batch of up to **1,000 lines or 512 KB**, and a batch is closed **60 seconds**
after its first line, whichever comes first. A batch is gzipped JSON over HTTPS: `POST /api/v1/logs`.

At the measured ~16,000 lines an hour (§11), that is about one request a minute while VRChat runs, and
none while it does not.

## 3. Where it goes, and who decides

### 3.1 The address

| Client state | Cloud address |
|---|---|
| Not paired | `https://cloud.modbot.co` |
| Paired, server has `MODBOT_CLOUD_ENDPOINT` | that address |
| Paired, server has neither variable | `https://cloud.modbot.co` |
| Paired, server has `MODBOT_CLOUD_DISABLED=1` | **nothing is sent**, and nothing is queued |

The paired server says which through `GET /api/v{n}/client/time`, which the client already calls when it
starts and every two hours (protocol §5). The answer gains one small object and one id:

```json
{ "serverTime": "…", "cloud": { "endpoint": "https://cloud.modbot.co", "disabled": false }, "instanceId": null }
```

- **Several paired servers:** if any says `disabled`, nothing is sent. Otherwise the first paired server
  that named an address wins.
- **Before a paired server has answered:** lines are queued and not sent, so a server that turns Cloud off
  never has its moderators send a first batch before the client finds out. If it then answers `disabled`,
  the queue is deleted.
- **An older server** whose answer has no `cloud` object counts as "no preference": the default address.
- **A server that is paused or stopped** in the client is not asked, and does not hold sending.
- The address must pass the client's usual rule: HTTPS, or plain HTTP to this PC only. Anything else is
  treated as `disabled`.

### 3.2 Device identity

On first send to an address, the client registers an **install**:

```http
POST /api/v1/installs   { "clientVersion": "2026.9.0", "platform": "windows" }
→ 201 { "installId": "…uuid…", "secret": "…43 characters…", "serverTime": "…" }
```

- The secret is stored in `%APPDATA%\Modbot\cloud-installs.json`, **encrypted to the Windows account with
  DPAPI**, the same way pairing tokens are (protocol §3.2). The install id and address are stored in the
  clear so the file can be read.
- One install per Cloud address. A different address registers again.
- Every batch carries `Authorization: Bearer <installId>.<secret>`. Cloud stores only a SHA-256 of the
  secret.
- A `401` means Cloud no longer knows the install. The client forgets it and registers again, with
  backoff.
- Registration is limited to **10 an hour per IP address** on Cloud. Cloud does not store the address.

### 3.3 The paired server's id

`modbotServerId` carries the paired server's `instanceId` from the time answer, when there is one, so a
later Cloud feature can group installs by server. Modbot servers do not have an instance id yet, so the
server sends `null` today and so does the client. Cloud keeps the last one it was sent on the install.

## 4. Storage

### 4.1 Two databases

| Variable | Database | Holds |
|---|---|---|
| `DATABASE_URL` | Cloud's main database (`CloudContext`) | installs and their secret hashes, admin sessions, settings. Later: accounts, instance registry, term lists, showcases. |
| `DATABASE_ENGINE_URL` | The event storage (`EngineContext`) | log files, log lines, parsed events, per-install clocks, daily and hourly totals. |

Both are required; Cloud refuses to start and names whichever is missing. Each has its own migrations
(`Data/Migrations`, `Engine/Migrations`) and its own history table (`__EFMigrationsHistory`,
`__engine_migrations_history`), and each is migrated at start.

**No foreign keys between them.** Engine rows name an install by its id only. When an install is removed
from the main database its engine rows are **left to retention** (§6); nothing reaches across.

### 4.2 Tables in the event storage

```sql
log_file                       -- one row per install per VRChat log file
  id                 bigint identity PK
  install_id         uuid        not null
  name               text(128)   not null
  stored_through     bigint      not null   -- highest offset stored; the dedupe marker
  lines_stored       bigint      not null
  first_received_at  timestamptz not null
  last_received_at   timestamptz not null
  UNIQUE (install_id, name)

log_line                       -- PARTITION BY RANGE (received_at), monthly
  id                 bigint      not null   -- sequence
  received_at        timestamptz not null   -- Cloud's clock
  sent_at            timestamptz not null   -- the client's clock when it sent
  logged_at          timestamp   null       -- as written in the log, no offset
  utc_offset_minutes smallint    null
  install_id         uuid        not null
  log_file_id        bigint      not null
  line_offset        bigint      not null
  text               text        not null
  PK (id, received_at)
  INDEX (install_id, received_at DESC)

log_event                      -- PARTITION BY RANGE (received_at), monthly
  id                 bigint      not null
  received_at        timestamptz not null
  occurred_at        timestamptz null       -- logged_at read as an instant, in Cloud's time (§5)
  install_id         uuid        not null
  log_file_id        bigint      not null
  line_offset        bigint      not null   -- with log_file_id, finds the raw line
  type               text(128)   not null   -- vrchat.log.player.joined … or modbot.unrecognised
  type_raw           text(128)   null       -- the client's own name, when type is modbot.unrecognised
  parsed_by          text(32)    not null   -- the client version that parsed it
  data               jsonb       not null
  PK (id, received_at)
  INDEX (type, occurred_at)                 -- the trends index (§9)

install_clock      (install_id PK)          -- the clock record, updated per batch (§5)
line_day_total     (day, install_id) PK     -- lines stored per install per day; the admin chart
event_hour_total   (type, hour) PK          -- events per type per hour; trends
```

- **Append-only.** `log_line` and `log_event` rows are never updated. A re-read is a new row. The totals
  tables are counters, like the server's daily totals (foundation §5.4).
- **Parsed events follow foundation §5.3.1.** Known client types are named `vrchat.log.*`; anything else
  is stored as `modbot.unrecognised` with the client's word in `type_raw` and its data kept whole. A later
  parser re-reads `log_line` and writes new `log_event` rows with its own `parsed_by`.

### 4.3 Partitioning: by received time

Both big tables are partitioned by **`received_at`, Cloud's own clock**, not the log's time:

- `received_at` is always now, so the partitions that exist are always the right ones. The log's time
  comes from a PC Modbot does not control, and a log can carry any date at all.
- Retention (§6) drops whole partitions by how long ago Cloud received them, which is how storage is
  actually used up.
- A trends query by `occurred_at` still reaches few partitions: a line arrives at most the outbox's life
  after it was written, so `received_at` bounds `occurred_at` closely.

Partitions are created one month behind to two months ahead, at start and daily, the same way the
server's `EventPartitionMaintainer` does.

### 4.4 Dedupe: install + log file + offset

A unique index on a partitioned table must include `received_at`, and a retry arrives with a new
`received_at`, so such an index would dedupe nothing. The dedupe key lives in `log_file` instead:

- A batch is written in one transaction. For each file it names, the `log_file` row is locked.
- A line whose offset is **at or below `stored_through`** is a duplicate and is dropped. The rest are
  written in offset order and `stored_through` moves up.
- The client sends each file's lines in offset order, always. A retried batch, a batch sent twice, and a
  restart that replays lines are all duplicates by this rule.
- A log that shrinks and starts again from offset 0 would read as duplicates. VRChat writes a new file
  per launch and does not do this.

The answer is `{ "stored": 812, "duplicates": 188 }`.

## 5. Time

Three times per line, and one clock record per install.

| Time | Whose clock | Trusted for |
|---|---|---|
| `received_at` | Cloud's (`TimeProvider`) | ordering and retention. Never taken from a client. |
| `sent_at` | the client PC's, uncorrected | measuring that PC's clock. |
| `logged_at` + `utc_offset_minutes` | VRChat's text and the PC's time zone | when it happened, on that PC. |

This reuses the server's approach (foundation §4.4, protocol §5):

- The client measures its offset to Cloud with `GET /api/v1/time`, the same SNTP estimate
  (`ServerClock`), every two hours, and sends `clockOffsetMs` and `clockConfidence` with every batch.
- Cloud also measures for itself: `received_at − sent_at`, which is the PC's clock error plus the network
  delay of one request.
- `install_clock` keeps, per install: the reported offset and confidence, the observed difference, and
  whether the two disagree by more than five minutes. A disagreeing clock is **flagged, not refused**.
- `log_event.occurred_at` is `logged_at − utc_offset_minutes`, then corrected: by the reported offset
  when its confidence is `good` or `fair` and it agrees with the observed difference, otherwise by the
  observed difference. All three raw times stay on `log_line`, so a better correction can be run later.

Cloud never reads the system clock; everything goes through `TimeProvider`. The client never reads it
either; everything goes through `IModbotClock`.

## 6. Retention

Settings in Cloud admin, stored in the main database:

| What | Default | Why |
|---|---|---|
| Log lines | **90 days** | About 95% of the storage (§11), and the rows that hold other players' names and private instance locations (§10). Ninety days is long enough to re-read a quarter's lines with a newer parser. |
| Parsed events | **365 days** | Small, and a year is what a trend needs to compare a month with the same month last year. |
| Hourly and daily totals | forever | Counts only, with no player names or ids. |

`0` means keep forever. Pruning is a `DROP TABLE` on a whole partition once all of it is past the window,
never a `DELETE` (foundation §5.5). `log_file` and `install_clock` rows untouched for longer than the log
line window are deleted by the same daily job.

This is deliberately not foundation §5.5's "keep everything by default": that rule is for a group's own
data on its own server. This is every client's view of every instance, held by the project.

## 7. Failure and offline

- **Durable outbox** in `%APPDATA%\Modbot\cloud\`: the open batch as JSON lines, closed batches gzipped,
  one file each, sent oldest first and deleted when Cloud answers `200`. It survives restarts and any
  length of time offline.
- **Cap: 100 MB on disk.** When closing a batch passes it, the oldest batches are deleted first and the
  dropped line count is kept. At about ten to one compression that is roughly 500 hours of play.
- Between the log reader and the outbox is an in-memory queue of **50,000 lines**. If it fills, the oldest
  lines are dropped and counted.
- **Backoff** on no network, `5xx` and `429`: exponential with jitter, two seconds doubling to five
  minutes, and `Retry-After` when Cloud sends one. This is Cloud's limit and ordinary backoff is correct;
  it has nothing to do with VRChat's cold stop (foundation §4.3.1).
- `400` and `413` are permanent for that batch: it is deleted and counted, not retried.
- **Never slows the moderation pipeline.** The log reader hands lines to the queue and returns. Writing
  the outbox, compressing, registering and sending all happen on a background task. A Cloud that hangs
  or is gone costs the reader nothing.

## 8. Limits

| Limit | Value | Answer |
|---|---|---|
| Lines per batch | 2,000 | `413` |
| Request body, compressed | 2 MB | `413` |
| Request body, after decompressing | 8 MB | `413` |
| Line text | 16,384 characters | cut, not refused |
| Event data | 4,096 bytes of JSON | the event is stored with empty data |
| Batches per install | 30 a minute | `429` + `Retry-After` |
| Lines per install | 200,000 an hour | `429` + `Retry-After` |
| Registrations per IP address | 10 an hour | `429` + `Retry-After` |

The client stays well inside these: 1,000 lines and 512 KB a batch. The rate limits are held in memory,
per Cloud process; a restart forgets them.

## 9. What trends will read

Nothing public reads this yet. When trends are built, they read:

- **`event_hour_total (type, hour)`** — "events of type X per hour across all installs" is a primary key
  range scan. Written in the same transaction as the events and only for rows actually stored, so dedupe
  holds for the totals too. `hour` is `occurred_at` truncated to the hour, or `received_at` when there is
  no time.
- **`log_event` with index `(type, occurred_at)`** — for anything the totals do not answer, and to
  rebuild them.
- **`log_line`** — to re-read with a newer parser.

## 10. Privacy facts for the privacy policy

Statements of fact for `PRIVACY_POLICY.md`:

1. **The Modbot Client sends VRChat's whole output log to Modbot Cloud by default.** It is on unless you
   turn it off in the client's settings. Turning it off deletes what was queued and not yet sent.
2. **It covers every instance you are in**, including private, friends-only and invite instances, not
   only instances of groups you moderate.
3. **VRChat's log contains other people's data**: the display names and user ids of everyone in the same
   instance as you, the avatar names they switch to, world ids and instance ids.
4. **It contains instance locations**, including the `~nonce(…)` part of private and friends-only
   instances, which is enough to join them. Cloud stores these as written.
5. **It contains your own VRChat display name and user id**, and anything else VRChat writes to its log.
6. **The client removes your Windows user folder name** from lines before sending, and sends log file
   names, not folders. It sends nothing from Modbot's own logs and none of its tokens.
7. **Your client is identified by a random install id**, not by your name, VRChat account or machine. If
   the client is paired with a Modbot server, the install can be linked to that server's id. Cloud does
   not store IP addresses.
8. **Log lines are kept 90 days and parsed events 365 days** by default (§6). Totals with no names or ids
   are kept indefinitely.
9. **Only the Modbot Cloud administrator can read raw lines**, after signing in to Cloud admin. There is
   no public view.
10. **Cloud admin never shows instance locations as join links**, and hides the `nonce` value in its view
    of a line.
11. **A Modbot server operator can turn this off for their moderators** with `MODBOT_CLOUD_DISABLED=1`, or
    send it to their own Cloud with `MODBOT_CLOUD_ENDPOINT`.

This reverses three earlier statements, on purpose and at the maintainer's request:

- M3 §3.1 lists "the raw log line" and "anything about the world outside the managed group" as never
  transmitted. That still holds for what goes to a **Modbot server**. It does not hold for Modbot Cloud.
- Client protocol §8's "No log content" still describes the server protocol. Cloud has its own.
- Central services §1.1 says the project holds no facts and no log content centrally. Cloud does.

## 11. Volume and cost — estimated, not measured

From the one real log described in `.agent/research/vrchat-log-events.md`: **17,123 lines in about 64
minutes**, roughly **16,000 lines an hour**, most of them `[IK Debug Log]` and `[VRCTrackingManager]`
frame lines. The `[Behaviour]` fixture averages 88 bytes a line.

| | Estimate |
|---|---|
| Raw text | ~1.8 MB an hour of play |
| On the wire, gzipped | ~0.2 MB an hour |
| Stored: one `log_line` row with its index | ~250 bytes → ~4 MB an hour |
| One install, 3 hours a day | ~12 MB a day → ~1.1 GB at the 90-day window |
| 100 active installs | ~110 GB at steady state → ~$27 a month at $0.25/GB |

Unverified: lines per hour in a busy instance, hours a day a moderator plays, and the real row size.
Measure all three from the first week of real data before choosing a database plan.

## 12. Deploying Cloud

- Image: `docker build -f src/Modbot.Cloud/Dockerfile .` from the repository root.
- Railway: its own service, not in `.railway/railway.ts`. Root Directory empty,
  `RAILWAY_DOCKERFILE_PATH=src/Modbot.Cloud/Dockerfile`, health check `/health/ready`.
- Two Railway Postgres services. `DATABASE_URL` references the first, `DATABASE_ENGINE_URL` the second.
- `ROOT_API_KEY` unlocks `/admin`. `PORT` is set by Railway and defaults to 8080.
- `/health/ready` answers only while both databases are reachable.

## 13. Not built here

Accounts, instance registration, term lists, server logs, showcases, and any public analytics. The main
database and the feature folders leave room for them.
