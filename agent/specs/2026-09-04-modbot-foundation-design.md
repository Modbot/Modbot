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

### 2.2 License: source-available, non-commercial

The requirement — *anyone may use and modify it, nobody may commercialise it including the author,
changes flow back upstream, author retains copyright and trademark* — **is not open source** under the
OSI definition. Field-of-use and non-commercial restrictions fail OSD #6. The project must not
describe itself as "open source"; it is **"source available, non-commercial."**

| File | Contents |
|---|---|
| `LICENSE` | **PolyForm Noncommercial 1.0.0**, verbatim |
| `CLA.md` | Contributor License Agreement. A license cannot compel upstreaming; only a contribution agreement can. Also required to keep copyright consolidated for any future relicensing. |
| `TRADEMARK.md` | "Modbot" and its logo are excluded from the license grant. Forks may not use the name. |

Rejected: AGPL-3.0 (OSI-compliant and prevents closed SaaS forks, but permits commercial use);
CC BY-NC (explicitly not intended for software).

**Known cost:** PolyForm-NC repositories are excluded from some package registries and will not
attract corporate contributors. Accepted deliberately.

### 2.3 One VRChat account, REST polling

Each deployment authenticates as exactly **one** VRChat account, which must be a moderator of the
managed group. No proxy pools, no multi-account request distribution — these are rate-limit evasion
and are the fastest route to Modbot being named in a VRChat TOS conversation.

The Pipeline websocket is **not used**. It carries friend and location updates, which are not
meaningful in a group-moderation context. Ingestion is REST polling on a governed schedule.

This makes the whole system a **single-writer scheduling problem**: one token bucket, one priority
queue, N job types competing for it. That is the piece worth getting genuinely right.

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

Environment variables are limited to `PORT` and `DATABASE_URL` (plus an optional
`MODBOT_SECRET_KEY`, §8.3). Everything else — VRChat credentials, group selection, Discord bot
token, SMTP — is entered through a first-run onboarding wizard and stored in the database.

Deploying Modbot is: click the Railway template, open the URL, follow the wizard.

---

## 3. Repository layout

```
Modbot/
├─ agent/                     # planning artefacts — specs, plans, research
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

`agent/` is deliberately **not** a dotfolder. Contributors and agents must be able to find it by
browsing the repository on GitHub.

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
    Task<VRChatResult<T>> ExecuteAsync<T>(
        Func<IVRChat, CancellationToken, Task<T>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default);

    VRChatSessionState State { get; }
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

- Owns the one authenticated `IVRChat` instance built from `VRChat.API`.
- Persists and re-hydrates the auth cookie from the database (never `cookie.txt` on disk).
- Serialises all calls (`SemaphoreSlim(1)`) behind a token bucket.
- **Priority queue**: interactive moderation actions preempt background sync.
- `401` → transparent re-login (TOTP via the stored 2FA secret), then retry once.
- `429` → honour `Retry-After` from `ApiException.Headers`, exponential backoff.
- Parses `ApiException.ErrorContent` into `WafCode` so Cloudflare blocks are distinguishable from
  ordinary errors, and surfaces this in the UI as a health state rather than as noise in the logs.
- Emits `X-Modbot-Contact-Email` / `X-Modbot-Contact-URL` headers and a descriptive User-Agent via
  `WithApplication(...)`, so VRChat can identify and contact the operator. Being a legible API
  citizen is a design goal, not an afterthought.

**Split-ready:** the token bucket and semaphore sit behind an `IRateLimitLease`. The in-process
implementation is a `SemaphoreSlim`; a future distributed implementation is a Redis lease. Same
interface, same call sites.

### 4.2 Sync jobs

| Job | Interval | Mode |
|---|---|---|
| `MemberSyncJob` | 5 min incremental, 30 min full | paged, stops early on unchanged page unless full |
| `BanSyncJob` | 15 min | paged |
| `InviteSyncJob` | 15 min | paged |
| `AuditLogSyncJob` | 1 min | cursor-based, newest-first until known cursor reached |

All persistence is **batched** — one `SaveChangesAsync` per page, not per entity. The old
implementation issued two round-trips per member, so a 5,000-member group cost roughly 10,000
sequential database calls per sync.

Mapping from SDK models to entities uses **Riok.Mapperly** (source-generated, compile-time verified),
not AutoMapper.

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

| Data | Default retention | Configurable |
|---|---|---|
| Raw facts (`modbot_event`) | 365 days | yes |
| Rollups | forever | yes |

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

### 5.7 Storage choice

Plain **PostgreSQL**, monthly partitions on `modbot_event`.

A busy group running instances around the clock is projected at roughly 15–25k presence events/day
once M6 lands — a few million rows per year, which is unremarkable for partitioned Postgres.

- **Not TimescaleDB**: continuous aggregates are licensed under the TSL, not Apache, which conflicts
  with the project's licensing care. It remains a cheap upgrade path precisely because it is an
  extension *on* Postgres.
- **Not ClickHouse or DuckDB**: breaks "Postgres is the only external service."

---

## 6. Data model

### 6.1 Removed from `old/`

`ModbotBilling`, `ModbotTransaction`, `ModbotGroupAccount`, `ModbotRole`, `Session`,
`ModbotGroup` (as a tenancy table), `ProxyOptions`, and the Clerk and Svix integrations.

### 6.2 Retained, reworked

`User`, `GroupMember`, `GroupBan`, `GroupInvite`, `GroupLog`, `ConnectedDiscordAccount`,
`ModbotFile`.

Changes: `<Nullable>enable</Nullable>` throughout; batched saves; explicit mapping via Mapperly;
every sync diff emits facts.

### 6.3 New

| Entity | Purpose |
|---|---|
| `Settings` | Singleton row. The managed group's id and cached metadata, VRChat credentials, Discord bot token, SMTP config, retention policy, feature toggles. Secrets encrypted (§8.3). |
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
3. **Select group** — from the groups where the authenticated account holds moderator permissions.
   If none qualify, say so explicitly and explain the required permissions.
4. **Optional** — Discord bot token and guild, SMTP for notifications, Discord OAuth application.

Each step is independently re-runnable later from settings.

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
| `MODBOT_SECRET_KEY` | no | optional encryption key override (§8.3) |

Everything else lives in `Settings`.

### 8.2 Migrations

EF Core migrations applied automatically at startup, as in the old implementation. A self-hosted
appliance must not require the operator to run a migration command.

### 8.3 Secret storage — decision required

Database-stored secrets need an encryption key, and a key is an environment variable. The options,
none of them free:

| Option | Cost |
|---|---|
| Require `MODBOT_SECRET_KEY` | One more variable. **Losing it permanently bricks every stored secret** — a serious hazard on Railway, where people redeploy and reset environments. |
| Auto-generate to a Railway volume | Zero extra variables. Fails if the volume is lost or the service is recreated. |
| **Key in the database, documented honestly** ← proposed | Protects against casual reading of a database dump, and nothing more. **No weaker than environment variables in practice** — Railway stores those in plaintext and displays them in its dashboard. Cannot brick the install. |

**Proposed:** the third, with `MODBOT_SECRET_KEY` supported as an *optional* override for operators
who want real encryption at rest and accept the recovery risk. The threat model is stated plainly in
`docs/security.md`. A self-hosted tool that can permanently destroy itself over a lost environment
variable is worse in practice than one that is honest about what it does and does not protect.

**This decision is flagged for explicit sign-off.**

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
| `IVRChatGate` | Unit tests with a faked `IVRChat`. Must cover: 401 re-login, 429 `Retry-After`, WAF classification, priority preemption, serialisation under concurrency. |
| Sync jobs | Fed recorded VRChat payloads; assert both projections **and** emitted facts. |
| Analytics | Property test the core invariant: rollups recomputed from facts equal rollups built incrementally. |
| API | Integration tests against Postgres via Testcontainers. |

The gate and the fact log get the most rigorous coverage: the gate because everything depends on it,
the fact log because its mistakes are unrecoverable.

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

## 15. Decisions taken without explicit sign-off

Recorded so they are visible rather than buried. All are cheap to reverse except the first.

1. **Secret storage** (§8.3) — key in the database, documented, with an optional environment override.
   **Flagged for explicit approval.**
2. **Meilisearch dropped** in favour of `pg_trgm` (§6.4).
3. **`agent/` rather than `.agent/`** — discoverability when browsing on GitHub.
4. **`VRChat.API` used rather than a hand-rolled client.** Investigation of `old/VRChat/VRChatClient.cs`
   found it existed for three reasons: per-request proxy sticky sessions (dead under the one-account
   model), WAF error surfacing, and a non-throwing result type. `ApiException` exposes `ErrorCode`,
   `ErrorContent` and `Headers`, which is sufficient for the latter two — and both belong in
   `IVRChatGate` regardless. The hand-roll was roughly 90% proxy plumbing that no longer applies.
5. **Retention defaults** — 365 days for facts, indefinite for rollups (§5.5).
