# Modbot — Importing old data

- **Date:** 2026-09-17
- **Status:** Implemented with this document
- **Covers:** the import file format; how each record becomes a fact; the source each record is
  filed under; the import job and its endpoints; the permission it needs; idempotency; storage;
  purge-user
- **Implements:** foundation §5.1 ("recorded history is not retrofittable" — except by the group
  bringing its own), §5.3 (facts, `source`), §5.3.1 (`modbot.unrecognised` and `type_raw`), §5.5
  (purge-user), §5.9 (Modbot's own audit log)
- **Related:** API keys design §3 (a key acts as the person who made it); foundation §4.4 (one
  clock), §3.1.1 (ids are opaque)

---

## 1. What this adds

A group moving to Modbot usually has history somewhere else: a Discord bot's ban list, a
spreadsheet of warnings, an export from VRCX or from whatever it ran before. Modbot cannot fetch
any of that. Until now the only way in was to write facts by hand.

This adds one door: **an upload of plain JSON records**, each of which becomes one fact in the
same log everything else is written to. Imported facts sit in the audit log, in a person's
history, in daily totals and in retention beside everything Modbot recorded itself, marked with a
source of their own so nobody mistakes them for something Modbot saw happen.

## 2. The file

A file is either a **JSON array** of records or **newline-delimited JSON** (one record per line,
blank lines ignored). Both are accepted and told apart by the first non-blank character: `[` is
an array; anything else is read line by line. A single `{…}` object on its own is one record.

One record:

```json
{
  "kind": "ban",
  "at": "2024-03-01T18:22:07Z",
  "subject": { "platform": "vrchat", "id": "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469" },
  "actor": { "platform": "vrchat", "id": "usr_9a2b…", "name": "Alice" },
  "externalId": "ban-1042",
  "data": { "reason": "Harassment in the Friday event" }
}
```

| Field | Required | Meaning |
|---|---|---|
| `kind` | yes | What happened. One of the short words in §3, or a full Modbot fact type name. Anything else is kept as it is (§3.2). |
| `at` | yes | When it happened, as an ISO 8601 date and time. A value without an offset is taken as UTC. |
| `subject` | yes | Who or what it happened to: `platform` is `vrchat` or `discord` (any case), `id` is that platform's id for them. Never validated for shape (foundation §3.1.1). For an entry about the group itself rather than a person, the group's id is the subject. |
| `actor` | no | Who did it: `platform`, `id`, and an optional `name`. The name is kept in the fact's data as `actorDisplayName`, the way VRChat's own audit entries carry one. |
| `externalId` | no | The old platform's own id for this record. Used for idempotency (§6). At most 200 characters. |
| `data` | no | Anything else worth keeping, as an object. Stored as the fact's payload. Nothing in it is interpreted. |

Anything else at the top level of a record is ignored. The upload as a whole carries a **source
label** (§4.1): the name of the platform the file came from, such as `vrcx` or `old-bot`.

Every fact written from a record has:

- `occurred_at` = `at`, exact (`occurred_before` null);
- `observed_at` = now, from `IModbotClock`, like every other fact;
- `source` = `Import` (§5);
- `data` = the record's `data`, plus `importId`, `importSource`, `externalId` when given, and
  `actorDisplayName` when the actor had a name.

## 3. Kinds

A `kind` is a short word, and the fact type it becomes depends on the subject's platform: a `ban`
of a VRChat user is `vrchat.group.member.ban`, a `ban` of a Discord user is `discord.member.ban`.

| `kind` | VRChat subject | Discord subject |
|---|---|---|
| `join` | `vrchat.group.member.join` | `discord.member.join` |
| `leave` | `vrchat.group.member.leave` | `discord.member.leave` |
| `remove` | `vrchat.group.member.remove` | — |
| `kick` | `vrchat.group.instance.kick` | `discord.member.kick` |
| `warn` | `vrchat.group.instance.warn` | — |
| `ban` | `vrchat.group.member.ban` | `discord.member.ban` |
| `unban` | `vrchat.group.member.unban` | `discord.member.unban` |
| `timeout` | — | `discord.member.timeout` |
| `note` | `modbot.note.add` | `modbot.note.add` |
| `role-add` | `vrchat.group.role.assign` | `discord.role.assign` |
| `role-remove` | `vrchat.group.role.unassign` | `discord.role.unassign` |
| `invite` | `vrchat.group.invite.create` | — |
| `join-request` | `vrchat.group.request.create` | — |

Kinds are matched without regard to case. A dash in the table is a kind that platform has no
fact type for; it is kept as unrecognised (§3.2), not refused.

### 3.1 A full fact type name as the kind

A `kind` containing a dot is taken as a Modbot fact type name. If it is one Modbot has
(`FactType.All`, for example `vrchat.group.post.create` or `vrchat.group.update`), the fact is
written with exactly that type. This is how a file of VRChat audit-log entries from another tool
comes in with every type Modbot already understands.

### 3.2 A kind Modbot does not know

Is **never dropped**. It is written as `modbot.unrecognised` with the kind, exactly as given, in
`type_raw` — the same mechanism foundation §5.3.1 uses for a VRChat audit entry Modbot has no
name for. It is moderation history, kept forever by default, visible to anyone who can see the
audit log, and it can be understood later. A file with a kind nobody expected still imports in
full; the rejection list is for records that are malformed, not for records Modbot has no word
for.

### 3.3 Two new fact types

- `modbot.note.add` — "Note added". A written note about a person. Moderation retention; visible
  with **See the audit log**. Old platforms nearly all have notes and Modbot had no type for one.
- `modbot.import.done` — "Import finished". Modbot's own record that an import ran (§4.4).
  Operational; visible with **See the operational log**.

## 4. The endpoints

Under `/api/imports`, tag **Imports**, and the permission is **Change settings**
(`ManageSettings`): the same one that opens the Settings page, where the card lives. It is an
operator's job, and an operator's API key holding it works, so it can be scripted.

### 4.1 `POST /api/imports`

Starts an import. Two body shapes:

- **the file as the body** — `Content-Type: application/json` or `application/x-ndjson`, the
  file's bytes and nothing else; `source`, `dryRun` and `fileName` as query parameters;
- **`multipart/form-data`** — a `file` part, and `source`, `dryRun`, `fileName` as form fields
  (the file part's own name fills `fileName` when the field is absent).

| Parameter | |
|---|---|
| `source` | Required. 1–64 characters. The source label every record is filed under (§6). |
| `dryRun` | `true` to validate and count without writing anything (§4.3). |
| `fileName` | Optional, shown in the list of past imports. |

The body is capped at **64 MB**; over that is `413`. An empty body is `400`. The answer is the
import (§4.2) with its id, status `Queued`. Nothing has been read yet: a file that is not JSON at
all ends as `Failed` with the parser's reason, not as a `400`.

### 4.2 `GET /api/imports/{id}` and `GET /api/imports`

One import, or the latest fifty, newest first:

| Field | |
|---|---|
| `id`, `source`, `fileName`, `dryRun` | As uploaded. |
| `status` | `Queued`, `Running`, `Done` or `Failed`. |
| `received` | Records read from the file so far, well-formed or not. |
| `imported` | Facts written. For a dry run, facts that would be written. |
| `skipped` | Records that were already imported (§6). |
| `rejected` | Records refused, and `rejections`: the first fifty, each `{ line, reason }`. |
| `error` | For `Failed`: why. |
| `startedBy`, `createdAt`, `startedAt`, `finishedAt` | Who and when. |

`line` is the line number in a newline-delimited file, and the 1-based position in a JSON array.
Counts update as the job runs, so a page can show progress by asking again.

### 4.3 Dry run

`?dryRun=true` runs the same job — parsing, mapping, the duplicate check — and writes nothing:
no facts, no dedupe rows, no audit entry. The counts say what an import of the same file would
do. It is listed with the past imports, marked as a dry run.

### 4.4 The audit entry

Every import that is not a dry run writes one `modbot.import.done` fact when it finishes, whether
`Done` or `Failed`: subject is the import id on the Modbot platform, actor is the account that
uploaded it, and the payload carries `source`, `fileName`, `status`, `received`, `imported`,
`skipped` and `rejected`. One entry, not one per record: the imported facts themselves already
say what came in.

## 5. The `Import` source

`FactSource.Import = 7`. Appended, never renumbered. It says: *a person uploaded this from
somewhere else; Modbot did not see it happen.* That is a different confidence from `AuditLog`
and a different one again from `SyncDiff`, and the timestamp is whatever the old platform said.

Where sources are listed, `Import` is listed:

- the audit log's **Source** filter, and its default. The default is **VRChat · Discord ·
  Client · Import**, with Sync still off. Old data is what somebody imported on purpose; hiding
  it by default would be hiding the reason they uploaded it.
- the source badge on every fact row (label **Import**);
- `GET /api/audit/filters`, which reflects over the enum and needs no change;
- the event stream's `source` field.

The client-report deduplication window (`FactDeduplication.AppliesTo`) stays `Client` only:
imports have their own idempotency (§6), which is exact rather than a window. The writer's
held-roles enrichment is skipped for `Import` as it is for `Client`: roles at a date years ago are
not something the recorded role changes can answer, and it is a query per record on a path that
runs for thousands.

## 6. Idempotency

Uploading the same file twice writes nothing the second time. The rule:

- a record with an `externalId` is keyed `id:` + the externalId;
- a record without one is keyed `hash:` + the SHA-256 of its canonical form: `kind`, `at` in
  UTC, subject platform and id, actor platform and id, and `data` with its keys sorted at every
  level, joined in that order;
- the key is scoped to the upload's **source label**. `ban-1042` from `old-bot` and `ban-1042`
  from `spreadsheet` are two records.

A key already recorded is **skipped**, counted, and never an error. The same record twice in one
file is skipped the second time too. Changing a field of a record without an `externalId` changes
its hash and imports it again as a new fact; that is the honest outcome, since Modbot cannot tell
a correction from a different event. A record with an `externalId` is imported once whatever its
other fields say, which is what an external id is for.

## 7. Storage

```sql
import                              -- one row per upload
  id                 uuid           primary key
  source             varchar(64)
  file_name          varchar(256)   null
  dry_run            boolean
  status             smallint       -- Queued 1 | Running 2 | Done 3 | Failed 4
  received, imported, skipped, rejected   integer
  rejections         jsonb          -- [{ line, reason }], at most fifty
  error              text           null
  started_by_user_id uuid
  started_by_name    varchar(64)
  created_at, started_at, finished_at     timestamptz (the last two null until they happen)
  body               bytea          null   -- the upload, until the job has read it

import_record                       -- what has been imported, for §6
  source             varchar(64)
  key                varchar(256)   -- "id:…" or "hash:…"
  fact_id            bigint
  import_id          uuid
  subject_platform   smallint
  subject_id         text
  imported_at        timestamptz
  PRIMARY KEY (source, key)
  INDEX (subject_platform, subject_id)
```

The dedupe key is its own table rather than a unique index on `modbot_event`, because the fact
table is partitioned by `occurred_at` and PostgreSQL requires the partition key in every unique
constraint on it — a unique index on `(source, key)` cannot exist there. A small side table with a
plain primary key can, and the batch check (§8) is one `IN` query against it.

The upload's bytes live in the `import` row until the job has parsed them, then the column is
set to null. That is what lets a queued import survive a restart of the process without a file
store, and a 64 MB row is nothing PostgreSQL minds.

**Purge-user** (foundation §5.5) deletes imported facts about a person with everything else: they
are ordinary rows in `modbot_event` keyed by subject. It also deletes that person's rows from
`import_record`, so nothing about them is left behind and so a later upload of the same file is a
deliberate act with a visible result rather than a silent no-op.

Retention applies by type (foundation §5.5): a `vrchat.group.member.ban` is moderation whether
Modbot recorded it or a file did. An import that reaches back years creates the monthly partitions
it needs as it goes (`EventPartitionMaintainer.EnsureForAsync`).

## 8. The job

There is no job queue in Modbot; there is the pattern of a scoped worker and a hosted service
that runs it (`RetentionService`, `DailyTotalsService`). Imports follow it:

- `ImportRunner.RunAsync(id)` does one import: loads the row, parses the body, and processes
  records in **batches of 500** — map, key, one query for keys already present, write the facts,
  add the dedupe rows, commit — updating the row's counts after each batch;
- `ImportService` is the hosted loop: it takes the oldest `Queued` import, runs it, and waits
  for a nudge (`ImportSignal`, pulsed by the upload endpoint) or five seconds before looking
  again. One import at a time, on purpose: two imports of the same file at once would race the
  duplicate check, and the ordering of the fact ids is worth keeping.

An import found `Running` when the process starts was interrupted by a restart. It is marked
`Failed` with that as the reason. The records its committed batches wrote are in the log and in
`import_record`, so uploading the file again imports only what was left.

## 9. No screen

There was a **Settings → Host & Database → Import** card: a file picker, a **Source** box, **Dry
run** and **Import** buttons and the list of past imports. It is gone, and the endpoints are the
whole feature.

The reason is that the card was on the wrong screen for the job it does. An import is a one-off
move somebody makes once, from a file they have just converted with a script they wrote against
this page, and the converting is the work — the upload is the last line of it. Putting a file
picker in Settings made a permanent control out of a thing almost nobody does twice, on the one
page every operator opens for ordinary reasons, and it invited a moderator to press it without
having read what the file has to contain.

So importing is a thing you do with an API key (§4), the format stays documented at
[docs.modbot.co/self-hosting/importing-old-data](https://docs.modbot.co/self-hosting/importing-old-data),
and nothing in the web app reaches `/api/imports`.

## 10. What this deliberately does not do

- **Map old platforms' formats.** Modbot takes one format and documents it. A converter for a
  particular bot's export is a script somebody writes once against this page, not a switch inside
  Modbot that would need a case per platform forever.
- **Write anything but facts.** No case files, no group members, no ban list rows. The member
  and ban lists are what VRChat says now; history is what the import is for.
- **Delete an import.** Facts are append-only (foundation §5.2). An import with the wrong data
  is corrected by purging the people concerned, or by restoring a backup taken before it.
- **Accept `observed_at` or `occurred_before`.** The server stamps the first (foundation §4.4)
  and an imported record is either dated or it is rejected.
