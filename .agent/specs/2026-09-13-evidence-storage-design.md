# Modbot — Evidence Storage

- **Date:** 2026-09-13
- **Status:** Draft, awaiting review
- **Covers:** case files — the moderator's written rationale, uploaded video and screenshot evidence,
  and the subject's VRChat profile as it stood when the action was taken
- **Depends on:** M0 (fact log, `Settings`, `IModbotClock`, `INotifier`, `IVRChatGate`), M4 (ban reports)
- **Implements:** foundation §5.8.3 and M4 §7 — *"optional evidence references"*, upgraded from
  references into stored artefacts
- **Related:** M3 §3.1 (what the client never transmits), `docs/security.md`, `docs/data-retention.md`

---

## 1. What a case file is

An audit-log entry says *"Gunner24 banned GayHater59"*. That is the whole of what VRChat records, and
it is almost useless three months later when the ban is disputed, when the moderator has left, or
when a federation partner (M8) asks why.

M4 §7 already requires a **ban report**: a classification and a written justification, pre-filled
from what Modbot knows. This spec adds the two things that turn a report into a case file:

| Part | Authored by | Stored as |
|---|---|---|
| **Rationale** — markdown, written by the acting moderator | human | text on the report |
| **Evidence** — video and screenshots the moderator attaches | human | **blobs** (§5) |
| **Profile snapshot** — the subject's username, bio, avatar, status at action time | Modbot | **`jsonb` fact** (§12) |

The third is the one people forget to ask for and the one that is unrecoverable. A banned user
changes their display name and clears their bio within the hour; the screenshot the moderator meant
to take was never taken. Foundation §5.1's argument applies unchanged — *recorded history is not
retrofittable* — and it applies with more force here, because a profile is not merely impossible to fill in later,
it is actively being erased by the person the case is about.

### 1.1 Why this is a document of its own

Evidence is the first thing Modbot stores that is neither a fact nor a setting. It is the first
file-upload surface in a product where **every API endpoint is JSON today** and no image bytes have
ever entered the system. It is the first thing whose size is measured in megabytes rather than the
326 bytes a fact costs. And it is the first thing that can be **silently destroyed by a
misconfiguration** rather than by an operator's decision.

Each of those is a new failure mode. Folding them into M4 would bury them.

---

## 2. What is already settled

These were decided before this document was written. They are recorded here with their reasoning so
that the reasoning is not lost, not so they can be re-argued.

1. **Three backends**, chosen in settings: S3-compatible (recommended), local filesystem, and
   in-database. Argued in §4.
2. **Docker is the only supported hosting method**, and the design therefore targets two deployment
   profiles rather than a spectrum of installations. §3.
3. **Evidence survives purge-user.** §15.
4. **Images and video both**, with a conservative default per-file cap the operator can raise. §9.3.
5. **Environment variables may pre-fill the wizard; the database still decides.** §16.1.

---

## 3. Two deployment profiles

Docker-only hosting means the choice an operator actually faces is not "which of eleven storage
options" but **"do I have somewhere durable to put bytes, and is it a bucket or a disk?"** Everything
else follows from that, including logging, so the design names two profiles and tunes the defaults
of each.

| | **Stateless** — recommended | **Persistent** |
|---|---|---|
| Evidence | S3-compatible bucket | filesystem under the data root |
| Logs | Seq (`SEQ_URL`) and console — the file sinks switch themselves off | files under the data root, Seq optional |
| Docker volume | **none** | **required** |
| Survives a redeploy | everything, because nothing local matters | everything on the volume; the container is still disposable |
| Typical host | Railway, Fly, any PaaS | a home server, a NAS, a spare box |
| The way it goes wrong | wrong bucket, wrong prefix, revoked key | **volume never mounted** (§8) |

The in-database backend is orthogonal to both: it makes a stateless deployment stateless *and*
volume-less at the cost of §4.3's problems, and it is the only backend that needs no third thing to
be configured at all. That is its entire case, and it is a real one for a small group.

### 3.1 Modbot must not declare a `VOLUME` in the Dockerfile

It is tempting to add `VOLUME /app/data` so that the persistent profile "just works". It is wrong.

`VOLUME` in a Dockerfile creates an **anonymous** volume when the operator mounts nothing — which
means a filesystem backend on an unconfigured host appears to work, writes evidence into a volume
nobody knows the name of, and loses it the first time the container is recreated with `--rm` or by a
platform that does not reattach anonymous volumes. That is the §8 trap with a coat of paint on it: it
converts a loud failure into a silent one.

So the data root is a **documented mount point** (`/app/data`, evidence under `/app/data/evidence`)
that the operator mounts deliberately, and Modbot detects a missed mount rather than papering over
it. The detection is what makes this safe, not the directive.

Modbot already takes this position for logs: `PersistenceProbe` decides whether the log directory
survives a restart from **evidence rather than from platform inference**, because Railway, Fly.io and
Render all offer mountable volumes and so the platform can only supply a default. Evidence storage
takes the same position for the same reason, and §8.2.1 sets out where the two mechanisms differ.

---

## 4. The three backends

### 4.1 S3-compatible — the recommendation

Object storage is the right shape for this data: write-once, read-rarely, large, and completely
uninteresting to query. It scales past any group's needs, it is cheap, it is backed up by somebody
else, and — decisively — **it can hand the browser a URL and step out of the way**, which none of the
other two can.

Must work against Wasabi, Railway Buckets, Cloudflare R2, MinIO and AWS S3. In practice that means:

- **Both URL styles.** Virtual-hosted-style (`https://bucket.endpoint/key`) and path-style
  (`https://endpoint/bucket/key`). Railway issues virtual-hosted-style on new buckets and path-style
  on older ones; MinIO defaults to path-style. This is a setting with an autodetect probe, not an
  assumption.
- **SigV4 only**, region configurable, endpoint configurable.
- **No feature that is not universal.** Not lifecycle rules, not versioning, not object lock, not
  server-side encryption with customer keys. §4.1.1 explains why that list is exactly the Railway
  gap, and why designing to it costs nothing.

#### 4.1.1 Railway Buckets, and the four things it does not have

Verified from Railway's documentation, because it is the platform the one-click template targets:

| | |
|---|---|
| Price | $0.015/GB-month |
| Egress | **free**, and **all S3 API operations are free and unlimited** |
| Visibility | private only — public buckets are not supported; presigned URLs are |
| URL style | virtual-hosted-style on new buckets, path-style on older ones (the Credentials tab says which) |
| Injected variables | `BUCKET`, `ACCESS_KEY_ID`, `SECRET_ACCESS_KEY`, `REGION`, `ENDPOINT` |
| Encryption | at rest, provider-managed |
| Backend | Tigris |
| **Not supported** | **bucket lifecycle configuration, object versioning, object locks, server-side encryption** |
| Backups | none, automatic or otherwise |
| Networking | public only |

Four consequences run through the whole of this design, and none of them is Railway-specific once
stated — they are simply the lowest common denominator made visible:

1. **No lifecycle rules ⇒ Modbot deletes its own garbage.** There is no "expire objects under
   `staging/` after 24 hours" to lean on. The orphan sweep in §9.5 is therefore a scheduled job
   Modbot runs, not a bucket setting an operator configures. Building it the other way would have
   produced a design that worked on AWS and quietly leaked objects on Railway.
2. **No versioning ⇒ a delete is final.** There is no previous version to restore, which is most of
   why §6 makes deletion an explicit, permissioned, recorded act rather than a button.
3. **No backups ⇒ say so.** An operator who believes their hosting provider is keeping copies of
   their evidence is wrong, and the settings page must tell them that rather than let them find out.
4. **Egress is asymmetric, and it points the design somewhere useful.** Railway's rule is *"bucket
   egress is free; service egress is not — including uploads from a service to a bucket."* So:

   | Path | Cost on Railway |
   |---|---|
   | Browser → bucket, presigned PUT | free (not service traffic) |
   | Bucket → Modbot, reading an object back | **free** (bucket egress) |
   | Modbot → bucket, proxying an upload through | **charged** (service egress) |
   | Bucket → browser, presigned GET | free |
   | Modbot → browser, proxied download | **charged** |

   The naive implementation — proxy uploads and downloads through the app — is the one that costs
   money on every byte in both directions. The implementation in §9.2 costs nothing, and it is not a
   worse design for other providers; it is simply also cheaper on this one.

### 4.2 Local filesystem

For a group running Modbot on a machine in someone's house, where "go and sign up for object
storage" is a genuine barrier and the disk is right there.

- Requires a mounted Docker volume, mounted by the operator (§3.1).
- Objects at `<data root>/evidence/sha256/ab/cd/<full-hex>`. The two-level shard keeps directory
  entry counts sane on filesystems that care, and costs nothing on those that do not.
- **No presigned URLs.** Every read streams through Modbot, which is fine at this scale and is
  declared as a capability rather than discovered when something throws (§13.2).
- The failure mode is §8, and it is the reason §8 exists.

### 4.3 In-database — supported, configurable, and not recommended

Postgres will hold bytes. It should not hold these ones, and the settings page says so at the moment
of choosing rather than in a footnote.

**Why it is a bad idea:**

- **`pg_dump` grows by the full evidence volume.** A database that was 240 MB a year becomes a 20 GB
  dump the first time somebody attaches a dozen clips. Backup windows, restore times and transfer
  costs all scale with it, and a restore that takes four hours is not a restore you can perform
  during an incident. On managed Postgres, the backup size is also often what you are billed for.
- **Field size limits are real.** A single Postgres value cannot exceed 1 GB, and a `bytea` that
  large is unworkable long before it is illegal. Evidence is therefore stored **chunked** — see
  below — which is exactly the complexity object storage exists to absorb.
- **It evicts the working set.** Reading a 100 MB video pulls 100 MB through the connection pool and
  through `shared_buffers`, displacing the fact-log pages that make §5.10's profile queries fast.
  The cost of the evidence backend is paid by the analytics that have nothing to do with it.
- **WAL amplification.** Every byte is written to the WAL and then to the heap, shipped to any
  replica, and retained by any PITR window. The bytes are effectively stored three times.

**Its one genuine virtue, which is not nothing:** a single backup covers everything. `pg_dump` and a
restore, and the evidence is there. No second credential, no second service, no second thing to
forget when handing the deployment to the next volunteer. For a small group with a handful of
screenshots and no appetite for object storage, that is a defensible trade, and refusing to support
it would push those operators onto the filesystem backend without a volume — which is strictly worse
(§8).

**Shape, if chosen:**

```sql
modbot_evidence_chunk
  hash      bytea    not null      -- SHA-256, 32 bytes
  ordinal   int      not null
  bytes     bytea    not null      -- ~1 MiB per chunk
  PRIMARY KEY (hash, ordinal)
```

The column is `SET STORAGE EXTERNAL` — TOAST without compression. Compression buys almost nothing on
media that is already compressed, and it makes a range read decompress the whole value, which breaks
video seeking (§10.5). Chunking gives range reads, dodges the 1 GB limit, and keeps any single row
small enough not to be pathological.

The settings page shows evidence bytes held in the database as a distinct line and warns once it
passes a threshold, because the failure here is gradual and nobody notices a backup getting slowly
slower.

### 4.4 Side by side

| | S3-compatible | Filesystem | In-database |
|---|---|---|---|
| Recommended | **yes** | for home installs | no |
| Volume required | no | **yes** | no |
| Presigned direct delivery | **yes** | no | no |
| Range reads | yes | yes | yes (chunked) |
| Bytes in `pg_dump` | no | no | **yes, all of them** |
| Extra credential to manage | yes | no | no |
| Survives losing the container | yes | only if mounted | yes |
| Silent-loss risk | wrong bucket/prefix | **unmounted volume** | none |

---

## 5. Content addressing

### 5.1 The key is the SHA-256 of the bytes

`sha256/ab/cd/abcdef…` — lowercase hex, nothing else, for every backend.

Four things fall out of it, and the fourth is the one that matters most in a dispute:

1. **Deduplication is free.** Two moderators attaching the same clip to two reports store one object.
2. **Keys are hex.** There is no user-supplied filename anywhere in a path, so path traversal is not
   mitigated, it is **impossible by construction**. No `..`, no null bytes, no Unicode normalisation
   surprise, no case-folding collision on a filesystem that folds case. The original filename is kept
   as metadata and displayed; it is never part of a key.
3. **Writes are idempotent.** Re-uploading the same bytes after a timeout produces the same key.
   Combined with §6's append-only rule, a retry cannot corrupt anything.
4. **The bytes and their name cannot drift apart.** An object at `sha256/<h>` whose contents do not
   hash to `<h>` is detectably wrong. Evidence that can be swapped without detection is worth nothing
   in the argument it exists to settle.

### 5.2 What the hash proves, and what it does not

It is worth being precise, because "tamper-evident" is the kind of phrase that grows in the retelling.

**It does prove:** that the object served from key `<h>` is byte-identical to the object that was
stored at `<h>`. Bit rot, a truncated write, a proxy that mangled a transfer, an object replaced in
the bucket by someone with bucket credentials but not database credentials — all detectable.

**It does not prove** that the bytes are what the uploader claimed, that the video is unedited, or
that the case is honest. And it does not defend against an attacker who holds **both** the object
store and the database, because they can replace the object and rewrite the hash in the metadata row
in the same breath. That is the same threat model `docs/security.md` states for secrets: anyone who
can read and write your database has already won. Content addressing raises the floor from "evidence
can be quietly edited by one moderator with a web login" to "evidence can only be forged by someone
with infrastructure access", which is a large and worthwhile jump, and not a cryptographic
guarantee.

Signing the hashes would close that gap. It would also need a key, and the only place to keep a key
is the database — foundation §8.3's circularity, unchanged. Not pursued; noted in §20.

### 5.3 Deduplication has a deletion consequence

If one object can belong to two case files, then deleting one case file must not delete the object.
This is the reason the metadata is split in two (§7) and the reason deletion is refcounted rather
than direct. It is easy to miss when writing the happy path and catastrophic when discovered by a
moderator whose evidence vanished because somebody else tidied up a different report.

A second, smaller consequence: **dedup is never surfaced to the uploader.** "You have already
uploaded this file" tells a moderator something about a case file they may have no right to see. The
upload completes normally whether or not the bytes were already present.

### 5.4 Verification, and the hole that presigned URLs punch in it

The natural rule is *"re-hash on read"*. It is the right rule where it applies, and the recommended
backend is exactly where it does not.

A presigned GET goes **straight from the bucket to the browser**. Modbot never sees the bytes, so it
cannot hash them. Choosing the backend that removes verification is not a mistake — the alternative
costs real money and real latency on every view — but pretending the rule still holds would be. So
verification is stated three ways instead of one:

| When | What happens |
|---|---|
| Read streamed through Modbot (filesystem, database, or S3 with direct delivery off) | Hashed as it streams. A mismatch fails the read and raises a `Critical` notification. |
| Read delivered by a presigned URL | **Not verified.** Modbot verified the object when it was committed (§9.1) and verifies it again on the sweep below. |
| Background integrity sweep | A throttled job re-reads objects and re-hashes them, oldest-verified first, recording `last_verified_at`. On Railway, bucket→service egress is free, so this costs nothing but time. |
| **Export for a dispute** | Always streamed and always verified, regardless of backend, and the export states the verification result. This is the moment the property is actually being relied on. |

The sweep's rate is a setting with a conservative default, because on a provider that charges for
GET requests or egress it is the one background job that can cost money.

---

## 6. Append-only, and what "delete" means

**An existing key is never overwritten.** A `PUT` to a key that already exists is a no-op, not a
replacement. With content addressing this is nearly tautological — identical keys mean identical
bytes — but stating it as a rule is what keeps a future "re-upload to fix the rotation" feature from
being built.

Foundation §5.8.5 detects moderators with grudges by looking at the pattern of what they did.
Evidence a moderator can quietly edit undermines that completely: the pattern is only as trustworthy
as the record it is computed from.

Three distinct operations, deliberately not one:

| Operation | Effect | Reversible | Permission |
|---|---|---|---|
| **Detach** | The attachment stops appearing on the report. Bytes remain. | yes, by re-attaching | `ManageEvidence` |
| **Destroy** | The bytes are deleted from the store, once no attachment references them | **no** | `DestroyEvidence` |
| **Purge-user** | Facts about a person are erased — **evidence is not** (§15) | no | `Administrator` |

Each writes a fact: `EvidenceAttached`, `EvidenceDetached`, `EvidenceDestroyed`. The destruction fact
records the hash, the size, the content type, who destroyed it and why — everything except the bytes.
"This case had a video and an administrator deleted it on 4 March" must remain answerable forever,
because the alternative is a case file that looks like it never had evidence at all.

Destroy is refcounted: the bytes go only when the last attachment referencing that hash is gone. An
administrator destroying evidence attached to two reports is told so, by name, before it happens.

**There is no undo.** Railway Buckets has no object versioning, R2 and Wasabi have it only if
enabled, and a filesystem unlink is a filesystem unlink. Modbot must not offer a trash can it cannot
implement on the backend the operator is actually running.

---

## 7. Metadata always lives in Postgres

Whatever holds the bytes, **Postgres holds the record of them** — hash, size, content type, declared
filename, who uploaded it, when, and which report it hangs off.

This is not incidental bookkeeping. It is what makes §8's detection possible, what makes refcounted
deletion possible, what makes the storage estimate possible without walking a bucket, and what
makes an evidence list render without touching the store at all.

### 7.1 Facts and the blob record, in the shapes §5.2 already established

Attachment events are **facts**. The blob record is **derived** — mutable, and recomputable from
the facts, exactly as foundation §5.2's invariant requires.

```sql
modbot_evidence_blob                    -- derived record: one row per distinct hash
  hash            bytea  primary key    -- SHA-256, 32 bytes
  byte_size       bigint not null
  content_type    text   not null       -- from the allowlist, decided by Modbot, never by the client
  backend         smallint not null     -- which store held it when it was committed
  first_stored_at timestamptz not null
  last_verified_at timestamptz null
  state           smallint not null     -- Present | Missing | Destroyed
  ref_count       int    not null
```

`ref_count`, `state` and `last_verified_at` are the mutable columns, and every one of them is derived:
from the attachment facts, from the sweep, from the destruction facts. A rebuild is a re-run.

Attachments are facts of type `EvidenceAttached` on the fact log, carrying the hash, the report id,
the uploader as `actor_id` and the case subject as `subject_id` — which means "everything attached to
this person's case files" and "everything this moderator has ever uploaded" are both already indexed
by §5.3's existing indexes, with no new ones.

### 7.2 The ordering is load-bearing: object first, metadata second

A crash between the two writes is inevitable given enough deployments, and the two orderings fail
very differently:

- **Object, then metadata** — a crash leaves an object nobody references. Harmless. The sweep
  (§9.5) removes it.
- **Metadata, then object** — a crash leaves a metadata row with no object. That is a report
  displaying evidence that does not exist, and it is **indistinguishable from the catastrophic loss
  §8 is built to detect**. It would poison the one signal that matters.

So: always object, then metadata, everywhere, in every backend. A row in `modbot_evidence_blob` is a
claim that the bytes were confirmed present at least once, and §8 relies on that claim being true.

---

## 8. The silent-data-loss trap

**This is the most important safety property in this design.**

### 8.1 The failure

An operator picks the filesystem backend, because they are running at home and it is obviously the
simplest thing. They do not mount a volume — perhaps they did not read that part, perhaps their
compose file lost the line in an edit, perhaps the platform reset it on a redeploy. Modbot writes
evidence into the container's writable layer. Everything works. Uploads succeed, thumbnails appear,
downloads work.

Then the container is recreated — a redeploy, a restart, an image update, any Tuesday — and **every
piece of evidence the group ever collected is gone.** Nothing errors. The reports still list their
attachments, because the attachments are in Postgres. The operator discovers it four months later,
in the middle of the dispute the evidence existed to settle.

The S3 variants are the same failure wearing different clothes: a bucket renamed, a prefix typo'd, a
key rotated to a different account, a staging bucket configured in production.

### 8.2 The store marker

Detection by counting — *"the database says 400 objects exist and the store has none"* — works, but it
is a heuristic with three weaknesses: it needs sampling decisions, it cannot fire before the first
upload, and it cannot tell "empty store" from "store that is not answering".

So Modbot writes a **store marker** instead. When a backend is configured and successfully tested, Modbot
generates a UUID, records it in `Settings`, and writes it to a single well-known key in the store:

```
  .modbot-store            →  { "storeId": "…", "createdAt": "…", "deployment": "…" }
```

It is the one key in the store that is not a hash. It lives outside the `sha256/` prefix, so §5.1's
"every user-influenced key is hex" property is untouched.

On every startup, and before every upload after a failure, Modbot reads it. That turns a fuzzy
question into a three-valued one:

| StoreMarker probe | Meaning | Fires when the store is empty? |
|---|---|---|
| Present, id matches | This is the store we configured | yes |
| **Absent** | **This is not that store** — unmounted volume, fresh bucket, wrong prefix | **yes** |
| **Present, id differs** | Pointed at a *different* Modbot's store | **yes** |
| Store did not answer | Transient — network, credentials, outage | — |

The critical property is the last column. The trap can be sprung on day one, before any evidence
exists, and the store marker catches it **then** — which is the only time it can be fixed for free.

#### 8.2.1 This is not `PersistenceProbe`, and the difference is the whole point

`Modbot.Core.Configuration.PersistenceProbe` already exists and already uses a marker file —
`.modbot-persistence`, stamped with a boot id — to decide whether the log directory survives a
restart. The two mechanisms are complementary, not duplicates, and the reason is stated in
`PersistenceProbe`'s own remarks:

> *Absence of a marker is not reported as proof of ephemerality. Proving that needs memory outside
> the directory being tested.*

That is exactly right, and it is exactly why the log probe answers a different question from this
one:

| | `PersistenceProbe` | The evidence store marker |
|---|---|---|
| Question | *Will this directory survive a restart?* | *Is this the store we put our evidence in?* |
| Written | every boot, stamped with a boot id | **once, at configuration time** |
| Memory outside the directory | none — hence the refusal to infer | **`Settings` records that the store was configured and its id** |
| Absence means | first run, or a wipe — indistinguishable | **conclusively the wrong store** |
| Catches a wrong bucket or prefix | no | yes |
| Applies to | the filesystem only | all three backends |

So the evidence store has the memory outside the directory that `PersistenceProbe` correctly
declines to invent, and it therefore gets to make the stronger claim. Absence is a finding, not an
ambiguity.

Both should run, and for the filesystem backend `PersistenceProbe`'s result is the better message to
show at **configuration** time (§8.5): "this directory has not been proven to survive a restart" is
the warning that prevents the mistake, where the store marker is what detects it after the fact.

> **Corrected 2026-09-13.** An earlier version of this paragraph said Modbot should *refuse* to
> select the filesystem backend on a platform assumed ephemeral, unless overridden. That
> contradicted this document's own §3 and the rule settled in `f21b2a6`, and it should not be
> implemented.
>
> **Modbot warns; it does not refuse.** Platform detection is a suspicion and can never be more
> than one — Railway, Fly.io and Render all support mountable volumes, so the operator who mounted
> one would be told their disk is ephemeral when it is not. They know whether they mounted it;
> Modbot does not. The settings page says plainly that the directory could not be proven to survive
> a restart and that object storage is strongly recommended, and offers **Use anyway**.
>
> The acknowledgement is recorded — who gave it, when, and **the exact warning text they were
> shown**, stored verbatim. A reworded warning must not retroactively change what somebody agreed
> to.
>
> Note what is *not* softened by this. The store marker lock (§8.3) still fires on an absent, foreign
> or malformed store marker, because those are evidence of real loss or a wrong store rather than a
> guess about a platform. The distinction this correction draws is exactly that one: **a suspicion
> never blocks; proof always does.**

### 8.3 The precise rule

```
  at startup, and on the first upload after any store failure:

    probe the store marker
      ├─ matches          → healthy. Also sample-check the N most recently stored
      │                     blobs; any missing ones are marked Missing individually
      │                     and reported, but do not lock the store state.
      ├─ absent, or
      │  id differs       → LOCK EvidenceStoreUnavailable   (§8.4)
      └─ no answer        → transient. Retry with backoff. Uploads fail with a
                            "temporarily unavailable" message. Do NOT lock, do NOT
                            alarm the operator on the first failure.
```

Separating "the store said no" from "the store said nothing" is what keeps a thirty-second bucket
blip from raising a full-width red banner about destroyed evidence. A false alarm here is not
harmless: it teaches the operator to dismiss the banner, and the banner has exactly one job.

The sampled-existence check is a second, independent signal for the *partial* case — individual
objects deleted by hand, a bucket lifecycle rule someone added on a provider that has them, bit rot.
Partial loss is a different failure from wholesale loss and gets a different, quieter response: mark
those blobs `Missing`, report them in a list the operator can act on, keep running.

### 8.4 What Modbot does — and why it is not "refuse to start"

Refusing to start is the instinctive answer. It is wrong, for three reasons, the first of which
settles it:

1. **It converts an unrecoverable past loss into an ongoing present loss.** The evidence is already
   gone; nothing about staying down brings it back. But a Modbot that is not running is not ingesting
   the audit log, not recording presence, not syncing bans — and foundation §5.1 is explicit that the
   history it fails to record while it is down **cannot be filled in later**. Trading live data collection
   for a dramatic gesture about data already lost is a bad trade in the only direction that matters.
2. **On the platform this targets, it hides the message.** A container that exits fails its
   healthcheck, and Railway rolls the deployment back and reports "deployment failed". The operator
   sees a build-shaped problem, not "your evidence volume is not mounted", and the logs carrying the
   real message scroll past in a container that no longer exists.
3. **It locks the operator out of the fix.** Configuration lives in the database (foundation §2.6),
   and the only way to change the storage backend is the settings page — inside the application that
   is refusing to start.

So the behaviour is a **locked degraded state**, `EvidenceStoreUnavailable`:

- **Modbot starts**, and every unrelated function works normally. Bans, audit ingest, Discord, the
  overlay, analytics: untouched.
- **Uploads are refused**, with the reason stated. Accepting an upload into a store that has just
  demonstrated it loses everything is worse than refusing it.
- **Evidence reads return the state, never a 404.** "Unavailable" and "destroyed" and "never existed"
  are three different facts about the world and the UI says which.
- **`/health/ready` stays ready.** Readiness gates the deployment, and rolling back to the previous
  image does not remount a volume. Failing readiness would make the platform undo the wrong thing.
- **A `Critical` notification** goes out through `INotifier` (foundation §4.5) — every available
  channel, immediately. This is the severity class that already includes "sync stopped".
- **A full-width banner on every page of the web UI**, not a log line and not a toast. It names the
  backend, the expected store id, what was found, and the number of blobs the database believes exist.
  Only an `Administrator` can acknowledge it, and acknowledging it does not clear it.
- **The lock does not clear itself.** If the volume is mounted correctly on the next deploy and the
  store marker reappears, the state resolves — but the incident is a fact on the log, permanently, and
  the affected blobs stay marked `Missing` until they are verified present.

That last clause is deliberate. A misconfiguration that fixes itself between two deploys, leaving no
trace, is how an operator concludes the warning was spurious.

### 8.5 The cheapest detection is the one at configuration time

None of the above should ever fire, because the settings page catches it first. Saving a storage
backend performs a **round trip before the setting is persisted**: write a canary object, read it
back, compare the bytes, delete it, then write the store marker. Only then is the configuration saved.

A backend that cannot pass that cannot be selected, and the error says which step failed —
credentials, endpoint, URL style, permissions, or a read that returned different bytes than were
written.

This is also the moment to state the durability properties plainly, since it is the moment the
operator is making the decision: whether the store has backups (Railway Buckets does not), whether
deletes are recoverable (they are not), and that the operator, not Modbot and not the provider, owns
this data.

---

## 9. Upload

### 9.1 One state machine, three transports

Every upload, on every backend, is the same three phases:

```
  1. BEGIN    POST /api/evidence/uploads
              → { uploadId, maxBytes, acceptedTypes, target }
              target is either a presigned PUT URL (S3) or a Modbot endpoint.

  2. TRANSFER the bytes go to `target`, once, as a raw body.

  3. COMMIT   POST /api/evidence/uploads/{uploadId}/commit
              Modbot reads the staged bytes back, hashes them, validates type
              and size, moves them to sha256/<hash>, deletes the staging object,
              then writes the metadata row (§7.2) and the attachment fact.
```

Bytes are never attached to a report until phase 3 completes. A moderator who closes the tab
mid-upload leaves a staging object and nothing else — no half-evidence on a report, ever.

**No multipart form encoding.** The metadata goes in phase 1 as JSON, the bytes go in phase 2 as a
raw body. Multipart parsing on a 100 MB request is a parser, a buffer-to-disk default, and a class of
bug, in exchange for nothing here. It also would not work for the presigned path, and one path is
better than two.

The staging key is derived from the upload id, so a retry of the same upload overwrites rather than
accumulating.

### 9.2 The presigned path is also the cheap path

On S3 backends the browser uploads **directly to the bucket** with a presigned PUT, and Modbot then
reads the staged object back to hash and validate it.

That looks like extra work — the bytes cross the network twice. On Railway it is strictly cheaper
than proxying, because of §4.1.1's asymmetry: the browser→bucket leg is not service traffic, the
bucket→Modbot read is free bucket egress, and the staging→final move is a server-side `CopyObject`,
which is a free API operation and never leaves the provider. Proxying the upload through the app
would charge service egress on every byte.

It is also the only way to keep hashing. **Handing out a presigned PUT means Modbot does not see the
bytes**, so it cannot hash, cannot enforce the cap, and cannot check the type — a hole the
recommendation to "just presign uploads" does not close on its own. Reading the object back closes
it, and closes it *before* anything is attached to a report.

Two things must still be attempted at the presign, because a rejected upload is better than a
verified-then-discarded one:

- a presigned POST policy with `content-length-range` where the provider supports it, and
- a `Content-Type` condition restricting the upload to the declared type.

Whether Tigris honours either is unverified — §20.

### 9.3 Enforcing the cap, while streaming, three times

Default **100 MB per file**, operator-raisable in settings. Also a per-report total and a
per-deployment total, both defaulting to generous, because the per-file cap alone does not stop
forty files.

Never buffer and then check:

1. **Before a byte moves** — reject at phase 1 if the declared size exceeds the cap, and reject at
   phase 2 on `Content-Length` when it is present. Cheapest possible refusal.
2. **While streaming** — a counting wrapper around the read that aborts the moment the cap is
   exceeded. `Content-Length` is a claim, and chunked bodies do not make one at all.
3. **At commit** — the authoritative check, on the size actually stored, because on the presigned
   path phases 1 and 2 were enforced by somebody else's server.

Kestrel's request body limit is raised **on the upload endpoint only**, never globally. A 100 MB body
limit on the JSON API is a denial-of-service surface for no benefit.

### 9.4 When it goes wrong

| Failure | Behaviour |
|---|---|
| Connection drops mid-transfer | Nothing is committed. The staging object is orphaned and swept. The UI offers retry, which reuses the upload id and the staging key. |
| Bytes do not match the declared type | Rejected at commit, staging object deleted immediately, nothing attached. The type is decided by **content inspection**, not by the client's claim and not by the filename. |
| Over the cap | Rejected at whichever of the three checks catches it first. |
| Store unreachable at phase 1 | Upload refused with a clear message. **No queue** — the stateless profile has nowhere to queue 100 MB, and a queue that silently holds evidence is its own §8. |
| Store unreachable at commit | The upload is retryable: staged bytes may still be there. Commit is idempotent on the upload id. |
| Modbot dies between transfer and commit | Orphan staging object, swept. The moderator re-uploads. |
| Modbot dies between the object move and the metadata write | Orphan final object, swept after a longer grace period. §7.2's ordering is what makes this the harmless direction. |

### 9.5 Sweeping orphans is Modbot's job, not the bucket's

There are no lifecycle rules to lean on (§4.1.1). So a scheduled job:

- deletes staging objects older than a configurable grace period (default 24 hours, long enough that
  a slow upload on a bad connection is never swept out from under itself);
- deletes final objects with no referencing attachment and a much longer grace period, and **only**
  when the store is healthy — sweeping while `EvidenceStoreUnavailable` is locked would be deleting
  based on a database whose relationship to the store is exactly what is in doubt.

Both record what they removed. A sweep that deletes an object it should not have kept no record of is
the second-worst bug in this design.

### 9.6 Resumability: not in the first version, and here is the arithmetic

100 MB on a home connection is 80 seconds at 10 Mbit up and around seven minutes at 2 Mbit. That is
long enough for failures to be real rather than theoretical, and long enough that a UI without a
genuine progress indicator gets cancelled by an impatient human.

S3 multipart upload would give real resumability, with presignable parts. It would also need a
parallel implementation for the two backends that have no multipart, a part-tracking table, and a
second orphan-sweep case for abandoned multipart uploads. That is a large amount of machinery for a
100 MB ceiling.

**Decision: retry, not resume**, in the first version:

- a real progress indicator, with bytes and rate, so cancellation is informed rather than anxious;
- a stable staging key per upload id, so a retry does not accumulate garbage;
- the cap keeps the worst retry bounded.

Revisit if operators actually report failed uploads. Recorded here so the revisit starts from the
reason rather than from scratch.

---

## 10. Serving evidence

A moderation tool where staff routinely open files uploaded by other staff is a near-ideal
stored-XSS target: the attacker is already authenticated, the audience is exactly the people with the
most permissions, and the delivery mechanism is a feature.

### 10.1 An allowlist, and SVG and HTML are not on it

| Accepted | `image/png`, `image/jpeg`, `image/webp`, `image/gif`, `video/mp4`, `video/webm` |
|---|---|
| **Refused** | everything else, explicitly including `image/svg+xml`, `text/html`, and anything unrecognised |

**SVG is a script execution format wearing an image's file extension**, and it is the single most
common way an "image upload" becomes an XSS. HTML needs no explanation. Refusing them outright costs
a moderator nothing — nobody attaches a vector drawing as evidence of harassment — and closes the
category.

The type is decided by **inspecting the leading bytes**, never by the client's `Content-Type` header
and never by the filename extension. A file that does not match one of the accepted signatures is
rejected at commit, whatever it claims to be. The `content_type` recorded in §7.1 is Modbot's
determination, and it is the only value ever sent back to a browser.

Container formats are not a complete defence — an MP4 is a container and a decoder is a decoder — but
the combination of a signature check, an allowlist, attachment-only delivery and browser-native
decoding is a proportionate answer for a self-hosted tool used by a dozen people.

### 10.2 Headers, for every byte Modbot serves

- `Content-Disposition: attachment; filename="…"` — the filename sanitised and quoted.
- `Content-Type:` the allowlisted type from §7.1, never a client-supplied string.
- `X-Content-Type-Options: nosniff`.
- `Content-Security-Policy: sandbox` — so that anything which did slip through the allowlist renders
  in an opaque origin with no script, no forms and no same-origin access. This is the strongest
  single mitigation available without a second hostname.
- `Cache-Control: private, no-store` on presigned issuance, and on the bytes themselves whatever the
  operator's deployment allows. Content-addressed objects are immutable, so long caching is safe for
  the *bytes*; it is the URL that must not be cached anywhere shared.

### 10.3 Presigned delivery, and what control it costs

A presigned GET can pin the response by signing `response-content-disposition` and
`response-content-type`, which covers the two most important headers. It **cannot** add
`X-Content-Type-Options` or a CSP, because those are not response-override parameters in the S3 API.

So presigned delivery is slightly weaker than proxied delivery, and the honest statement is that the
allowlist is doing the work there rather than the headers. Given that SVG and HTML cannot be stored
in the first place, that is an acceptable position — but it is a position, and an operator running a
particularly sensitive deployment should be able to turn direct delivery off and pay the egress. That
is a setting.

**Presigned URLs are short-lived — five minutes — and single-use is not achievable.** Anyone holding
the URL within its window can fetch the object without authenticating. That is intrinsic to
presigning, it is why the window is short, and it is why §14's access fact records *issuance* rather
than retrieval.

### 10.4 Previews without a second hostname

`Content-Disposition: attachment` means a plain `<img src>` will not render, which is the point.
A moderation UI still needs to show a screenshot without a download-and-open round trip.

The textbook answer is a **separate origin** for user content, and it is the right answer in
principle: it makes even a successful XSS land somewhere that has no session and no same-origin
access to the app. The problem is that it costs a second hostname, a second certificate and a DNS
step, in a product whose setup is deliberately *"click the template, open the URL, follow the
wizard"* (foundation §8.3). Requiring it would be the same kind of mistake as requiring an encryption
key in an environment variable.

So it is **recommended and optional**: if the operator configures an evidence hostname, Modbot serves
inline previews from it under a strict CSP. If they do not, previews are rendered by the SPA fetching
the bytes with credentials and creating a `blob:` URL **with the type taken from §7.1's allowlist
rather than from the response** — so the bytes are never reachable at a navigable same-origin URL,
and the object URL can never be typed as HTML.

### 10.5 Range reads

`<video>` seeking requires HTTP range requests. S3 handles them natively; the filesystem backend
seeks the file; the database backend uses §4.3's chunking, which is most of why it chunks. A backend
that cannot answer a range request produces video that plays only from the start, which a moderator
reviewing a twenty-minute clip will discover at the worst moment.

---

## 11. Never transcode, never strip

Both alter the evidence. A re-encoded video is not the video the moderator attested to, and a
stripped image is not the image they saw. The hash in §5.1 is a claim about bytes; the moment Modbot
rewrites the bytes, the claim is about Modbot's output rather than about the evidence.

### 11.1 EXIF is disclosed, not removed

Photographs carry GPS coordinates, device serial numbers, and capture timestamps. A moderator
attaching a phone photo may be attaching their own home location without knowing it.

The quiet fix — strip it on upload — is the wrong one twice over: it destroys metadata that is
sometimes the most probative thing in the file (a capture timestamp that contradicts a claim), and it
rewrites a file the uploader is attesting to.

So Modbot **reads** the metadata and **shows** it, at the moment of upload, before the moderator
confirms:

> This image contains location data (37.42° N, 122.08° W) and a capture time of 2026-03-04 21:14.
> It will be stored exactly as it is. Remove it yourself first if you do not want it kept.

Reading metadata is parsing, not decoding — no image codec is invoked, and the parser is bounded and
fails closed. If it cannot parse the metadata it says nothing rather than guessing.

The same disclosure appears to anyone later viewing the evidence, because the metadata is part of
what the file says.

### 11.2 No server-side decoding, and therefore no generated thumbnails

The obvious next feature is a thumbnail grid. It requires decoding attacker-influenced media on the
server — an image library or, for video, ffmpeg in the container. Both are large dependencies with
long CVE histories, and both would be processing exactly the files §10 assumes are hostile.

**The browser is a better decoder than anything shippable here**, hardened by more attention than
this project will ever have, and it is already going to render the file anyway. So previews are
`<img>` and `<video>` elements sized by CSS (§10.4), and the container gains no media dependency.

The cost is bandwidth: a grid of screenshots downloads full-size images. On the recommended backend
that bandwidth is free and comes from the bucket, which is the same asymmetry §9.2 already exploits.
If generated thumbnails are ever revisited, they are **derived cache objects** keyed by
`sha256 + variant`, never evidence, never exported, and regenerable — never a substitute for the
original anywhere it is relied on.

---

## 12. The profile snapshot is not blob storage

Rendering the subject's profile to a PNG and storing it as a blob is the intuitive implementation and
it throws away the only thing that makes the snapshot valuable.

*"Show me everyone we banned in the last year whose bio mentioned this Discord invite"* is a real
moderation question — it is how a group finds the rest of a coordinated group after catching one
member. It is answerable in one query if the snapshot is structured data and unanswerable if it is a
picture. Foundation §5.3 already puts a GIN index on `data`, so the capability is there for free.

### 12.1 It is a fact, with `jsonb`

A `SubjectProfileSnapshot` fact, subject = the person actioned, actor = the moderator whose action
prompted it, `data` carrying display name, prior display names, bio, status and status description,
pronouns, account age and date joined, the avatar and profile image ids, the group roles held, and
the raw field set Modbot received — as received, not normalised, since a normaliser written today
will silently drop a field VRChat adds next year.

It is small: a few kilobytes of mostly text, against 326 bytes for an ordinary fact. Even a group
banning ten people a day adds single-digit megabytes a year.

**No id is validated on the way in** (foundation §3.1.1), and no field is required to be present.

### 12.2 Captured when the report is opened, not after the ban lands

The natural reading of "a snapshot at ban time" is *fetch the profile when the ban is submitted*.
Better: M4 §7 already has Modbot **pre-filling the report with what it knows**, which means it is
already fetching the subject's profile at the moment the moderator opens the form. The snapshot is
that fetch, preserved.

This is strictly better on three counts:

- **It costs zero additional requests** against a budget foundation §4.3 describes as opaque and
  punitive.
- **It is taken before the action**, which is when the profile still reflects the behaviour being
  moderated. A profile fetched after a ban may already have been edited in response to it.
- **It cannot delay or fail the ban.** M3 §7.3.1's rule — never block moderation on a lookup —
  applies unchanged. If the profile fetch failed, the report records that it failed, and the ban
  proceeds.

The snapshot records `captured_at` and whether it preceded the action. A snapshot taken forty minutes
after the fact is still useful and must not pretend to be contemporaneous — the same honesty
foundation §5.3 enforces with `occurred_before`.

### 12.3 The profile image is bytes, and it needs a rate-limit answer first

Everything above is text. The avatar and profile images are not, and they are the part of a profile
most often changed immediately after a ban — which makes them the part most worth capturing and the
part Modbot cannot capture from a `jsonb` column.

Captured images are **evidence blobs with a different provenance**: same store, same content
addressing, `origin = Captured` rather than `Uploaded`. The distinction matters because §11's
never-transcode rule applies to them for a different reason (Modbot did not author them and must not
appear to have), and because §14's permissions may reasonably treat an avatar thumbnail differently
from a video.

**This does not ship until the rate limit question is asked.** Per foundation §4.3.4's standing
instruction:

- The profile text comes from **`users.read`**, which already exists as a class at a provisional
  0.33 req/s, and §12.2 makes it a request that was happening anyway. No new budget.
- The **images are not that endpoint.** They are files served from VRChat's file/image hosts, and no
  limit has been measured for them. §4.3.4 forbids inferring one from a neighbour, and §4.3.4.1 is a
  worked example of exactly that mistake. So image capture needs its own class, its own lane, its own
  cold stop — and somebody has to ask what the limit is first.
- **Group membership is deliberately left out of the first version.** The obvious source is
  `users.groups`, which §4.3.4.1 isolated precisely because nothing has measured it, on the reasoning
  that a 429 there should cold-stop only group selection. Adding a second caller widens that blast
  radius to include opening a ban report, and a cold stop that blocks a ban is not acceptable. If the
  groups list is wanted, it needs its own answer, not a borrowed lane.

---

## 13. The interface

### 13.1 Shape

```csharp
public interface IEvidenceStore
{
    EvidenceStoreCapabilities Capabilities { get; }

    // Writes to a staging key. Returns the hash computed while streaming and the byte count.
    Task<StagedObject> StageAsync(string uploadId, Stream body, long maxBytes, CancellationToken ct);

    // Server-side move from staging to sha256/<hash>. No bytes cross the app on a store
    // that can copy internally.
    Task CommitAsync(string uploadId, ReadOnlyMemory<byte> hash, CancellationToken ct);

    Task<Stream> OpenReadAsync(ReadOnlyMemory<byte> hash, ByteRange? range, CancellationToken ct);
    Task<ObjectStat?> StatAsync(ReadOnlyMemory<byte> hash, CancellationToken ct);
    Task DeleteAsync(ReadOnlyMemory<byte> hash, CancellationToken ct);

    // Null when, and only when, the corresponding capability is absent (§13.2).
    Task<Uri?> TryCreatePresignedReadAsync(ReadOnlyMemory<byte> hash, TimeSpan ttl, CancellationToken ct);
    Task<Uri?> TryCreatePresignedWriteAsync(string uploadId, long maxBytes, CancellationToken ct);

    // The §8 probe. Never throws for "absent"; absent is a result, not an error.
    Task<StoreProbe> ProbeAsync(CancellationToken ct);
}

[Flags]
public enum EvidenceStoreCapabilities
{
    None            = 0,
    PresignedRead   = 1 << 0,
    PresignedWrite  = 1 << 1,
    RangeRead       = 1 << 2,
    ServerSideCopy  = 1 << 3,
}
```

### 13.2 A capability is declared, not discovered by exception

Presigned URLs are the one thing the three backends genuinely disagree about. A design where the
filesystem store throws `NotSupportedException` from `CreatePresignedUrl` pushes that disagreement
into a runtime failure on a path that only some deployments exercise — which means it is discovered
in production, by an operator, on the backend the developer did not run.

M3 §7.3 already set the precedent for the alternative: file-id support in an avatar provider is *"a
capability flag, not a detail"*, declared in a table and shown in the settings UI so an operator
cannot pick a provider that silently never works. Same treatment here.

The rule is a biconditional, and it is what makes the capability testable rather than documented:

> `TryCreatePresignedReadAsync` returns non-null **if and only if** `Capabilities` includes
> `PresignedRead`.

Callers branch on the capability. Nothing throws. And the settings page shows, per backend, whether
evidence is delivered directly by the store or streamed through Modbot — because that difference is
visible to the operator as bandwidth and as latency, and they should be able to see why.

### 13.3 One conformance suite, three backends

A single test suite runs against all three implementations and asserts the contract rather than the
implementation: content addressing, idempotent writes, the append-only rule, range reads where
declared, the capability biconditional above, and — most importantly — **the §8 probe's three-valued
result**, including the transient case, which is the one that is never exercised by accident.

The filesystem and database backends run against a temporary directory and Testcontainers Postgres as
the existing data tests do (`CLAUDE.md`). The S3 backend runs against MinIO in a container, which is
also the closest available stand-in for the non-AWS providers this has to work with.

---

## 14. Permissions

New flags on `ModbotPermissions`. Bits 0–14 are taken and 62 is `Administrator`; these take the next
three, and the enum's own rule applies — **never renumber one**.

| Flag | Bit | Grants |
|---|---|---|
| `ViewEvidence` | `1 << 15` | See and download the files attached to a case file |
| `ManageEvidence` | `1 << 16` | Attach to, and detach from, a report |
| `DestroyEvidence` | `1 << 17` | Permanently delete bytes (§6) |

**`ViewEvidence` is separate from `ViewAuditLog`, and that separation is the point.** Evidence about a
person — possibly video of them, in their own words and their own voice — is categorically more
sensitive than the line of audit log it hangs off. A moderator who needs to know that this user was
banned for harassment does not automatically need to watch it happen. The existing split between
`ViewAuditLog` and `ViewOperationalLog` is the same instinct applied one level up.

Attaching evidence **to a report you are filing** is covered by the action permission — `Ban`, `Kick`,
`Warn` — because a report nobody can attach evidence to is a report with no evidence.
`ManageEvidence` is for touching **somebody else's** report, which is a different act.

`DestroyEvidence` should be held by the fewest people in the deployment, and the UI says so at the
moment of granting it. It is not granted by any default role.

### 14.1 Access is a fact

Every view and every download writes an `EvidenceAccessed` fact: who, which blob, which report, when.

Modbot does not log reads anywhere else, and the exception is deliberate. This is the one asset where
misuse is invisible by nature — a moderator who downloads a video of someone for reasons that have
nothing to do with the case leaves no trace otherwise — and it is the one where a subject may
legitimately ask who has looked at it.

Two honest limitations:

- On the presigned path the fact records **that a link was issued**, not that the object was fetched,
  because the fetch never touches Modbot. That is a weaker claim and the UI says so.
- The window is five minutes and the URL is not single-use (§10.3).

Access facts are `Presence`-class for retention purposes, not `Moderation`: they are about staff
behaviour over time, and a group that wants a window on them should be able to have one.

---

## 15. Retention, purge, and the exception

### 15.1 Evidence survives purge-user

`docs/data-retention.md` currently promises that **deletion works regardless of retention settings**.
After this spec that is no longer unconditionally true, and the honest thing is to say so loudly
rather than to quietly narrow it.

Purge-user already has two stated exceptions: records of actions the purged person *performed* on
someone else, and the fact that a purge occurred. This adds a third, and it is larger:

> **A case file survives purge-user.** The written rationale, the attached evidence, and the profile
> snapshot taken at the time of the action are retained.

The reasoning is the one already used for the first exception. A case file is **the group's record of
its own decision** — the justification for an act the group performed and may have to defend. Erasing
it on request would let anyone who was banned delete the evidence of why, which turns purge-user into
a tool for laundering a moderation history. Foundation §5.8.5's grudge detection depends on those
records existing; M8's federation depends on them being defensible.

Everything else about that person still goes: every presence fact, every avatar change, every session,
every aggregate counted against them. What remains is a case file about an action the group took.

### 15.2 The purge receipt says what was kept and why

A purge that silently retains things is worse than one that retains nothing, so the receipt is
explicit:

> Purged 41,208 facts for `usr_…`.
> **Retained: 2 case files** (bans on 2026-01-14 and 2026-04-02), including their written rationale,
> 3 evidence files, and the profile snapshots taken at the time. Case files are the group's record of
> its own moderation decisions and are retained under the Moderation retention class.

Named, counted, and reasoned — not a footnote. The operator can then decide, as a separate and
deliberate act, whether to destroy a specific piece of evidence with `DestroyEvidence` (§6). That
remains possible; it is simply not something a purge does automatically.

### 15.3 The retention window is a different question, and it has a different answer

A conflict this design creates that was not obvious: **purge-user spares evidence, but a configured
retention window would not.** Evidence belongs to a ban report, ban reports are Moderation-class facts,
and `ModerationFactRetentionDays` defaults to 0 — keep forever — but an operator may set it.

If evidence simply follows the class, a group that sets a two-year moderation window discovers that
its oldest case files quietly lost their video. That is §8's silent loss arriving by a different
route, this time with the operator's nominal consent and without their understanding.

The proposal, flagged as needing a decision (§20): evidence follows the Moderation class, **and**
setting a moderation retention window shows what it will destroy, in files and in bytes, before it is
saved — the same treatment §5.5 already gives the storage estimate. An operator who genuinely wants
old evidence gone can have it; one who set a window thinking about row counts is told what else it
reaches.

### 15.4 Blobs dwarf facts, and the estimate must say so

326 bytes per fact against tens or hundreds of megabytes per video is not a difference in degree.
A single 100 MB clip is roughly **320,000 facts** — about five months of the "typical group" row in
§5.5's table. The moment evidence exists, a storage page that measures only Postgres is not merely
imprecise, it is reporting the wrong number by orders of magnitude.

They also grow differently, which matters more than the ratio. Facts arrive at a rate, which is why
§5.5 extrapolates a line through them. Evidence arrives **per ban**: bursty, lumpy, and driven by
whether the group had a bad week. Fitting one straight line through both produces an estimate that
is wrong in both directions. They are measured and projected separately, and presented separately.

---

## 16. Settings, and the wizard

Storage configuration is a step in the onboarding wizard and a page under Settings → Data, and like
every other wizard step it is independently re-runnable (foundation §7.1).

It asks for: the backend, the backend's own fields, the per-file cap, the per-report and
per-deployment totals, whether direct (presigned) delivery is enabled, an optional evidence hostname
(§10.4), and the integrity sweep's rate.

It shows: the durability statement from §8.5, the current blob count and bytes, whether direct
delivery is available on the selected backend (§13.2), and — for the database backend — a standing
note about `pg_dump`.

Changing backends does **not** move existing objects. The blob record says which backend held
each object, and a migration between backends is an explicit, resumable job that copies, verifies by
hash, and only then repoints — never a side effect of saving a setting. Until that job exists,
switching backends with objects present is refused, with the reason stated.

### 16.1 Environment pre-fills the wizard; the database decides

Foundation §8.1 is unchanged: **`PORT` and `DATABASE_URL` remain the only required variables**, and
nothing here adds a required one.

But a Railway one-click template injects `BUCKET`, `ACCESS_KEY_ID`, `SECRET_ACCESS_KEY`, `REGION` and
`ENDPOINT` into the service, and making the operator retype values that are already sitting in the
environment is a needless way to lose people in a wizard.

So, exactly this:

- **At first boot only**, and **only when no evidence backend is configured**, if all five variables
  are present the storage step arrives **pre-filled**, labelled as coming from the environment, with
  the endpoint and bucket visible and the secret masked.
- The operator presses **Test and save**, which runs §8.5's round trip and writes the store marker. Only
  then is anything persisted.
- **After that the environment is never read again.** A later change to a Railway variable does not
  move the store, does not repoint anything, and does not generate a warning — it is simply ignored,
  because the database is the source of truth (foundation §2.6).

That last rule is the load-bearing one. An implementation that re-read the environment on every boot
would let a variable edit silently repoint the store at a different bucket — which is precisely the
trap §8 exists to catch, introduced deliberately by the convenience feature meant to smooth the
setup.

These names are also generic enough (`BUCKET`, `REGION`) to belong to something else entirely, which
is a second reason they are a **hint requiring confirmation** and never configuration.

---

## 17. The rationale is markdown, and markdown is an attack surface too

The written justification is authored by a moderator and rendered to other moderators. Two rules:

- **Rendered with raw HTML disabled and the output sanitised.** No embedded HTML, no `javascript:`
  or `data:` URLs, external links marked and `rel="noopener noreferrer"`.
- **Images in the rationale resolve only to evidence attached to the same report** — an internal
  reference scheme, never an arbitrary URL. Otherwise a case file becomes a tracking pixel: an image
  pointing at an external server that fires whenever any staff member opens the report, reporting
  their IP address and the time they read it. That is a surveillance channel pointed at moderators,
  created by a feature nobody asked for, and the fix is to never resolve external image URLs at all.

---

## 18. What this asks of code that already exists

Tasks, not changes. Nothing here is implemented by this document.

| # | Where | Task |
|---|---|---|
| 1 | `src/Modbot.Analytics/Storage/` | `StorageEstimator` measures Postgres only. Add a **blob dimension**: `BlobBytes` and `BlobCount` on `StorageMeasurement`, sourced from a running total in the blob projection rather than from a `ListObjects` walk — LIST is slow everywhere and billed on Wasabi and R2 even though it is free on Railway. Project blobs on their own curve (§15.4), never folded into the fact line. |
| 2 | `src/Modbot.Api/` | First file-upload surface in a JSON-only API. Add the three endpoints of §9.1 with a per-endpoint body limit, never a global one. |
| 3 | `Dockerfile` | Do **not** add `VOLUME` (§3.1). Document `/app/data` as the mount point. Nothing else changes. |
| 4 | `.railway/railway.ts` | No volume for the stateless profile. A bucket is a separate resource the operator adds; the template may reference it, but nothing becomes required. |
| 5 | `src/Modbot.Core/Logging/`, `src/Modbot.Core/Configuration/` | Largely **already done**: the file sinks now switch off unless `PersistenceProbe` has evidence the directory survives a restart. What remains is to point the log directory at the same data root the filesystem evidence backend uses, so one mounted volume serves both, and to size it — the worst case at the current caps (64 MB/file; 60 main, 60 http, 6 debug, each in two formats) is ~15–16 GB, which the docs must state so an operator does not mount a 10 GB volume and have evidence and logs compete for it. |
| 6 | `src/Modbot.Core/Data/Entities/ModbotPermissions.cs` | Three new flags at bits 15–17 (§14), pinned by `ModbotPermissionsTests` like the rest. |
| 7 | `src/Modbot.Core/Data/Entities/Settings.cs` | Storage backend, its fields, the caps, the store marker id, direct-delivery toggle, sweep rate. Secrets encrypted like every other secret column. |
| 8 | Host startup | The §8.3 probe, the lock, the `Critical` notification and the banner state. |

---

## 19. Non-goals

- **The client never uploads anything.** M3 §3.1 stands unchanged and unamended: the Windows client
  and the SteamVR overlay read VRChat's log directory and nothing else, and they have no file-upload
  path. Evidence is attached through the web UI by a human, on a machine, deliberately. Reading
  VRChat's own screenshot folder — which is right there, and tempting — is explicitly forbidden.
- **No automatic capture of anything.** Modbot never records a moderator's screen, never records
  audio, and never captures a video of an instance. The only bytes that enter the system are ones a
  human chose to attach, plus the profile images of §12.3.
- **No content analysis.** Modbot does not run detection, classification, or recognition over
  evidence. A file is stored and served; it is not interpreted. (M8's flagging works on facts, not on
  media, and that is unchanged.)
- **No public sharing.** No public links, no anonymous access, no "share this case file" URL. Every
  access is authenticated and permissioned, and presigned URLs are short-lived derivatives of an
  authenticated request rather than a distribution mechanism.
- **Evidence is not in the read API.** The `ApiKey`-scoped read API (foundation §7.3) exposes facts
  and aggregates. It does not expose evidence bytes or presigned URLs.
- **No editing, cropping, annotating or redacting.** §11.
- **No federation of evidence.** M8 §5.1's rule that no moderation data leaves the deployment is
  unaffected; a federated signal carries an assertion, never a file.

---

## 20. Open questions

Honest ones. Several of these gate implementation rather than merely informing it.

1. **Does Tigris (Railway Buckets) honour `response-content-disposition` and `response-content-type`
   on presigned GETs?** §10.3 depends on it. If not, direct delivery on Railway cannot force
   attachment disposition, and either direct delivery is off by default there or the allowlist is
   doing all the work alone.
2. **Does it support presigned POST policies with `content-length-range`?** §9.2's pre-upload cap
   enforcement depends on it. If not, a moderator with a valid session can push an arbitrarily large
   object into the bucket and Modbot only discovers it at commit — which is survivable (the object is
   deleted, nothing is attached) but means the operator can be billed for bytes Modbot rejected.
3. **Does it validate `x-amz-checksum-sha256` on PUT?** If so, the store itself rejects a corrupted
   upload and §5.4's verification gets stronger for free.
4. **The image endpoint's rate limit** (§12.3). Per foundation §4.3.4 this must be **asked**, not
   inferred. Which host serves profile and avatar images, whether the authenticated session cookie is
   required, and what the limit is. Until answered, image capture does not ship.
5. **Whether group membership belongs in the snapshot at all** (§12.3), given `users.groups` is
   isolated by §4.3.4.1 specifically because nothing has measured it.
6. **Virus scanning.** Staff downloading each other's uploads is a malware delivery path, and the
   allowlist does not address a malicious MP4. ClamAV is a large dependency for a small container and
   its signatures need updating, which reintroduces an outbound dependency. Options: ship without it
   and rely on attachment-only delivery, make it an optional external hook, or make it an optional
   sidecar for the persistent profile. Unresolved.
7. **Should evidence be encrypted at rest by Modbot?** The circular-key problem of foundation §8.3 is
   unchanged — any key lives in the same database — so the answer is probably the same "no, encrypt
   the storage layer". But video of a person is a different category of data from a VRChat password,
   and the answer deserves to be reached rather than inherited.
8. **Retention versus evidence** (§15.3). Does evidence follow the Moderation retention class, or is
   it never expired by a window at all? The proposal is "follows the class, with the consequence
   shown before saving", but a reasonable group might expect a case file to be permanent in a way its
   surrounding facts are not.
9. **Cap defaults** (§9.3). 100 MB per file is the decided default. The per-report and
   per-deployment totals are not, and they need a number that does not surprise a group on a Railway
   plan when the bill arrives.
10. **Wasabi's minimum storage duration** (90 days on deleted objects) means destroying evidence
    there does not reduce the bill for three months. Worth surfacing in the backend comparison, or
    too provider-specific for the settings page? Leaning toward a line in the docs rather than in the
    UI.
11. **Whether a second origin should eventually be required** rather than recommended (§10.4). It is
    the strongest available mitigation and it is the one that costs setup friction. If the evidence
    viewer grows beyond `<img>` and `<video>`, the answer probably changes.
12. **What an export looks like.** §5.4 promises verified export for a dispute, but not its shape — a
    zip with a manifest of hashes, a signed statement, something else. It needs designing before the
    first group actually needs one, which is the worst possible time to design it.
