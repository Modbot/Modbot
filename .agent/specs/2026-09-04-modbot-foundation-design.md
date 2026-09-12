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
3. **Windows client + SteamVR overlay** — in-instance monitoring (M3, separate spec cycle).

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

### 2.7 Versioning: calendar versions, plus a separate API version

Two version numbers exist and they answer different questions. Conflating them is the mistake this
section prevents.

#### 2.7.1 Release version — `YYYY.M.D`

```
  2026.1.1      first release of 1 January 2026
  2026.1.1a     first hotfix that same day
  2026.1.1b     second hotfix that same day
  2026.11.23    23 November 2026
```

Year, month, day, **not zero-padded**, with a lowercase letter appended for hotfixes shipped on the
same day. It is immediately legible — anyone can tell how old a deployment is at a glance, which is
exactly what you want to know when a self-hoster reports a bug.

**Comparison is componentwise and numeric, never lexical.** String sorting puts `2026.1.10` before
`2026.1.2`, which is wrong, and unpadded components guarantee someone will hit it. A comparison
helper ships with the version type and is used everywhere; no ad-hoc string comparison of versions
anywhere in the codebase.

#### 2.7.2 Mapping onto formats that will not accept it

Two build systems require numeric versions in shapes this format does not fit. Both mappings are
mechanical, monotonic, and must be generated by the build rather than typed by hand.

**.NET `AssemblyVersion`** needs four numbers, each 0–65535:

```
  2026.1.1   →  2026.1.1.0
  2026.1.1a  →  2026.1.1.1        hotfix letter becomes the revision (a=1, b=2, …)
```

Every component fits. The human-readable `2026.1.1a` goes in `InformationalVersion`, which is what
the UI and logs display.

**MSI `ProductVersion`** is the trap, and it will bite at the first client release. Windows Installer
allows `major.minor.build` where **major ≤ 255** — and `2026` does not fit. It also ignores any
fourth field entirely when deciding whether an upgrade applies, so the hotfix letter cannot live
there either.

```
  (year - 2000) . month . (day × 10 + hotfixIndex)

  2026.1.1    →  26.1.10
  2026.1.1a   →  26.1.11
  2026.12.31  →  26.12.310
  2026.12.31c →  26.12.313
```

Monotonic within a year and across year boundaries, valid until 2255, and every component in range.
Getting this wrong produces MSIs that silently refuse to upgrade — which looks like a broken updater
and is very hard to diagnose after the fact.

#### 2.7.3 API version — a plain integer

The server advertises an **API version**: a single integer, incremented **only on a breaking change**
to the interfaces the client depends on.

It is deliberately not the calendar version. Release versions change daily; API compatibility changes
rarely. A client that had to match a calendar version would be incompatible with a server that merely
shipped a typo fix.

- The server exposes it on an unauthenticated endpoint, so a client can check compatibility before it
  has credentials.
- The client declares the **range** of API versions it supports, not a single value, so one client
  build works across a span of server releases.
- Incompatibility is reported as a clear, actionable message naming both versions — never a parse
  failure or a silent no-op.

#### 2.7.4 Matching a client to a server

Moderators should not have to work out which client build to install.

1. The client (or the pairing flow) reads the server's API version.
2. It consults the release manifest, in which **every release declares the API versions it supports**.
3. It selects the newest client build compatible with that server.

The manifest is a static file (see the central services spec), so this lookup happens **in the
client** and needs no server-side logic — which is what keeps `my.modbot.co` and the release host
free of application code and free of any knowledge of who is asking.

A server ahead of every available client, or behind all of them, is stated plainly with both numbers
and what to do about it.

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

### 3.2 A standing rule: the client's source is its privacy policy

The Windows client (M3) runs unattended on a volunteer moderator's personal PC, reads VRChat's log
files, and sends what it observes to a server. That is, accurately described, the shape of spyware.
The only thing separating Modbot from spyware is that its behaviour is **honest, bounded and
verifiable** — and a moderator asked to install it is entitled to check rather than trust.

Therefore, in `Modbot.Client` and `Modbot.Overlay` specifically:

- **Every function that reads from disk, captures data, or transmits it carries a plain-language
  comment** stating what it reads, why, what leaves the machine, and what does not. Written for a
  suspicious reader with moderate technical skill, not for a compiler.
- **Redaction and exclusion are commented at the point they happen**, so a reader can see the thing
  *not* being sent, in the code, rather than taking a README's word for it.
- Comment density here is deliberately higher than the rest of the codebase. This is not a style
  inconsistency to be cleaned up; **do not "simplify" these comments away.**
- The README ships a summary table of every file read and every field transmitted, and it is a
  review failure for that table to drift from the code.

```csharp
// Reads VRChat's own output log -- the same file VRChat writes for its own diagnostics, which you
// can open in Notepad right now. We scan ONLY for instance join/leave lines and avatar-change
// lines. We do not read chat, we do not read your friends list, and nothing about worlds you visit
// outside this group's instances is sent anywhere.
//
// What leaves your PC for each line matched: the VRChat user id, the instance id, and a timestamp.
// That is all. The raw log line is never transmitted and never stored.
```

AGPL guarantees a moderator *can* read the source. Comments like this are what make that right
practically useful rather than theoretical — most people who want reassurance will not reconstruct
intent from a regex and a `HttpClient.PostAsync`.

---

## 4. Runtime architecture

```
   MemberSyncJob   ──┐
   BanSyncJob       ─┤        ┌──────────────────────────────────┐
   InviteSyncJob    ─┼───────▶│  IVRChatGate  (singleton)        │
   AuditLogSyncJob  ─┤        │  • owns THE authenticated session│──▶ VRChat.API ──▶ VRChat
   ModerationAction ─┘        │  • token bucket + Semaphore(1)   │
                              │  • 401 → re-login                │
                              │  • 429 → COLD STOP, never retry  │
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

### 4.2 Sync jobs — paced per type, capped globally, deliberately desynchronised

Each sync type issues **at most one request per interval**. These are *request pacing* rates, not
full-sync rates: a paged sync of a large group simply spans many intervals.

| Sync type | Max rate | Requests/sec | Endpoint class |
|---|---|---|---|
| Group members | 1 per **2 s** | 0.500 | `groups.members` |
| Group bans | 1 per **2 s** | 0.500 | `groups.bans` |
| Group audit log | 1 per **8 s** | 0.125 | `groups.auditlog` |
| Group instances | 1 per **8 s** | 0.125 | `groups.instances` |
| Group info | 1 per **10 s** | 0.100 | `groups.read` |
| Group roles | 1 per **10 s** | 0.100 | `groups.read` |
| | **Sum** | **1.450** | |

**Global ceiling: 2 requests/second**, enforced by the top bucket of the hierarchy (§4.3.1) and never
exceeded by any Modbot instance.

The 0.55 req/s of headroom between the sum and the ceiling is not spare capacity to be spent on
faster sync — it is reserved for **interactive work**: moderation actions, onboarding, and a
moderator's live queries, which preempt background sync (§4.1). A background scheduler that consumed
the full ceiling would make every ban wait behind a member page.

#### 4.2.1 These are caps, and they are configurable downward only

Every rate above, plus the global ceiling, is operator-configurable through a slider in settings.

- **The values above are hard maxima.** Configuration may only lower them. The cap is enforced
  server-side on write, not merely in the UI, so it cannot be raised by editing a request.
- Lowering is always allowed and always safe: a group on a constrained host, sharing an account, or
  simply wanting to be gentler can dial any of them down, at the cost of staleness.
- The settings UI shows the resulting total request rate as sliders move, and shows how far it sits
  under the global ceiling — so an operator can see the interactive headroom they are leaving.

#### 4.2.2 Scheduling must not be wall-clock aligned

If Modbot scheduled from a fixed clock — every 2 seconds on the even second — then **every Modbot
instance in the world would hit VRChat simultaneously**, producing synchronised spikes that are far
worse for VRChat than the same total volume spread evenly. It would also make Modbot's aggregate
traffic look exactly like a coordinated botnet, which is not an impression this project can afford.

So scheduling is relative and randomised:

```
  nextRun(type, n) = processStartTime
                   + randomOffset(type)          // fresh per process, per sync type,
                                                 // uniform in [0, interval)
                   + n × interval
                   ± jitter                      // small random per-tick, ~±10%
```

- **Offsets are generated at process start**, not persisted. A fleet restarting together — a Railway
  platform event, say — re-randomises and re-spreads rather than marching in lockstep.
- **Per-type offsets**, so one instance's six sync types do not fire as a burst either.
- **Per-tick jitter** on top, so the pattern does not settle into a regular comb that re-aligns with
  other instances over time.
- Intervals are measured from a **monotonic source**, so an NTP correction or VM migration cannot
  compress or stretch the schedule. `IModbotClock` (§4.4) remains the authority for *timestamps*;
  elapsed-time scheduling is a separate concern and must not be driven by wall-clock arithmetic.

#### 4.2.3 Degradation

Pacing caps mean a large group's full member sync simply takes longer — 50,000 members at 100 per
page is 500 requests, which at 1 per 2 s is a little under 17 minutes for a complete pass. That is
the correct behaviour, not a problem to optimise away.

When the global ceiling actually binds, because interactive work is consuming headroom, background
sync yields in a defined order: full member sweeps stretch first, then instances and roles, then
bans. **The audit log is protected** — it is cheap and it is the authoritative fact source (§5.3).

The UI shows real last-sync times per data type, never an implied freshness guarantee.

#### 4.2.4 Persistence

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
- **Limits are per-endpoint, and sometimes per-resource** — scoped to a particular group id rather
  than to the account. There is no single global allowance to reason about.

That third property is the one that matters, because it breaks the standard tool. Exponential
backoff assumes probing is free and optimises for converging quickly on the recovery moment; a
schedule of 1/2/4/8 minutes issues four probes and can add three to five minutes of penalty while
never converging. **Under a per-probe penalty, the objective is to minimise the number of probes,
not the total wait.**

#### 4.3.1 Prevention is the mechanism; recovery is damage control

A 429 is treated as an **incident, not control flow**. It is surfaced in the UI, recorded as a fact,
and counted — a healthy Modbot should emit approximately zero.

**Hierarchical budgets.** Because limits are per-endpoint and sometimes per-resource, a single
global bucket is insufficient. The gate maintains a **hierarchy of token buckets**, and a request
must acquire a token at *every* level it belongs to:

```
  global  ──▶  endpoint class          ──▶  resource (optional)
  (backstop)   e.g. groups.members          e.g. grp_1a2b…
```

- **Global** is a backstop, not the model — it exists because an account-wide limit may also apply
  and we cannot see it.
- **Endpoint class** groups related operations (`groups.members`, `groups.bans`, `groups.auditlog`,
  `users.read`, `moderation.write`). Each has its own configurable ceiling.
- **Resource** applies where a limit is observed to be scoped to a specific id. In a single-group
  appliance this dimension usually collapses to one group, but it must exist in the model — some
  endpoints key on user or world ids, and the appliance assumption must not bake it out.

Each ceiling is an operator-configurable estimate in `Settings`; Modbot runs at a conservative
fraction of it (proposed default **60%**) and never bursts to the estimate. These are guesses about
someone else's undocumented system and must be changeable without a redeploy.

**On a 429 — scoped cold stop.** Halt the **most specific bucket** that matched the failing request,
not all VRChat traffic. A limit hit on `groups.members` must not stop audit-log ingestion, which is
both cheap and the authoritative fact source.

Simultaneously apply a multiplicative decrease to **every ancestor bucket**, because a 429 on one
endpoint is evidence that the whole estimate is optimistic and may indicate proximity to an unseen
account-wide limit.

Interactive moderation actions are exempt from *other* buckets' stops but obey their own: if
`moderation.write` is cold, a ban fails fast with an explanatory message rather than queueing into
the penalty.

Recovery, applied per stopped bucket:

1. Wait a long fixed base period (proposed **15 minutes**). Issue nothing **on that bucket** at all.
2. Send **one** probe: the cheapest call in that endpoint class.
3. If it succeeds, resume that bucket at a reduced budget (below). If it fails, wait **a longer
   period** (proposed +15 minutes each time, linear) and probe once more.
4. After a small number of failures, leave the bucket stopped and alert the operator. Something is
   wrong that waiting will not fix.

Never more than one probe per waiting period per bucket, and no short retries at any point.

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
  resource is spent on — and now allocates across endpoint classes, not one flat pool.
- Rate-limit windows are measured on `IModbotClock` (§4.4), never the host clock.
- The UI must show real last-sync times per data type, plus per-bucket gate health
  (`Healthy | RateLimited | WafBlocked | Reauthenticating`) — an operator has to be able to see that
  Modbot is deliberately slow rather than broken, and *which* part of it is stopped.

#### 4.3.4 Provisional limits, and a standing instruction

Real per-endpoint limits are unknown and will be established **during implementation, endpoint by
endpoint**. Until then Modbot assumes a **sustained 0.3–1 request/second per endpoint class**.

> **Standing instruction for implementers (human or agent):**
> **Ask about the rate limit before building against an endpoint Modbot has not used before.**
> Do not infer a limit from a neighbouring endpoint, and do not raise a provisional value because a
> sync feels slow. These numbers are guesses about an undocumented system whose failure mode is
> opaque and punitive (§4.3); the cost of being wrong is asymmetric.

**The per-type pacing caps and the 2 req/s global ceiling in §4.2 are authoritative.** The table
below records where those numbers came from -- the pacing the previous implementation actually ran in
production (`old/Modbot/Consumers/`), which is the best empirical evidence available -- and covers
endpoint classes §4.2 does not schedule:

| Endpoint class | Provisional rate | Source |
|---|---|---|
| `groups.members` | **0.67 req/s** | `MemberConsumer` — 1500 ms between pages |
| `groups.bans` | **0.29 req/s** | `BanConsumer` — 3500 ms between pages |
| `groups.auditlog` | **0.29 req/s** | `LogConsumer` — 3500 ms between pages |
| `groups.invites` | **0.29 req/s** | no prior data; matched to the conservative neighbour |
| `users.read` | **0.33 req/s** | `UserProducer` — 3000 ms |
| `moderation.write` | **0.3 req/s** | unknown; interactive and low-volume, kept conservative |
| `auth` | negligible | login and re-login only |

Two caveats about that evidence:

1. **Those rates were per-group in a multi-tenant deployment**, with each group's consumer holding
   its own proxy session — so the per-account rate was N× the per-endpoint figure above. Under one
   account and no proxy rotation these become the *total* rates, which puts the new design at parity
   or safer for a single group.
2. **The old 429 handling was almost certainly too aggressive.** It waited 3 minutes and then
   retried via `goto TryAgain`, repeatedly. If a real penalty runs ~10 minutes, that is three or
   more premature probes, each adding 45–80 s — roughly 2.5–4 minutes of self-inflicted extension
   per incident. This is consistent with the penalty-extension behaviour described in §4.3 and is
   most likely how it was discovered. It is the direct reason the cold-stop base is **15 minutes**
   with **one** probe, not 3 minutes with retries.

### 4.4 Time — one authority, never the local clock

Modbot has **one clock**: `IModbotClock`, served by the backend. Nothing anywhere — server, client,
overlay — reads `DateTime.Now` or `DateTimeOffset.UtcNow` directly for anything that becomes a fact,
a rollup boundary, or a rate-limit window.

This exists because analytics correctness depends on timestamps from **many machines that Modbot
does not control**. A moderator's PC with a clock ten minutes off would otherwise silently corrupt
session durations, deduplication and every time-bucketed metric — and it would do so invisibly,
producing plausible-looking wrong numbers rather than an error.

**Client synchronisation.** Windows clients synchronise to server time rather than merely tolerating
skew, using the standard SNTP round-trip estimate:

```
offset = ((t1 - t0) + (t2 - t3)) / 2       round-trip compensated
    t0 client send   t1 server receive
    t2 server send   t3 client receive
```

- Re-measured periodically and on reconnect; smoothed across samples, and samples with an outlying
  round-trip discarded rather than averaged in.
- Clients report `occurred_at` in **server time**, having applied their offset, along with the
  offset itself and their confidence in it.
- The client never steps the machine's own clock. The offset is applied to reported timestamps only.

**Defence in depth — skew is corrected *and* tolerated.** Synchronisation reduces skew; it does not
guarantee it. So in addition:

- The server records its own `observed_at` on every ingested fact, which is authoritative for
  ordering and is never taken from a client.
- A client `occurred_at` that disagrees with `observed_at` beyond a plausible transport delay is
  **clamped and flagged**, not trusted and not silently discarded — a systematically skewed client
  is a fault worth surfacing to the operator.
- Deduplication windows (§5.7) are sized against residual skew, not raw clock skew, which is what
  makes a window narrow enough to preserve genuine rapid rejoins viable at all.

The gate uses the same clock for its rate-limit windows, so a host clock adjustment (NTP step, VM
migration, DST bug) cannot make it believe a penalty has expired.

### 4.5 Notifications — one pipeline, several channels

Modbot raises notifications through a single `INotifier` abstraction. Channels are delivery
mechanisms behind it, not parallel systems, so a new event type reaches every channel for free and
a new channel serves every existing event for free.

```
  event ──▶ INotifier ──▶ routing (severity × user preference × availability)
                             ├──▶ Email        SMTP, operator-supplied (§7.4)          M0
                             ├──▶ Discord      bot DM, or a channel                    M5
                             ├──▶ Web Push     VAPID; works in the browser and as PWA  M3
                             └──▶ Client       native toast + SteamVR overlay HUD      M3
```

#### 4.5.1 The failure mode is fatigue, not delivery

A moderation tool that notifies too much gets muted, and a muted tool misses the one alert that
mattered. So severity is the primary routing input, and **the defaults are deliberately quiet**:

| Severity | Examples | Default routing |
|---|---|---|
| **Critical** | VRChat credentials rejected; WAF block; sync stopped | Every available channel, immediately |
| **Warning** | Rate limit cold stop; accountability ticket opened (§5.8.5); repeat offender detected at ban threshold | Push + Discord immediately; email only if unacknowledged after a delay |
| **Info** | Member milestones, sync summaries, weekly metrics | **Digest only** — never an immediate interrupt |

Per-user, per-channel, per-severity preferences on top of that. An operator who wants everything by
email can have it; nobody gets it by default.

**Notifications are themselves deduplicated and rate-limited.** A sync failing every minute produces
one notification and then a state change, not sixty. This uses the same reasoning as §4.3: the thing
being protected is a scarce resource, and here the resource is the operator's attention.

#### 4.5.2 The overlay is the channel that justifies the others

A moderator inside VRChat cannot see email, cannot see Discord, and cannot see a browser. For the
person actually doing moderation at the moment it matters, **the SteamVR overlay is the only channel
that exists** (M3).

This is why notifications are one pipeline rather than a Discord feature and an email feature. The
routing table above is what lets "a flagged user just joined your instance" reach a moderator's
headset, a co-owner's phone via push, and the staff channel in Discord, from one raised event.

#### 4.5.3 Graceful degradation

Channels have different availability: email needs SMTP configured, Discord needs a bot token *and* a
linked account, push needs a browser subscription. Routing skips unavailable channels silently and
**never fails the action that raised the notification** — a ban does not fail because SMTP is down.
Delivery is at-least-once, attempted out of band, and failures are surfaced in settings as channel
health rather than thrown at the caller.

If an event's severity is Critical and *no* channel is available, that itself is surfaced in the UI
on next login, because a Critical alert nobody can receive is the same as no alerting at all.

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

#### 5.2.1 The counted-only path — when *not* to write a fact

One deliberate exception. Some events are **high-cardinality and individually worthless**: nobody
will ever ask "who sent a Discord message at 14:32:07", only "how many did this person send this
week". Discord message volume is the canonical case, and a busy server produces thousands a day.

For these, Modbot **increments a rollup directly and writes no fact at all**:

```
  Discord message ──▶ ROLLUPS only     (discord.messages, dimension = user)
                      no modbot_event row, no message content ever stored
```

Three reasons, and the third is the one that settles it:

1. **Volume** — a fact per message would dwarf every other source combined, for no query anyone runs.
2. **The invariant still holds** where it matters. These rollups are not recomputable, which is a
   real cost; it is accepted only for metrics where the aggregate *is* the datum.
3. **Privacy.** Modbot must never store message content, and storing a per-message row with author
   and timestamp is a social graph whether or not the text is attached. Counting sidesteps the
   question entirely — there is nothing to leak, subpoena, or accidentally expose in an export.

The test for this path: *would anyone ever query an individual one of these?* If yes, it is a fact.
Kicks, bans, joins, leaves and voice-channel sessions are all facts. Message counts are not.

### 5.3 Fact schema

```sql
modbot_event                     -- PARTITION BY RANGE (occurred_at), monthly partitions
  id               bigserial
  occurred_at      timestamptz  not null   -- when it happened (lower bound if imprecise)
  occurred_before  timestamptz  null       -- NULL = exact; else upper bound of the window
  observed_at      timestamptz  not null   -- when Modbot learned of it
  type             smallint     not null   -- MemberJoined | MemberLeft | BanAdded | RoleGranted |
                                           -- InstanceJoined | InstanceLeft | AvatarChanged |
                                           -- DiscordMemberJoined | DiscordVoiceJoined | ...
  subject_platform smallint     not null   -- VRChat | Discord
  subject_id       text         not null   -- usr_... or a Discord snowflake
  actor_platform   smallint     null
  actor_id         text         null       -- who caused it
  instance_id      text         null
  source           smallint     not null   -- AuditLog | SyncDiff | Client | Discord | Manual
  data             jsonb        not null

  INDEX (subject_platform, subject_id, occurred_at DESC)
  INDEX (actor_platform, actor_id, occurred_at DESC)   -- moderator pattern detection, §5.8.5
  INDEX (type, occurred_at DESC)
  GIN   (data)
```

**`subject_platform` exists because Discord is a second fact source** (§9.1), not only a second
surface. A Discord snowflake and a VRChat `usr_…` must not collide in one text column, and once
accounts are linked (M5) a dossier needs to query both sides of the same person. Keeping the
platform as its own column rather than namespacing the string (`dc:123…`) keeps the identifier
joinable against the user tables without parsing.

**`actor_id` gets its own index** because §5.8.5 asks actor-side questions — "everything this
moderator has done" — which the subject-side index cannot answer efficiently.

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
| **Dossier** | yes | One user, everything Modbot knows: join date, roles, **full moderation history** (kicks, warns, bans, by whom, with classifications), every audit-log mention, and (from M3) sessions, time spent, avatar history. "Who is this person" is the question staff ask most often. |
| **Accountability** | yes | Repeat-offender detection and moderator pattern detection (§5.8), both computed from audit-log facts — neither requires Modbot to perform actions. |
| **Metrics** | yes | Group health over time: member growth, join/leave rate, ban rate, staff action volume, per-moderator activity. |
| **Segments** | **no — M7** | Queryable cohorts (*"members with >10h in our worlds in the last 30 days, no bans, joined before June"*) → export, bulk action, giveaway draw. Requires presence data to be interesting. |

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
- Clients do not need to agree, elect a leader, or know about each other. Whichever report arrives
  first wins; the rest are no-ops. A client dropping out mid-instance loses no coverage, because the
  others are already reporting the same events.
- The `source` and `occurred_before` fields (§5.3) still apply: the earliest-arriving report sets
  the timestamp window, and a later authoritative source can supersede it.

#### 5.7.1 Sizing the deduplication window

The window is squeezed from both sides by real numbers:

| Bound | Value | Source |
|---|---|---|
| **Floor** — must not merge distinct events | a genuine leave-and-rejoin can complete in **15 s** on a fast cached world | observed |
| **Ceiling** — must merge reports of one event | residual clock disagreement between moderator PCs after synchronisation | §4.4 |

**Proposed window: ±5 seconds** — a 10-second span, comfortably inside the 15-second floor.

That is only viable because of `IModbotClock` (§4.4). Against *raw* machine clocks the ceiling is
unbounded — a PC whose clock is minutes off would need a window of minutes, which would swallow
every genuine rejoin. **Synchronising to server time is what makes a narrow window possible at all**;
it is a correctness prerequisite for deduplication, not a nicety.

**Windowed, not bucketed.** An earlier `floor(timestamp / bucket)` hash is rejected: it fails at
bucket boundaries. Two clients reporting the same join at `14:00:04.9` and `14:00:05.1` fall into
different 5-second buckets and both get written — and the failure is silent and intermittent,
appearing only for events that happen to straddle a boundary. Instead, ingest performs a range check
for an existing fact matching `(instance_id, subject_id, type)` within ±window, backed by an index on
`(instance_id, subject_id, type, occurred_at)`.

Note that join and leave are distinct `type` values, so a 15-second leave→rejoin cycle produces two
*joins* 15 s apart — outside the window — and never collides with its own leave.

The window is configurable, and a slow-PC join taking 30–60 s to complete does not widen it:
moderators observe the join when the user actually enters the instance, not while their client is
loading.

Sustained peak is therefore ~18k facts/hour ≈ 430k/day, which at the 90-day presence retention of
§5.5 is roughly 39M live rows — comfortable for partitioned Postgres. Typical groups (100–10,000
members, one or two instances) sit three orders of magnitude below this.

#### 5.7.2 Storage choice

Plain **PostgreSQL**, monthly partitions on `modbot_event`, so retention pruning is a partition
drop rather than a mass `DELETE`.

- **Not TimescaleDB**: continuous aggregates are licensed under the TSL, not Apache, which conflicts
  with the project's licensing care. It remains a cheap upgrade path precisely because it is an
  extension *on* Postgres.
- **Not ClickHouse or DuckDB**: breaks "Postgres is the only external service."

### 5.8 Moderation accountability

Three related capabilities, all of them queries over the fact log rather than new subsystems:
**repeat-offender detection** (subject-side), **moderator pattern detection** (actor-side), and the
**classification** data that makes both legible.

#### 5.8.1 Friction scales with reversibility — the governing principle

Volunteer moderators will not fill in a form for every kick. Requiring one does not produce better
records; it produces moderators who stop using Modbot, or who type "troll" five hundred times. A
required field that is always answered the same way carries no information and costs real goodwill.

So friction is graduated against how hard the action is to undo:

| Action | Reversibility | Volume | Required input |
|---|---|---|---|
| **Kick** | seconds — they can rejoin | high | **Optional** one-tap classification |
| **Warn / mute** | trivial | medium | **Optional** one-tap classification |
| **Ban** | deliberate act to reverse; removes someone from the community | low | **Full report required** |

The optional classification is **togglable to required** in settings, for groups that want stricter
record-keeping and have the staff culture to sustain it. Default is optional.

#### 5.8.2 Classification must be one tap, not a text box

This is the design decision the whole feature depends on. An optional free-text field gets a
single-digit completion rate; a row of buttons costs about a second and gets a high one.

Classification is a **fixed, group-editable enum** — for example: Crasher, Harassment, NSFW, Spam,
Advertising, Underage, Ban Evasion, Other — presented as buttons in every surface that performs a
moderation action (web UI, Discord bot, and later the SteamVR overlay, where typing is genuinely
hostile). Free text is an *additional* optional note, never the primary input.

**The classification is not paperwork; it is the signal.** Without it, moderator pattern detection
can only observe "Mod A kicked User B five times." With it, it can distinguish that from "Mod A
kicked User B five times, all classified *Crasher*, consistent with four other moderators' kicks of
the same user" — which is obviously fine — and from "five kicks, all *Other*, no other moderator has
ever actioned this user" — which is worth a human look. Data quality at the point of capture is what
makes the difference between a useful signal and an accusation generator.

#### 5.8.3 Ban reports

A ban requires a report before it is submitted: classification (mandatory here), a written
justification, and optional evidence references. Modbot pre-fills everything it already knows —
prior kicks and warnings, who issued them, their classifications, the user's join date and history —
so the moderator is confirming a case rather than composing one from memory.

This is deliberately the *only* place with real friction, because it is the only action that is
costly to get wrong and rare enough to afford the cost.

#### 5.8.4 Repeat offenders

Subject-side aggregation over facts: prior kicks, warns, mutes and bans, across all moderators and
all instances, with classifications where present.

Surfaced two ways: passively in the dossier (§5.6), and **proactively at the moment of action** —
when a moderator is about to kick someone, Modbot shows that this is the user's fourth kick in
thirty days from three different moderators. That is the moment the information is worth having, and
it is also the moment it costs nothing to display.

#### 5.8.5 Moderator pattern detection

Actor-side aggregation, looking for the shape the user described: one moderator repeatedly actioning
one user across separate instances and sessions, where no other moderator has.

Candidate signals, all computed from existing fact fields:

- Same `actor_id` → same `subject_id`, repeatedly, across distinct `instance_id`s and sessions.
- No other moderator has ever actioned that subject (an isolated grudge looks very different from a
  user everyone is removing).
- The actor's rate of unclassified actions, relative to their peers.
- The actor's action volume relative to peers over the same period.

**Design constraints, which matter more than the detection itself:**

- **It surfaces patterns for human review. It never accuses, and never auto-punishes a moderator.**
  Wrongly flagging a volunteer who was handling a persistent troll is corrosive in a way that a
  missed detection is not — the costs are asymmetric, so the thresholds should be too.
- Thresholds are configurable, with conservative defaults.
- A flagged pattern opens a **ticket**, which is a prompt for explanation rather than a disciplinary
  process. Group owners see it; the moderator is asked, not sanctioned.
- **Good classification behaviour reduces future friction.** Where a flagged pattern is fully
  explained by consistent classifications that other moderators corroborate, the ticket is
  auto-resolved and never shown. Moderators who spend the one second get asked fewer questions
  later — which is the incentive that makes §5.8.2 work in practice rather than only on paper.
- Resolutions are recorded as facts, so "this was reviewed and found legitimate" is itself history.

#### 5.8.6 What this needs, and when

Most of it does **not** require Modbot to perform moderation actions. Audit-log ingestion (M2)
already supplies `actor_id`, `subject_id` and action type for kicks and bans performed in VRChat's
own UI — so repeat-offender and moderator-pattern detection work from M2.5 onward.

Only the *capture* side — classification prompts, ban reports, tickets — requires Modbot to be the
one performing the action, and that is M4.

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

Deferred to M5: ban synchronisation in both directions, role synchronisation, VRChat↔Discord account
linking and auto-invite.

Runs as an `IHostedService` inside `Modbot.Host`. If no bot token is configured, it does not start
and the rest of Modbot is unaffected.

### 9.1 Discord is a second fact source, not only a second surface

From M5, the bot also **ingests**, giving the Discord side of a community the same treatment VRChat's
audit log gets — an equivalent activity history, queryable and charted alongside the VRChat data.

| Signal | Storage path | Why |
|---|---|---|
| Member joined / left | **fact** | Individually interesting; pairs with VRChat group join/leave |
| Role granted / removed | **fact** | Audit trail; underpins role sync (M5) |
| Moderation action (Discord ban, kick, timeout) | **fact** | Feeds §5.8 accountability alongside VRChat actions |
| Voice channel join / leave | **fact** | Sessions and time-spent, exactly like instance presence |
| **Messages sent** | **rollup only** (§5.2.1) | High volume, individually worthless, and content is never stored |
| Current member count, online count | rollup snapshot | A gauge, not an event |

Facts carry `subject_platform = Discord` (§5.3). Once accounts are linked (M5), a dossier answers
"this person" across both platforms rather than "this VRChat account" and "this Discord account"
separately — which is what makes a linked account worth having.

**Voice presence is treated exactly like instance presence**: same session model, same time-spent
rollups, same 90-day retention and same purge-user coverage (§5.5). A community running events in
Discord voice rather than in-world gets the same regulars detection and the same giveaway
eligibility as one running instances.

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
| **Ingest deduplication** | Replay the same instance event as reported by six independent clients with jittered timestamps; assert **exactly one** fact is written and time-spent totals match a single-client replay. Must include the **boundary case** that killed the bucketed design (reports at `14:00:04.9` / `14:00:05.1`) and the **floor case** (a genuine rejoin 15 s later yields *two* facts, not one). |
| **Time provider** (§4.4) | SNTP offset estimation under asymmetric latency; outlier round-trips discarded; a client whose clock is minutes off still produces correctly-ordered facts; a client `occurred_at` beyond plausible transport delay is clamped **and flagged**, not silently accepted or dropped. |
| Analytics | Property test the core invariant: rollups recomputed from facts equal rollups built incrementally. |
| Capacity handling | An instance reporting a capacity above the usual ceiling (exemption case, §3.1) must render and bucket correctly — regression guard against reintroduced constants. |
| API | Integration tests against Postgres via Testcontainers. |

The gate, the fact log and ingest deduplication get the most rigorous coverage: the gate because
everything depends on it, the fact log because its mistakes are unrecoverable, and deduplication
because its failure mode is **silent** — nothing errors, the numbers are simply wrong by 6×.

---

## 13. Non-goals for this spec

The Windows client and SteamVR overlay (M3); moderation actions (M4); Discord ban and role sync,
account linking (M5); instance launching and monitoring (M6); segments, cohorts and giveaways (M7);
group flagging, AI moderation and the federated warning network (M8).

Federation is deliberately last. It is the only feature that is inherently political, and nothing
else depends on it — Modbot must be fully useful whether or not it ever exists.

---

## 14. Milestones

| # | Slice | Spec |
|---|---|---|
| **M0** | Foundation: repo, licensing, CI, `IVRChatGate`, `IModbotClock`, auth, onboarding, **fact log + rollups + retention** | this |
| **M1** | Member/ban/invite sync, search UI, **facts emitted on diff** | this |
| **M2** | Audit log ingestion, parsing, viewer — **authoritative facts, dedups M1's inferences** | this |
| **M2.5** | User dossier with moderation history, metrics dashboard, repeat-offender + moderator pattern detection (§5.8) | this |
| — | Basic Discord bot: audit log sync, lookup commands | this |
| **M3** | **Windows client + SteamVR overlay; presence facts, ingest API, client auth** | future |
| M4 | Moderation actions; one-tap classification, required ban reports, accountability tickets (§5.8) | future |
| M5 | Discord: ban sync, role sync, account linking, auto-invite, **Discord as a fact source + analytics** (§9.1) | future |
| M6 | Instance launching, instance list monitoring and Discord sync | future |
| M7 | Segments, cohorts, giveaways | future |
| M8 | Group flagging, AI moderation, federated warning network | future |

### 14.1 Why the client comes third

The Windows client was originally sixth. It moved ahead of moderation actions, Discord sync and
instance tooling for one reason: **presence data is only ever valuable retroactively, and it cannot
be backfilled.**

This is the same argument as §5.1, applied to a milestone instead of a schema. Every week the client
does not exist is a week of instance history that no future feature can recover. "Who are our real
regulars?", "who was in the instance when that happened?", and every giveaway weighted by time spent
are all questions whose answers are being *destroyed* right now, not merely deferred.

Everything that moved down is, by contrast, **convenience over capability** — the work is deferred,
but nothing is lost by deferring it:

- **Moderation actions (M4)** — VRChat's own UI can already ban and kick. Modbot makes it faster and
  searchable; it does not make it possible.
- **Discord sync (M5)** — a second surface onto data M1–M2 already hold.
- **Instance tooling (M6)** — launching instances works today, just manually.

Deferring any of those costs staff time. Deferring the client costs history.

**Consequences of the move:**

- The client's unknowns arrive sooner. Log tailing, client authentication and the ingest API all
  need their own brainstorm → spec cycle before implementation, and that cycle should start early
  rather than after M2.5 lands.
- **Client authentication is a new auth surface** not yet designed. Staff log in with a
  username and password; a client running unattended on a moderator's PC needs its own credential
  with a narrow scope — ingest only, revocable per device. `ApiKey` (§6.3) exists but is scoped for
  the read API; whether it extends or a distinct device-token concept is needed is an M3 design
  question.
- The M0 substrate the client depends on — the fact log, `IModbotClock`, the deduplication window —
  is **already in the first spec**, so nothing needs pulling forward. This was fortunate rather than
  planned, and is why the move is cheap.
- **Segments and giveaways (M7) are no longer blocked by anything after M3.** They were sequenced
  late only because they needed presence data. That constraint now lifts three milestones earlier,
  so M7 could reasonably follow M3 directly.

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
13. **Rate-limit budgets are hierarchical** — global → endpoint class → resource (§4.3.1). VRChat's
    limits are per-endpoint and sometimes scoped to a specific group id, so one flat bucket cannot
    model them. A 429 cold-stops only the most specific matching bucket while decreasing every
    ancestor.
14. **One clock: `IModbotClock`** (§4.4). Nothing reads the local system clock for anything that
    becomes a fact, a rollup boundary, or a rate-limit window. Clients synchronise to server time
    (SNTP round-trip estimate) *and* the server independently records `observed_at` and clamps
    implausible client timestamps — corrected and tolerated, not one or the other.
15. **Rate limits are provisional and endpoint-specific** (§4.3.4). Assume 0.3–1 req/s per endpoint
    class, seeded from the previous implementation's production pacing. **Implementers must ask
    before building against a new endpoint** rather than inferring a limit from a neighbour.
16. **Versioning is calendar-based** `YYYY.M.D` with a letter for same-day hotfixes (§2.7), and the
    **API version is a separate integer** bumped only on breaking change. Comparison is componentwise
    and numeric -- unpadded components make lexical sorting wrong. MSI `ProductVersion` cannot hold
    a 4-digit year (major ≤ 255), so it uses the generated mapping in §2.7.2; getting it wrong makes
    upgrades silently refuse to apply.
17. **Sync pacing is fixed per type with a 2 req/s global ceiling** (§4.2), configurable downward
    only, with the cap enforced server-side. Headroom below the ceiling is reserved for interactive
    work, not spent on faster sync.
18. **Scheduling is never wall-clock aligned** (§4.2.2). Fixed-clock scheduling would make every
    Modbot instance worldwide hit VRChat on the same second -- synchronised spikes that are worse
    for VRChat than the same volume spread out, and traffic indistinguishable from a coordinated
    botnet. Offsets are per-process, per-type, regenerated at start, with per-tick jitter, measured
    from a monotonic source.
19. **Moderation friction scales with reversibility** (§5.8.1). Kicks and warns take an *optional*
    one-tap classification; only bans require a report. Requiring a form per kick does not produce
    better records, it produces moderators who abandon the tool or type "troll" five hundred times.
    Togglable to required in settings; default optional.
20. **Classification is a one-tap enum, never a text box** (§5.8.2) — and it is the *signal* that
    makes moderator pattern detection possible rather than an accusation generator, not paperwork.
21. **Accountability surfaces patterns for review; it never accuses or auto-punishes** (§5.8.5).
    Wrongly flagging a volunteer handling a persistent troll is corrosive in a way a missed
    detection is not, so the thresholds are asymmetric too. Good classification behaviour
    auto-resolves tickets, which is the incentive that makes the optional field actually get used.
22. **`subject_platform` / `actor_platform` columns** (§5.3), because Discord is a second fact
    source (§9.1) and a snowflake must not collide with a `usr_…` in one text column. `actor_id`
    gets its own index for §5.8.5's actor-side queries.
23. **A counted-only path exists for high-cardinality events** (§5.2.1). Discord message volume
    increments a rollup and writes no fact — too voluminous, individually worthless, and counting
    means there is no social graph to leak. The test: would anyone ever query an individual one?
24. **Deduplication is windowed (±5 s), not bucketed** (§5.7.1). A `floor(t / bucket)` hash fails
    silently at bucket boundaries. The window is bounded below by a genuine 15-second leave-and-
    rejoin and is only narrow enough to fit because of §4.4 — server-time sync is a correctness
    prerequisite for deduplication, not an optimisation.
