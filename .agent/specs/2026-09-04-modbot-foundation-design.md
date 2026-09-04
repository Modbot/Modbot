# Modbot — Foundation Design (M0–M2.5)

- **Date:** 2026-09-04
- **Status:** Draft, awaiting approval
- **Covers:** M0 (foundation), M1 (group sync), M2 (audit log), M2.5 (dossier + metrics), basic Discord bot
- **Supersedes:** the SaaS-era implementation preserved in `old/` (gitignored reference clone of `Modbot/Modbot`)

---

## 1. What Modbot is

Modbot is a **self-hosted moderation, analytics and automation appliance for a single VRChat group.**
It caches what VRChat's API exposes but its own UI does not make usable, records the history VRChat
throws away, and gives a group's staff tools VRChat does not provide at all.

It is **not** a SaaS product. It is **not** multi-tenant. One deployment serves one VRChat group.

Three surfaces, sharing one backend:

1. **Web tool** — the primary operator UI.
2. **Discord bot** — a second surface for groups that live in Discord.
3. **Windows client + SteamVR overlay** — in-instance monitoring (M6, separate spec cycle).

Plus `my.modbot.co`: a static page that stores a list of Modbot instance URLs in `localStorage`
and redirects to the one you pick, in the manner of Home Assistant. It holds no data and has no backend.

---

## 2. Decisions

Each of these was settled during brainstorming. Rationale is recorded so future contributors
(human or agent) do not relitigate them.

### 2.1 Language: .NET for everything except the web UI

**.NET 10** for backend, Discord bot, Windows client and SteamVR overlay.
**TypeScript + React + Vite** for the web UI only.

| Reason | Detail |
|---|---|
| The overlay decides it | OpenVR overlays in .NET are a solved problem. In TypeScript this means Electron plus native `openvr_api` bindings — the piece most likely to rot unmaintained. |
| SDK ownership | The maintainer maintains `vrchatapi/vrchatapi-csharp`. When VRChat breaks the API, the dependency is fixable same-day rather than filed as an issue. |
| Review capability | The maintainer has 10 years of .NET against 2 of TypeScript. An agent writes the code either way; a human must be able to judge whether it is correct, for years. |
| Contributor pool | The VRChat ecosystem is C#-literate (UdonSharp, the SDK, client mods). |

Rejected: full-TypeScript (loses the overlay), Blazor for the web UI (charting and data-grid
ecosystems are far behind React, and M2.5 needs both).

### 2.2 License: AGPL-3.0

**`LICENSE` is GNU Affero General Public License v3.0**, verbatim. Modbot **is** open source under
the OSI definition and may describe itself as such.

| File | Contents |
|---|---|
| `LICENSE` | **AGPL-3.0**, verbatim |
| `CLA.md` | Contributor License Agreement. A license cannot compel upstreaming; only a contribution agreement can. Also required to keep copyright consolidated so relicensing remains possible. |
| `TRADEMARK.md` | "Modbot" and its logo are excluded from the license grant. Forks may not use the name. |

**What AGPL does and does not deliver**, recorded so it is not misremembered later:

- ✅ Anyone may use, modify, self-host and fork it.
- ✅ Anyone who runs a **modified** Modbot as a network service must offer their source to its users.
  This is the clause that prevents a closed-source hosted fork.
- ✅ Copyright remains with the author; the trademark is separate and unlicensed.
- ❌ **It does not prohibit commercial use.** Someone may lawfully run a paid Modbot hosting service,
  provided they offer modified source to their users. A non-commercial restriction (PolyForm-NC) was
  considered and rejected in favour of being genuinely open source.
- ❌ It does not compel upstream contribution. That is the CLA's job, and it is social rather than
  legal — the CLA governs what you may do with contributions you receive, not whether they arrive.

Dependency compatibility: both VRChat SDKs and Discord.Net are MIT, which is AGPL-compatible.

### 2.3 One VRChat account, REST polling

Each deployment authenticates as exactly **one** VRChat account, which must be a moderator of the
managed group. **No multi-account request distribution and no rotating proxy pools** — those are
rate-limit evasion and the fastest route to Modbot being named in a VRChat TOS conversation.

The Pipeline websocket is **not used**. It carries friend and location updates, which are not
meaningful in a group-moderation context. Ingestion is REST polling on a governed schedule.

This makes the whole system a **single-writer scheduling problem**: one token bucket, one priority
queue, N job types competing for it. That is the piece worth getting genuinely right.

### 2.3.1 One optional egress proxy — for WAF access, not evasion

Cloudflare's WAF blocks some networks outright, including many datacentre and VPS IP ranges. An
operator on such a network cannot reach the VRChat API at all — not rate-limited, **blocked**.

Modbot therefore supports **one optional, statically configured egress proxy**, set during
onboarding immediately after the VRChat account step (§7.1). This is a connectivity fix for
operators who would otherwise be unable to run Modbot at all.

The distinction from the old implementation is total and deliberate:

| Old (SaaS) | New |
|---|---|
| Pool of proxies, rotated per request | **One** proxy, or none |
| Sticky sessions encoded in the proxy username (`NetworkCredential(pw, sessionName)`) | No sessions |
| `ProxyChangeRequired` → rotate to a fresh IP and retry | WAF block → **tell the operator**, surfaced as a health state |
| Purpose: distribute load across IPs to exceed rate limits | Purpose: reach the API at all |

Volume through one proxy is identical to volume without one. Modbot never rotates IPs in response
to a block, because that is the behaviour the rate limits exist to prevent.

### 2.4 Single-tenant, single-group appliance

One deployment, one VRChat group. Deleted from the old model: `ModbotGroup` as a tenancy table,
`ModbotGroupAccount`, `ModbotRole`, `ModbotBilling`, `ModbotTransaction`, and all plan gating.

Staff are local users with username/password login (**not ASP.NET Core Identity**), optional
Discord linking, and optionally Discord-required login as a per-deployment setting.

### 2.5 Modular monolith, split-ready

One deployable (`Modbot.Host`) containing API, web UI, Discord bot and sync scheduler.
Railway template is **one service plus one Postgres**.

The modules are separate projects behind interfaces, and `IVRChatGate` is written so its
in-process implementation can be swapped for a Redis-lease implementation without touching call
sites. The seam exists; it is not exercised.

### 2.6 Configuration lives in the database

Environment variables are limited to **`PORT` and `DATABASE_URL`, and nothing else**. VRChat
credentials, the egress proxy, group selection, Discord bot token and SMTP are all entered through
a first-run onboarding wizard and stored in the database.

Deploying Modbot is: click the Railway template, open the URL, follow the wizard.

---

## 3. Repository layout

```
Modbot/
├─ .agent/                    # planning artefacts — specs, plans, research
│  ├─ specs/                  # design documents (this file)
│  ├─ plans/                  # implementation plans
│  └─ research/               # investigation notes
├─ docs/                      # REAL documentation only: setup, usage, self-hosting, API reference
├─ src/
│  ├─ Modbot.Core/            # entities, ModbotContext, migrations, domain events, facts
│  ├─ Modbot.VRChat/          # IVRChatGate, rate limiter, session management, sync jobs
│  ├─ Modbot.Analytics/       # fact emission, rollup engine, retention, segment queries
│  ├─ Modbot.Discord/         # Discord.Net bot as IHostedService
│  ├─ Modbot.Api/             # HTTP endpoints, auth, onboarding
│  ├─ Modbot.Web/             # React + Vite + TS; builds into Modbot.Host/wwwroot
│  └─ Modbot.Host/            # the single deployable; composition root
├─ tests/
│  ├─ Modbot.Core.Tests/
│  ├─ Modbot.VRChat.Tests/
│  └─ Modbot.Analytics.Tests/
├─ libs/                      # GITIGNORED — reference clones of the VRChat SDKs
├─ old/                       # GITIGNORED — previous SaaS implementation, reference only
├─ LICENSE  CLA.md  TRADEMARK.md
├─ Dockerfile  railway.json
├─ AGENTS.md → CLAUDE.md      # agent instructions
└─ README.md
```

`.agent/` holds planning artefacts and is kept distinct from `docs/`, which is reserved for real
end-user documentation: setup, usage, self-hosting and API reference. `AGENTS.md` at the repository
root points contributors and agents at `.agent/` so the dotfolder is still discoverable.

### 3.1 A standing rule: no hardcoded VRChat capacity constants

**Nowhere in Modbot may an instance capacity, member cap, or similar VRChat limit be written as a
constant.** VRChat grants per-group exemptions that raise the usual 80-user instance ceiling to 200
or 300, and those exemptions change without notice.

Capacity is **data read from the API**, never a compile-time assumption. This applies to UI
progress bars, capacity warnings, pagination sizing, presence-buffer allocation and analytics
bucketing alike. A group that received an exemption must not see Modbot report "80/80 — full" for
an instance holding 240 people.

---

## 4. Runtime architecture

```
   MemberSyncJob   ──┐
   BanSyncJob       ─┤        ┌──────────────────────────────────┐
   InviteSyncJob    ─┼───────▶│  IVRChatGate  (singleton)        │
   AuditLogSyncJob  ─┤        │  • owns THE authenticated session│──▶ VRChat.API ──▶ VRChat
   ModerationAction ─┘        │  • token bucket + Semaphore(1)   │
                              │  • 401 → re-login                │
                              │  • 429 → Retry-After backoff     │
                              │  • WAF detection → surfaced      │
                              │  • ApiException → VRChatResult<T>│
                              └──────────────────────────────────┘
                                            │
                              ┌─────────────┴──────────────┐
                              ▼                            ▼
                    ModbotContext (EF Core)        IFactWriter (append-only)
                     • projections (current)        • modbot_event
                       GroupMember, GroupBan, …     • never mutated, never overwritten
                              │                            │
                              │                            ▼
                              │                     RollupJob (recomputable aggregates)
                              ▼
                     DomainEvent ──▶ Discord embeds  /  SSE to web UI
```

Every sync job is a `BackgroundService` driven by a `PeriodicTimer`.

**MassTransit is removed.** In the old implementation the bus did no work: `MemberConsumer.Consume`
contained an infinite `while (!ct) { sync; await Task.Delay(5min); }`, and the producer published once
at startup and never again. A message bus distributes work; it does not schedule it and does not govern
a shared rate limit. Both had to be reintroduced by hand (`Task.Delay`, a static `NextSyncTimes`
dictionary, and a `goto TryAgain`), which is the signal that the abstraction was fighting the problem.
Under a single-group appliance there is nothing to fan out.

### 4.1 `IVRChatGate`

The single most important interface in the system. **Nothing else may ever construct a VRChat client.**

```csharp
public interface IVRChatGate
{
    // The callback returns ApiResponse<T>, so callers MUST use the
    // ...WithHttpInfoAsync variants. These never throw (see below).
    Task<VRChatResult<T>> ExecuteAsync<T>(
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default);

    VRChatSessionState State { get; }   // Healthy | Reauthenticating | RateLimited | WafBlocked | Unconfigured
}

public readonly record struct VRChatResult<T>(
    bool     Success,
    T?       Value,
    int      StatusCode,
    string?  ErrorMessage,
    int?     WafCode,        // VRChat's Cloudflare WAF classification
    string?  RawResponse);
```

Responsibilities, all in one place:

- Owns the one authenticated `IVRChat` instance built from `VRChat.API`, including the optional
  egress proxy (§2.3.1) via `VRChatClientBuilder.WithProxy`.
- Persists and re-hydrates the auth cookie from the database (never `cookie.txt` on disk).
- Serialises all calls (`SemaphoreSlim(1)`) behind a token bucket.
- **Priority queue**: interactive moderation actions preempt background sync.
- `401` → transparent re-login (TOTP via the stored 2FA secret), then retry once.
- `429` → **cold stop, never retry** (§4.3). This is the one place the gate deliberately does not
  behave like a normal HTTP client.
- Classifies WAF blocks distinctly from ordinary errors and surfaces them as a **health state in
  the UI with a prompt to configure a proxy** — not as log noise.
- Emits `X-Modbot-Contact-Email` / `X-Modbot-Contact-URL` headers and a descriptive User-Agent via
  `WithApplication(...)`, so VRChat can identify and contact the operator. Being a legible API
  citizen is a design goal, not an afterthought.

**No exception translation is required.** `VRChat.API` has been modified upstream so that every
`...WithHttpInfoAsync` method catches `ApiException` internally and returns a non-success
`ApiResponse<T>` instead of throwing. `ApiResponse<T>` exposes `StatusCode`, `Headers`, `Data`,
`ErrorText`, `Cookies` and `RawContent` — everything the gate needs. Verified in
`GroupsApi.GetGroupMembersWithHttpInfoAsync`.

**Known upstream limitation.** The catch path constructs a fresh `ApiResponse` with
`new Multimap<string, string>()` and no `RawContent`, so on **error** responses `Headers` is empty
and the WAF body survives only inside `ErrorText`, formatted as `"Error calling {method}: {body}"` —
`WafCode` must be extracted by locating the JSON payload within that string.

Worth fixing upstream, but **it changes nothing about rate limiting**: VRChat does not send
`Retry-After` at all, so there is no header to recover (§4.3). The gate must tolerate the absence
of both and must not regress if they later become populated.

**Split-ready:** the token bucket and semaphore sit behind an `IRateLimitLease`. The in-process
implementation is a `SemaphoreSlim`; a future distributed implementation is a Redis lease. Same
interface, same call sites.

### 4.2 Sync jobs — budget-derived, not fixed-interval

Jobs declare a **cost** and a **desired freshness**. The scheduler allocates the request budget
(§4.3) across them; it does **not** run them on hardcoded timers.

| Job | Request cost | Desired freshness | Priority |
|---|---|---|---|
| `AuditLogSyncJob` | 1–2 (cursor-based, newest-first until a known cursor) | 1 min | highest — cheap, and the authoritative fact source |
| `MemberSyncJob` (incremental) | 1–3 pages (stops on first unchanged page) | 5 min | high |
| `BanSyncJob` | ⌈bans / 100⌉ | 15 min | medium |
| `InviteSyncJob` | ⌈invites / 100⌉ | 15 min | medium |
| `MemberSyncJob` (full) | ⌈members / 100⌉ | as budget allows | lowest — deliberately starvable |

**Fixed intervals do not survive contact with large groups.** A 50,000-member group costs 500
requests for one full member sync. On a 30-minute timer that is 1,000 requests/hour for a single
job — which on a plausible budget is the entire allowance, spent re-reading mostly-unchanged rows,
leaving nothing for the audit log or for a moderator trying to issue a ban.

So freshness targets are **aspirations the scheduler meets when it can**. Under budget pressure it
degrades in a defined order: full syncs stretch out first (hours, or overnight), then bans and
invites, then incremental members. The audit log and interactive actions are protected. The UI
displays actual last-sync times per data type, never an implied guarantee.

This also means **large groups automatically sync less often** with no configuration, and small
groups — the 100-to-10,000-member majority — comfortably meet every freshness target.

All persistence is **batched** — one `SaveChangesAsync` per page, not per entity. The old
implementation issued two round-trips per member, so a 5,000-member group cost roughly 10,000
sequential database calls per sync.

Mapping from SDK models to entities uses **Riok.Mapperly** (source-generated, compile-time verified),
not AutoMapper.

### 4.3 Rate limiting — an opaque, punitive limit

VRChat's rate limit does not behave like a normal API limit, and the gate must not treat it like one:

- **No published limit.** The threshold is undocumented and changes.
- **No `Retry-After`.** No header communicates the limit, the remaining allowance, or the penalty
  duration. A 429 tells you that you are limited and nothing else.
- **Retrying while limited extends the penalty.** Empirically, a retry issued before the penalty
  expires appears to add roughly **45–80 seconds** to it.

That third property is the one that matters, because it breaks the standard tool. Exponential
backoff assumes probing is free and optimises for converging quickly on the recovery moment; a
schedule of 1/2/4/8 minutes issues four probes and can add three to five minutes of penalty while
never converging. **Under a per-probe penalty, the objective is to minimise the number of probes,
not the total wait.**

#### 4.3.1 Prevention is the mechanism; recovery is damage control

A 429 is treated as an **incident, not control flow**. It is surfaced in the UI, recorded as a fact,
and counted — a healthy Modbot should emit approximately zero.

**Budget.** The operator configures an estimated requests-per-hour ceiling; Modbot runs at a
conservative fraction of it (proposed default **60%**) and never bursts to the estimate. The token
bucket in §4.1 enforces this as a hard cap, not as best-effort pacing. The estimate lives in
`Settings` with a documented default, because it is a guess about someone else's undocumented
system and will need changing without a redeploy.

**On a 429 — cold stop.** All VRChat traffic halts, including interactive moderation actions, which
fail fast with an explanatory message rather than queueing into the penalty.

1. Wait a long fixed base period (proposed **15 minutes**). Issue nothing at all.
2. Send **one** probe: the cheapest available call.
3. If it succeeds, resume at a reduced budget (below). If it fails, wait **a longer period**
   (proposed +15 minutes each time, linear) and probe once more.
4. After a small number of failures, stop and alert the operator. Something is wrong that waiting
   will not fix.

Never more than one probe per waiting period, and no short retries at any point.

**Budget adaptation — AIMD, tuned for expensive loss.** The same shape as TCP congestion control,
with the constants pushed hard toward caution because the loss signal here is far more punishing
than a dropped packet:

- **On a 429:** multiplicative decrease — halve the effective budget. The estimate was wrong.
- **On sustained success:** additive increase — recover by a small fixed step per hour, capped at
  the configured ceiling. Recovery should take hours, not minutes.

The asymmetry is the point. Overshooting costs an opaque multi-minute outage; undershooting costs
slightly staler data.

#### 4.3.2 Penalty state must survive restarts

The current penalty state, the adapted budget, and the recent-request window are **persisted in the
database**, not held in memory.

A Railway redeploy, a crash, or an OOM kill would otherwise reset the gate to full budget and send
it straight back into a live penalty — and a crash-loop would do this every few seconds, issuing a
continuous stream of penalty-extending probes. **A restart loop must not be able to escalate a
ten-minute rate limit into an account-level problem.** On boot the gate reads persisted state and
resumes its cold wait before issuing anything.

#### 4.3.3 Consequences elsewhere

- Sync intervals are derived, not fixed (§4.2).
- The priority queue is the budget allocator, not a nicety: it decides what a scarce, non-renewable
  resource is spent on.
- The UI must show real last-sync times per data type, plus current gate health
  (`Healthy | RateLimited | WafBlocked | Reauthenticating`) — an operator has to be able to see that
  Modbot is deliberately slow rather than broken.

---

## 5. Analytics and data engine

This is a **substrate**, not a feature, and it must exist in M0 because it is the only part of the
design that cannot be retrofitted.

### 5.1 The one-way door

A sync that upserts current state destroys history. The old `ProcessMembers` overwrote each
`GroupMember` row on every pass, which makes these questions permanently unanswerable:

- What was our member count on 3 July?
- How many people left in the week after that incident?
- Who are our actual regulars?

Auth, search, the Discord bot are all retrofittable. **Recorded history is not.** Therefore the fact
log ships in M0 even though most of the analytics UI does not.

### 5.2 Three layers

```
  VRChat sync ──┐
  Audit log   ──┼──▶ FACTS       append-only, immutable, partitioned
  Win client  ──┘      │
                       ├──▶ PROJECTIONS  current state (GroupMember, GroupBan) — mutable
                       └──▶ ROLLUPS      aggregates — always recomputable from facts
```

**Invariant: rollups are recomputable from facts; facts are never mutated.** A bug in a metric is a
re-run, not lost data. A metric invented next year backfills across all recorded history.

### 5.3 Fact schema

```sql
modbot_event                     -- PARTITION BY RANGE (occurred_at), monthly partitions
  id               bigserial
  occurred_at      timestamptz  not null   -- when it happened (lower bound if imprecise)
  occurred_before  timestamptz  null       -- NULL = exact; else upper bound of the window
  observed_at      timestamptz  not null   -- when Modbot learned of it
  type             smallint     not null   -- MemberJoined | MemberLeft | BanAdded | RoleGranted |
                                           -- InstanceJoined | InstanceLeft | AvatarChanged | ...
  subject_id       text         not null   -- usr_... this fact is about
  actor_id         text         null       -- usr_... who caused it
  instance_id      text         null
  source           smallint     not null   -- AuditLog | SyncDiff | Client | Manual
  data             jsonb        not null

  INDEX (subject_id, occurred_at DESC)
  INDEX (type, occurred_at DESC)
  GIN   (data)
```

**`occurred_before` and `source` are what make the analytics honest.** When the audit log reports a
ban at 14:32:07, that is exact. When a sync diff notices a member is gone, all that is actually known
is that they left *between the previous sync and this one* — a five-minute window. Collapsing both
into a single timestamp invents precision and produces fake spikes in hourly charts. Recording the
window instead lets rollups distribute across the interval or exclude low-precision facts from
fine-grained series.

`source` also drives **deduplication**: the same ban arrives twice, once as a low-confidence
`SyncDiff` inference and once as an authoritative `AuditLog` fact. Same event, different confidence;
the authoritative one wins and the inference is superseded.

### 5.4 Rollups

```sql
modbot_rollup_daily
  day        date
  metric     text          -- 'members.total', 'members.joined', 'bans.added',
                           -- 'moderator.actions', 'instance.minutes', ...
  dimension  text  null    -- optional: user id, moderator id, instance id, role id
  value      numeric
  PRIMARY KEY (day, metric, dimension)
```

A generic metric/dimension shape so new metrics require no migration. Recomputed by `RollupJob`;
a full rebuild from facts is always available and is the supported fix for any aggregation bug.

### 5.5 Retention and privacy

Retention is **tiered by fact class**, because the volume profiles differ by three orders of
magnitude (§5.7) while the value profiles run the opposite way.

| Fact class | Examples | Volume | Default retention |
|---|---|---|---|
| **Moderation** | bans, kicks, role changes, audit-log entries, membership changes | low — hundreds/day | **forever** |
| **Presence** | instance join/leave, avatar change, session heartbeats | high — up to ~18k/hour at peak | **90 days** |
| **Rollups** | all derived aggregates | trivial | **forever** |

All configurable. Flat 365-day retention was the initial proposal and is wrong: at peak presence
volume it implies on the order of 10⁸ rows, which is real money in Railway storage for data whose
individual rows stop being interesting within weeks — while a ban from three years ago is exactly
the kind of thing a moderator needs. Long-range questions ("who are our regulars over two years")
are answered from rollups, which are cheap and kept permanently.

Charts therefore keep their full history even after the underlying events age out.

A **purge-user** action erases every fact for one user on request.

Presence data — who was where, for how long, wearing what — is genuinely personal information, even
though it is all data the group could already observe directly. The defensible posture is: it lives
on the group's own server, retention is configurable, and deletion works. This is documented in one
paragraph in `docs/`, stated plainly and without editorialising.

### 5.6 Surfaces in this spec

| Surface | In M2.5 | Description |
|---|---|---|
| **Dossier** | yes | One user, everything Modbot knows: join date, roles, ban history, every audit-log mention, and (from M6) sessions, time spent, avatar history. "Who is this person" is the question staff ask most often. |
| **Metrics** | yes | Group health over time: member growth, join/leave rate, ban rate, staff action volume, per-moderator activity. |
| **Segments** | **no — M6.5** | Queryable cohorts (*"members with >10h in our worlds in the last 30 days, no bans, joined before June"*) → export, bulk action, giveaway draw. Requires presence data to be interesting. |

### 5.7 Volume, and why client facts must be deduplicated at ingest

Sizing comes from the extreme upper bound: a major-league group with 400k+ members running **30
concurrent instances**, each with **4–6 moderators running the Windows client**, at roughly **10
joins/leaves per minute per instance**.

| | Rate | Note |
|---|---|---|
| Actual events | ~18k/hour | 30 instances × 10/min × 60 |
| **Reports received** | **~110k/hour** | same events, reported independently by 4–6 clients each |

**The 6× gap is the design requirement.** Six moderator clients in one instance all observe the
same person joining and all report it. Without deduplication the fact log inflates sixfold, every
"who joined" query returns duplicates, and — worst — **time-spent calculations sextuple-count**,
which silently corrupts exactly the metric that giveaways and regular-detection depend on.

Therefore:

- **Deduplication happens at the ingest boundary, not in storage.** A duplicate report is discarded
  before it becomes a fact.
- Client-sourced facts carry a **deterministic event identity** so that independent clients
  observing the same event compute the *same* key without coordinating:
  `hash(instance_id, subject_id, type, floor(observed_at / bucket))`, enforced by a unique index.
- Clients do not need to agree, elect a leader, or know about each other. Whichever report arrives
  first wins; the rest are no-ops. A client dropping out mid-instance loses no coverage, because the
  others are already reporting the same events.
- The `source` and `occurred_before` fields (§5.3) still apply: the earliest-arriving report sets
  the timestamp window, and a later authoritative source can supersede it.

Sustained peak is therefore ~18k facts/hour ≈ 430k/day, which at the 90-day presence retention of
§5.5 is roughly 39M live rows — comfortable for partitioned Postgres. Typical groups (100–10,000
members, one or two instances) sit three orders of magnitude below this.

### 5.7.1 Storage choice

Plain **PostgreSQL**, monthly partitions on `modbot_event`, so retention pruning is a partition
drop rather than a mass `DELETE`.

- **Not TimescaleDB**: continuous aggregates are licensed under the TSL, not Apache, which conflicts
  with the project's licensing care. It remains a cheap upgrade path precisely because it is an
  extension *on* Postgres.
- **Not ClickHouse or DuckDB**: breaks "Postgres is the only external service."

---

## 6. Data model

### 6.1 Removed from `old/`

`ModbotBilling`, `ModbotTransaction`, `ModbotGroupAccount`, `ModbotRole`, `Session`,
`ModbotGroup` (as a tenancy table), and the Clerk and Svix integrations.

Proxy configuration is **retained but reshaped**: from a rotating pool with per-request sticky
sessions to a single optional egress proxy stored in `Settings` (§2.3.1).

### 6.2 Retained, reworked

`User`, `GroupMember`, `GroupBan`, `GroupInvite`, `GroupLog`, `ConnectedDiscordAccount`,
`ModbotFile`.

Changes: `<Nullable>enable</Nullable>` throughout; batched saves; explicit mapping via Mapperly;
every sync diff emits facts.

### 6.3 New

| Entity | Purpose |
|---|---|
| `Settings` | Singleton row. The managed group's id and cached metadata, VRChat credentials, **egress proxy config (§2.3.1)**, Discord bot token, SMTP config, per-class retention policy (§5.5), feature toggles. Secrets encrypted (§8.3). |
| `ModbotUser` | Staff account: username, password hash, permission bitfield, optional Discord link. |
| `ApiKey` | Tokens for the read API, separate from user session credentials. |
| `modbot_event` | The fact log (§5.3). |
| `modbot_rollup_daily` | Aggregates (§5.4). |

### 6.4 Search

**PostgreSQL `pg_trgm` with GIN indexes** on display name and user id.

Meilisearch is dropped. It is an additional service to deploy, monitor and keep in sync, for a table
that tops out in the low tens of thousands of rows in even a very large group. Trigram search covers
fuzzy display-name matching well at that scale. Revisit only if measurement shows it is too slow.

---

## 7. Authentication, authorisation and onboarding

### 7.1 First run

With no `ModbotUser` rows present, every route redirects to `/setup`:

1. **Create administrator** — username and password.
2. **VRChat login** — email, password, TOTP secret. Validated live against the API; failure is shown
   immediately rather than at first sync. Session cookie persisted to `Settings`.
3. **Connection check / proxy** — see §7.1.1.
4. **Select group** — from the groups where the authenticated account holds moderator permissions.
   If none qualify, say so explicitly and explain the required permissions.
5. **Optional** — Discord bot token and guild, SMTP for notifications, Discord OAuth application.

Each step is independently re-runnable later from settings.

#### 7.1.1 Connection check and proxy step

Immediately after the VRChat account step, Modbot **tests its own egress** and tells the operator
whether they need a proxy, rather than making them find out from a failed sync hours later:

> **Using a proxy?** Let's test whether you need one.
> [ Test connection ] — Modbot will try to reach the VRChat API directly.

- **Pass** → "No proxy needed." Continue. The field stays available but collapsed.
- **Fail with a WAF block** → the diagnosis is stated plainly: this network's IP range is blocked by
  Cloudflare, which is common on datacentre and VPS hosting, and it is not the operator's fault or a
  VRChat account problem. A proxy is required to continue, with **iproyal.com** offered as a known
  working option. A proxy URL and credentials can then be entered and **re-tested in place** until
  the check passes.
- **Fail otherwise** (DNS, timeout, bad credentials) → distinguish it from a WAF block, because the
  remedy is completely different and a proxy will not help.

The same check is re-runnable from settings, and the gate's `WafBlocked` health state (§4.1) links
straight back to it — so an operator whose host is blocked *after* a working install gets the same
guided fix instead of a wall of failed syncs.

### 7.2 Sessions

Cookie authentication: `HttpOnly`, `Secure`, `SameSite=Lax`.
Password hashing via `PasswordHasher<ModbotUser>` registered standalone — **no ASP.NET Core Identity**.
(The old implementation already did exactly this and it was the right call.)

Optional Discord OAuth linking, and a per-deployment setting to *require* Discord login.

### 7.3 Authorisation

A permission bitfield per `ModbotUser`, enforced by `RequiresFlagAttribute` — carried over from the
old implementation, which had the right shape. `RequiresGroupFlagAttribute` is deleted along with
multi-tenancy.

Read API access uses `ApiKey`, scoped separately from user sessions.

### 7.4 Notifications

`EmailService` is retained and sends via **operator-supplied SMTP** configured in the wizard.
No hosted email provider dependency.

---

## 8. Configuration and secrets

### 8.1 Environment

| Variable | Required | Purpose |
|---|---|---|
| `PORT` | yes | provided by Railway |
| `DATABASE_URL` | yes | Postgres connection |

**That is the complete list.** Everything else lives in `Settings` and is set through the wizard.

### 8.2 Migrations

EF Core migrations applied automatically at startup, as in the old implementation. A self-hosted
appliance must not require the operator to run a migration command.

### 8.3 Secret storage — settled

Secrets in `Settings` (VRChat password and 2FA secret, Discord bot token, SMTP password, proxy
credentials) are encrypted at rest with a key generated on first boot and **stored in the database**.

No environment variable, no volume, no key-management step. This deliberately protects against
casual reading of a database dump and **nothing more** — an attacker with full database access has
the key too.

That is the correct trade for this project. Modbot is run by community groups on managed hosting,
where in practice nobody touches the database directly, and where Railway already stores environment
variables in plaintext and displays them in its dashboard — so a key held there would not be
meaningfully safer. What a required key *would* reliably do is permanently brick installs when
someone redeploys and loses it.

The threat model is stated in one honest paragraph in `docs/security.md`. Operators who need real
encryption at rest should encrypt the database itself, which is where that control belongs.

---

## 9. Discord bot (basic)

In scope for this spec:

- **Audit log → Discord channels.** Rich embeds per event type, routable to multiple channels with
  per-type filtering.
- **Lookup commands** — `/user`, `/member`, returning dossier summaries.

Deferred to M4: ban synchronisation in both directions, role synchronisation, VRChat↔Discord account
linking and auto-invite.

Runs as an `IHostedService` inside `Modbot.Host`. If no bot token is configured, it does not start
and the rest of Modbot is unaffected.

---

## 10. Web UI

React + TypeScript + Vite, built into `Modbot.Host/wwwroot` and served by Kestrel. One container,
no separate frontend deployment.

Screens in this spec: setup wizard, login, members, bans, invites, audit log viewer, user dossier,
metrics dashboard, settings.

Live updates via **Server-Sent Events** — one-directional, trivially proxied, no WebSocket
infrastructure. Charts follow the project's data-visualisation conventions and must be legible in
both light and dark themes.

---

## 11. Dependencies

| Package | Purpose |
|---|---|
| ASP.NET Core (.NET 10) | host, API, static files |
| EF Core + Npgsql | persistence, migrations |
| `VRChat.API` | VRChat REST client (wrapped by `IVRChatGate`, never used directly) |
| Discord.Net | bot |
| Serilog | logging |
| FluentValidation | request validation |
| Riok.Mapperly | source-generated mapping |
| Otp.NET | TOTP for VRChat 2FA |

**External services: PostgreSQL. That is all.**

Explicitly not used: `VRChat.API.Realtime` (the Pipeline carries nothing useful here), MassTransit,
Meilisearch, Redis, AutoMapper, Clerk, Svix.

---

## 12. Testing

| Layer | Approach |
|---|---|
| `IVRChatGate` | Unit tests with a faked `IVRChat`: 401 re-login, WAF classification from `ErrorText`, priority preemption, serialisation under concurrency, proxy vs. direct egress. |
| **Rate limiting** (§4.3) | Against a fake VRChat that models the *punitive* limiter — a 429 while penalised extends the penalty. Assert: **at most one probe per waiting period**; no request is issued during a cold stop; budget halves on 429 and recovers only additively; the token bucket never exceeds the configured fraction of the ceiling. A test that passes against a *non*-punitive fake proves nothing, so the fake's penalty-extension behaviour is itself asserted. |
| **Restart safety** (§4.3.2) | Trip the limit, destroy and recreate the host, assert the new instance resumes the cold wait from persisted state and issues nothing. Then simulate a crash-loop and assert total probes stay bounded — the regression guard against a restart loop escalating a rate limit. |
| Sync jobs | Fed recorded VRChat payloads; assert both projections **and** emitted facts, including `occurred_before` windows on inferred events. |
| **Ingest deduplication** | Replay the same instance event as reported by six independent clients with jittered timestamps; assert **exactly one** fact is written and that time-spent totals are unchanged versus a single-client replay. |
| Analytics | Property test the core invariant: rollups recomputed from facts equal rollups built incrementally. |
| Capacity handling | An instance reporting a capacity above the usual ceiling (exemption case, §3.1) must render and bucket correctly — regression guard against reintroduced constants. |
| API | Integration tests against Postgres via Testcontainers. |

The gate, the fact log and ingest deduplication get the most rigorous coverage: the gate because
everything depends on it, the fact log because its mistakes are unrecoverable, and deduplication
because its failure mode is **silent** — nothing errors, the numbers are simply wrong by 6×.

---

## 13. Non-goals for this spec

Moderation actions (M3); Discord ban and role sync, account linking (M4); instance launching and
monitoring (M5); the Windows client and SteamVR overlay (M6); segments, cohorts and giveaways
(M6.5); group flagging, AI moderation and the federated warning network (M7).

Federation is deliberately last. It is the only feature that is inherently political, and nothing
else depends on it — Modbot must be fully useful whether or not it ever exists.

---

## 14. Milestones

| # | Slice | Spec |
|---|---|---|
| **M0** | Foundation: repo, licensing, CI, `IVRChatGate`, auth, onboarding, **fact log + rollups + retention** | this |
| **M1** | Member/ban/invite sync, search UI, **facts emitted on diff** | this |
| **M2** | Audit log ingestion, parsing, viewer — **authoritative facts, dedups M1's inferences** | this |
| **M2.5** | User dossier, metrics dashboard | this |
| — | Basic Discord bot: audit log sync, lookup commands | this |
| M3 | Moderation actions: ban/kick by display name, user id, avatar id | future |
| M4 | Discord: ban sync, role sync, account linking, auto-invite | future |
| M5 | Instance launching, instance list monitoring and Discord sync | future |
| M6 | Windows client + SteamVR overlay; presence facts | future |
| M6.5 | Segments, cohorts, giveaways | future |
| M7 | Group flagging, AI moderation, federated warning network | future |

---

## 15. Decision log

Recorded so they are visible rather than buried, and so they are not relitigated.

1. **License is AGPL-3.0** (§2.2), replacing an earlier PolyForm Noncommercial proposal. Modbot is
   genuinely open source; the accepted consequence is that commercial hosting is permitted.
2. **Secret storage settled** (§8.3) — key in the database, no environment variable, honest docs.
   Community-group threat model, not a multinational SaaS one.
3. **Meilisearch dropped** in favour of `pg_trgm` (§6.4).
4. **`.agent/` rather than `agent/`**, with `AGENTS.md` at the root pointing to it.
5. **Proxy support restored, reshaped** (§2.3.1) — one optional egress proxy for WAF-blocked
   networks, with a guided test in the wizard (§7.1.1). Not a rotating pool; never a response to
   rate limiting.
6. **`VRChat.API` used rather than a hand-rolled client.** The old `VRChatClient.cs` existed because
   at the time of writing the maintainer did not yet maintain `VRChat.API` and it was broken — a
   historical constraint, not a technical one. It has since been modified upstream so
   `...WithHttpInfoAsync` returns a full `ApiResponse<T>` and never throws, which is precisely what
   the gate wants. Two upstream gaps remain, documented in §4.1.
7. **Retention is tiered** (§5.5) — moderation facts forever, presence facts 90 days, rollups
   forever. Replaces a flat 365-day default that did not survive contact with real peak volume.
8. **Client facts are deduplicated at the ingest boundary** (§5.7) via a deterministic event
   identity. Required because 4–6 moderator clients per instance report the same events, a 6×
   amplification that would otherwise corrupt every time-spent metric.
9. **No hardcoded VRChat capacity constants anywhere** (§3.1) — instance limits are raised by
   exemption and must be read from the API.
10. **No exponential backoff on rate limits** (§4.3). VRChat's limiter is opaque, sends no
    `Retry-After`, and *extends* the penalty by ~45–80s when retried early — so backoff chases a
    deadline it keeps pushing away. Replaced by: a hard budget below an operator-configured
    estimate, cold stop on 429, one probe per long waiting period, and AIMD budget adaptation with
    a fast decrease and slow increase.
11. **Sync intervals are budget-derived, not fixed** (§4.2). Fixed timers put a 50k-member group's
    full sync at ~1,000 requests/hour, which alone could exhaust the budget. Freshness targets are
    aspirations; degradation order is defined and the audit log plus interactive actions are
    protected.
12. **Rate-limit state persists in the database** (§4.3.2), so a redeploy or crash-loop cannot reset
    the gate into a live penalty and escalate it.
