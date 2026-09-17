# Modbot — Foundation Design (M0–M2.5)

- **Date:** 2026-09-04
- **Status:** Draft, awaiting approval
- **Covers:** M0 (foundation), M1 (group sync), M2 (audit log), M2.5 (profile + metrics), basic Discord bot
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

One deployable (`Modbot.Server`) containing API, web UI, Discord bot and sync scheduler.
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

#### 2.7.1 Release version — `YYYY.M.PATCH`

```
  2026.1.0      first release of January 2026
  2026.1.1      next release that month
  2026.1.7      seventh
  2026.2.0      February — patch resets each month
```

Year, month, and a patch number that **resets at the start of each month**. Components are **not
zero-padded**.

It stays immediately legible — anyone can tell roughly how old a deployment is at a glance, which is
what you want to know when a self-hoster reports a bug — while the patch number carries the
same-period releases that would otherwise need a suffix.

**Comparison is componentwise and numeric, never lexical.** String sorting puts `2026.1.10` before
`2026.1.2`, which is wrong, and unpadded components guarantee someone will eventually hit it. A
comparison helper ships with the version type and is used everywhere; no ad-hoc string comparison of
versions anywhere in the codebase.

#### 2.7.2 Mapping onto formats that will not accept it

Two build systems require numeric versions in shapes this format does not fit. Both mappings are
mechanical, monotonic, and must be generated by the build rather than typed by hand.

**.NET `AssemblyVersion`** needs four numbers, each 0–65535:

```
  2026.1.7  →  2026.1.7.0
```

Every component fits directly. The display version goes in `InformationalVersion`, which is what the
UI and logs show.

**MSI `ProductVersion`** is the one that will bite at the first client release. Windows Installer
allows `major.minor.build` where **major ≤ 255** — and `2026` does not fit. It also ignores any
fourth field entirely when deciding whether an upgrade applies, so nothing can be hidden there.

```
  (year - 2000) . month . patch

  2026.1.0   →  26.1.0
  2026.1.7   →  26.1.7
  2026.12.3  →  26.12.3
  2027.1.0   →  27.1.0
```

A direct component-for-component mapping with no packing arithmetic. Monotonic within a year
(patch rises, then month rises and patch resets) and across year boundaries, valid until 2255, every
component in range.

Getting an MSI version mapping wrong produces installers that run fine but **silently refuse to
upgrade** — which presents as a broken updater and is very hard to diagnose after release. The
absence of packing here is the main practical reason this format was chosen over a day-plus-suffix
scheme.

#### 2.7.3 API version — a plain integer, and **both sides advertise a range**

The API version is a single integer, incremented **only on a breaking change** to the interfaces the
client depends on. It is deliberately not the calendar version: release versions change constantly,
API compatibility changes rarely, and a client forced to match a calendar version would break every
time the server shipped a typo fix.

**Both sides support a range, not a point.** This is the part that matters, and the reason is §2.7.4:

```
  server advertises   { apiVersionMin: 3, apiVersionMax: 6 }
  client supports     { apiVersionMin: 4, apiVersionMax: 7 }
  negotiated          6        highest mutually supported
```

- The server exposes its range on an **unauthenticated** endpoint, so a client can negotiate before
  it holds credentials.
- Negotiation picks the highest version both sides support. No overlap is a clear, actionable error
  naming both ranges — never a parse failure, and never a silent no-op.
- **Deprecation policy:** a server supports the current API version plus **at least the two previous
  ones**, and never drops a version less than **12 months** old. This is what gives client authors a
  wide enough window that moderators are not forced to update in lockstep with every server they
  connect to.

#### 2.7.4 One client, many servers, different versions

**A moderator can be staff in more than one group**, each running its own Modbot on its own upgrade
schedule. One person's client may therefore be talking to a server on API version 4 and another on
version 6, at the same time.

This rules out the obvious design — "read the server's version, install the matching client" —
because there is no single server to match. It also rules out installing one client per server: they
would each tail the same VRChat log, duplicate the same work, and present the moderator with two tray
icons and two updaters for one machine.

So:

- **The client is version-agnostic by construction.** It implements the protocol once per supported
  API version behind a single internal interface, and selects the right implementation **per server
  connection** after negotiating with each.
- **Installing is just "newest".** Because servers support a range covering the last several versions,
  the newest client speaks to every reasonably-current server. Client selection needs no knowledge of
  any particular server, which removes the per-server lookup entirely.
- **Compatibility is a server obligation, not a client scavenger hunt.** The deprecation window in
  §2.7.3 is what makes "install the newest client" reliably correct.

The ingest surface is small — presence facts, time sync, pairing, handshake — which is precisely why
this is affordable. A small API that only ever gains optional fields can stay compatible for years,
and breaking changes should be rare enough that the two-version window is generous rather than tight.

M3 §5.5 covers the client-side consequences: shared log reading, per-server routing, and the hard
rule that one group's events must never reach another group's server.

#### 2.7.5 Upgrade risk is metadata, not something you infer from the number

No version scheme tells a self-hoster whether an upgrade is *safe*. Someone on `2026.1.0` looking at
`2026.6.2` cannot tell whether that is a quiet run of bugfixes or six months of schema changes — and
Modbot has a database, migrations, and a one-click redeploy button, which is precisely the
combination where guessing wrong hurts.

So every release declares it:

| Field | Meaning |
|---|---|
| `breaking` | Behaviour or interfaces changed in a way that needs attention |
| `requiresMigration` | This release runs schema migrations |
| `minimumUpgradeFrom` | You must already be at least this version — older installs must upgrade through a waypoint first |
| `upgradeNotesUrl` | What to read before upgrading |

##### `minimumUpgradeFrom` is enforced locally, with no network

This is the field that prevents actual breakage, and its enforcement **must not depend on reaching
the release host** — an operator upgrading an air-gapped or update-checks-disabled deployment needs
the same protection as everyone else.

On startup, before running any migration, Modbot compares the **version recorded in the database**
against the binary's own `minimumUpgradeFrom` and **refuses to start** if the gap is too wide,
naming the waypoint release to install first. The database records its version on every successful
migration, so this check is entirely local.

The failure mode it exists for is squashed migrations: once old migrations are collapsed, a
sufficiently ancient install has no path forward and the migration fails partway — leaving a database
in a state nobody designed. Refusing to start is dramatically better than that, and the message says
exactly which version to go through.

##### Update checking and the no-phone-home rule

Showing an upgrade banner requires reading the release manifest, which is an outbound request. The
central services spec forbids telemetry and phone-home, so this needs to be an explicit decision
rather than something that creeps in.

**The check is enabled by default and can be disabled**, and the disclosure is honest: it is a plain
HTTP GET for a static file that transmits **nothing about the deployment** — no group, no member
count, no version, no identifier. What it unavoidably reveals is the same thing any HTTP request
reveals: that an IP address fetched a file.

That is a real disclosure, not a nil one, which is why it is stated plainly at onboarding and why the
switch exists. It is on by default because self-hosted moderation software silently running months
behind on known bugs is the larger harm.

### 2.8 Vertical slices, not layered MVC

Code is organised **by feature, not by technical layer**. One folder per feature containing its
endpoint, request and response types, validation, handler and queries:

```
  src/Modbot.Api/Features/
    Auth/
      Login/           LoginEndpoint.cs  LoginRequest.cs  LoginHandler.cs  LoginValidator.cs
      CreateApiKey/
    Onboarding/
      CreateAdmin/  VerifyVRChat/  TestConnection/  SelectGroup/
    Members/
      SearchMembers/  GetMember/  GetProfile/
```

Not the layered shape the old implementation used — `Controllers/`, `Services/`, `Models/DTO/`,
`Consumers/` — where a single feature is spread across five directories.

**Why, specifically for this project:**

1. **Agents write most of this code, and a slice is one unit of context.** Implementing "search
   members" means reading and editing one folder. Under layering it means holding a controller, a
   service, a DTO, a validator and a mapper simultaneously, and edits get less reliable the more
   files a change has to touch at once.
2. **Parallel work does not collide.** Two agents on two features touch two folders. Under layering
   they both edit `Services/` and `Models/DTO/`, and every task queues behind merge conflicts.
3. **Review is a folder diff.** For a project meant to accept contributions from people who have
   never read the rest of it, "here is the whole feature" is worth a great deal.
4. **The shared kernel already exists.** VSA's known failure is duplicated logic across slices, which
   is prevented by having genuinely shared infrastructure — and §4 has already produced exactly that:
   `IVRChatGate`, `IModbotClock`, `IFactWriter`, `ISecretProtector`, `INotifier`. Anything
   cross-cutting belongs to one of those or to `Modbot.Core`; slices hold feature logic only.

**Minimal APIs, not MVC controllers.** Each feature's endpoint maps itself, injecting what it needs.

**No MediatR.** The indirection buys little when handlers are already one-per-slice, it makes call
paths harder to follow for both humans and agents, and — decisively for a project with this
licence — MediatR's current versions are commercially licensed. ASP.NET's own middleware and endpoint
filters cover what its pipeline behaviours would have. The same reasoning already excluded AutoMapper
in favour of Mapperly (§11).

The rule that keeps this honest: **if two slices need the same logic, it moves into `Modbot.Core` or
behind one of the §4 abstractions — it is never copy-pasted, and never left in one slice for another
to reach into.**

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
│  ├─ Modbot.Analytics/       # fact emission, daily totals engine, retention, segment queries
│  ├─ Modbot.Discord/         # Discord.Net bot as IHostedService
│  ├─ Modbot.Api/             # HTTP endpoints, auth, onboarding
│  ├─ Modbot.Web/             # React + Vite + TS; builds into Modbot.Server/wwwroot
│  └─ Modbot.Server/            # the single deployable; composition root
├─ tests/
│  ├─ Modbot.Core.Tests/
│  ├─ Modbot.VRChat.Tests/
│  └─ Modbot.Analytics.Tests/
├─ libs/                      # GITIGNORED — reference clones of the VRChat SDKs
├─ old/                       # GITIGNORED — previous SaaS implementation, reference only
├─ LICENSE  CLA.md  TRADEMARK.md
├─ Dockerfile  .railway/railway.ts
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

### 3.1.1 A standing rule: VRChat ids are opaque — never validate their format

`usr_<uuid>`, `grp_<uuid>`, `wrld_<uuid>` describe *modern* ids. **VRChat changed its id format years
ago, and legacy ids follow no structured format at all** — they are customised, arbitrary, and still
in active use by long-standing accounts.

So, everywhere in Modbot:

- **No regex validation of id shape.** Never `usr_[0-9a-f-]{36}`, never a length check, never a UUID
  parse. An id is an opaque string.
- **No id generation or normalisation.** Ids are stored and compared exactly as received.
- **Parsing extracts, it does not validate.** When pulling an id out of a log line or an instance
  location string, match the *delimiters* — not an expected id shape. `(usr_…)` at end of line;
  `group(` … `)` inside a location.
- Database columns are `text`, not `uuid`.

The failure mode is the reason this is a standing rule rather than advice: a format check would work
perfectly in testing, pass review, and then **silently exclude exactly the oldest and most
established members of a community** — the founders, the long-time regulars — while appearing to work
for everyone else.

### 3.2 A standing rule: the client's source is its privacy policy

The Windows client (M3) runs unattended on a volunteer moderator's personal PC, reads VRChat's log
files, and sends what it observes to a server. That is, accurately described, the shape of spyware.
The only thing separating Modbot from spyware is that its behaviour is **honest, bounded and
verifiable** — and a moderator asked to install it is entitled to check rather than trust.

Therefore, in `Modbot.Companion` and `Modbot.Overlay` specifically:

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
                     • current-state tables        • modbot_event
                       GroupMember, GroupBan, …     • never mutated, never overwritten
                              │                            │
                              │                            ▼
                              │                     DailyTotalsJob (recomputable aggregates)
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
    // The endpoint is an explicit parameter, NOT inferred from the callback.
    // A Func is opaque: the gate cannot see which endpoint a lambda will call,
    // so it cannot pick a bucket. An unknown endpoint class THROWS rather than
    // borrowing a neighbour's budget -- see 4.3.4's standing instruction.
    Task<VRChatResult<T>> ExecuteAsync<T>(
        VRChatEndpoint endpoint,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default);

    VRChatSessionState State { get; }   // Healthy | Reauthenticating | RateLimited | WafBlocked | Unconfigured
                                        // | SignInWaiting | NoGroupAccess (§4.1.2)
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
- Serialises all calls behind a token bucket — **not a `SemaphoreSlim`**. A semaphore releases in
  arrival order, which cannot satisfy the preemption requirement on the next line; a priority gate
  decides the order at release time instead.
- **Priority queue**: interactive moderation actions preempt background sync.
- `401` → check the session without signing in; sign in again (TOTP via the stored 2FA secret) only
  if it is really bad, within the limit per hour, then retry once (§4.1.2).
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
`new Multimap<string, string>()`, so **headers are lost** and `Retry-After` is unavailable there.

**Corrected 2026-09-12:** an earlier draft said the WAF body lands in `ErrorText`. It does not. The
catch path passes `ex.Message` to the constructor's **`rawContent`** parameter, so the body arrives in
**`RawContent`** and `ErrorText` stays null — formatted as `"Error calling {method}: {body}"`, so
`WafCode` is still extracted by locating the JSON payload within that string.

Headers are also only lost on the **catch** path. A `429` arriving as an ordinary HTTP response keeps
them, so the classifier reads both fields and both paths rather than assuming either.

Worth fixing upstream, but **it changes nothing about rate limiting**: VRChat does not send
`Retry-After` at all, so there is no header to recover (§4.3). The gate must tolerate the absence
of both and must not regress if they later become populated.

#### 4.1.1 The gate owns authentication — no convenience helpers

`VRChat.API` ships `LoginAsync` and `TryLoginAsync`. **`IVRChatGate` uses neither.** They exist to get
a script running in five lines; Modbot is a long-running daemon whose entire rate-limiting design is
built on reacting to HTTP status codes, and the helpers discard exactly that.

`LoginAsync` returns `null` when the final `GetCurrentUser` is not `200 OK`, keeping no status. So a
`401` (re-authenticate), a `429` (cold stop, §4.3.1), a `403`, and a Cloudflare WAF block (tell the
operator, §2.3.1) all arrive as the same bare `null` — and §4.1 requires a different response to each.

The gate therefore drives the flow itself:

```
  Authentication.GetCurrentUserWithHttpInfoAsync()   → status + body + cookies
    ├─ 200 and no requiresTwoFactorAuth  → authenticated
    ├─ 200 and requiresTwoFactorAuth     → Verify2FAWithHttpInfoAsync(TOTP), then re-fetch
    ├─ 401                               → credentials genuinely rejected; surface to operator
    ├─ 429 (on a sign-in)                → one-hour wait, stored; nothing needing a session is sent (§4.1.2)
    ├─ 429                               → cold stop; never retry (§4.3.1)
    └─ other / WAF                       → classify, surface as health state
```

This generalises: **nothing in Modbot calls the convenience overloads.** Every call is
`...WithHttpInfoAsync`, because the status, the headers and the raw body are what the gate is for.
A plain `await vrchat.Groups.GetGroupAsync(id)` anywhere in the codebase is a review failure, not a
shortcut.

**Split-ready:** the token bucket and the priority gate sit behind an `IRateLimitLease`. The
in-process implementation is local state; a future distributed implementation is a Redis lease. Same
interface, same call sites.

#### 4.1.2 Signing in is scarce — check the session, count every sign-in, wait out a limit

*Added 2026-09-15.* VRChat allows about **four or five sign-ins an hour**, publishes nothing about
it, and answers the next one with a **one-hour block**. Before this section existed, every restart
signed in with the password at least once and often three or four times, so a few deploys in an
evening were enough to lock Modbot out for an hour.

**What went wrong.** The SDK adds a `Basic` header to `GET /auth/user` whenever a username is set on
the client, stored cookie or not. The gate built its client with both, and "checked the stored
cookie" with `/auth/user` — so every start-up sent the password. It also put the cookie in the SDK's
configuration, which copies it back into the cookie jar before every request: when VRChat handed
out a new cookie, the next call went out with the old one and got a 401. Every 401 then signed in
again, once *per failing call*, so two producers failing together cost two more sign-ins. And
"Test connection" in the wizard threw the session away on purpose and signed in.

**What counts as a sign-in.** Any request that sends the password or a two-factor code:
`GET /auth/user` with the password, and `Verify2FA`. The `GET /auth/user` that follows a two-factor
code is counted too — VRChat's counting is undocumented and the careful reading costs little. A full
sign-in with a two-factor challenge is therefore **three**; with a stored `twoFactorAuth` cookie,
VRChat usually skips the challenge and it is **one**. `/auth/user` is never used for anything but a
real sign-in.

**Checking a session without signing in.** A stored session is always tried first — on start-up,
after a 401, and when the wizard or Settings asks. The maintainer confirmed that `GET /auth` (Verify
Auth Token) does not sign in again, so the check is, in order, each step asked only when the one
before could not say:

1. **`GET /auth`.** `ok: true` → the session is good. `ok: false` or a 401 → it is bad; sign in.
   Anything else (a 5xx, a timeout, a Cloudflare block, a body without `ok`) → unknown; go on.
2. **Get User** on a profile any signed-in account can read — by default VRChat staff member Nayir,
   `usr_fbdf2c30-fcea-4220-88f4-c3f83e11215a`, changeable in `settings.vr_chat_session_check_user_id`
   if VRChat ever removes it. 200 → good. 401 → bad; sign in. Anything else → unknown; go on.
3. **Get Group** on the managed group. 200 → good. A 401 or 403 here is **not** read as a bad
   session: step 2 did not end in a 401, and a refusal from the group could just as well be the
   group. Modbot reports that it cannot tell and does not sign in.

A 429 at any step is that bucket's ordinary cold stop (§4.3.1): the check stops there, nothing is
signed in, and no sign-in banner is shown. `GET /auth` has its own class, `auth.verify`, on the auth
lane at one request per ten seconds — **a guess, not a measurement, that needs confirming** — and it
is not counted against the sign-ins per hour.

**Lost group access is not a sign-in problem.** When the session is known to be good and group
requests still get 401 (or Get Group gets 403), the gate shows `NoGroupAccess` — "The VRChat account
cannot read the group" — and never signs in for it. The finding stands for fifteen minutes before a
further 401 checks again, so a producer's refused polls do not each spend a check.

**The limit per hour.** Every counted request is written to `vrchat_sign_in_attempt` *before* it is
sent, so a restart, a crash or a crash loop cannot hand the allowance back. At most
`RateLimitOptions.SignInsPerHour` go out in any rolling hour: **4 by default, and configuration may
lower it but never raise it**. A sign-in that cannot finish in the room left is not started — a
two-factor sign-in with two left waits rather than spending one and stopping. When the limit is used
up, Modbot waits until enough of the oldest are an hour old.

**A rate limit on a sign-in.** A 429 on any counted request — or a refusal whose body says "too
many"; VRChat's exact answer to this has not been captured — starts a **one-hour wait from that
moment**, stored in `settings.vr_chat_sign_in_wait_until`. While it lasts:

- nothing that could count as a sign-in is sent, and **nothing that needs a session either** — not
  even a session check. Calls return `SignInWaiting` straight away, the way a cold stop does, and
  sync treats it as a rate limit and backs off;
- a restart reads the wait and keeps to it;
- an operator entering new credentials, or pressing "Verify and store" or "Test connection", does
  not skip it — that would only turn one hour into two. The credentials are stored, the answer is a
  short "Waiting to sign in to VRChat.", and the attempt after the wait uses them.

When the hour is up Modbot makes **one** attempt, on a five-second timer so it happens even when
nothing else is asking VRChat for anything. If that is rate limited too, the next hour starts from
then. Modbot's own limit being used up is the same kind of wait and is shown the same way.

**One at a time.** The gate holds one lock for checking and signing in. Calls that get a 401 while a
check or sign-in is under way share it and take its result; after a sign-in fails, the next 401 does
not start another at once — it goes through the limit. Credentials VRChat has refused are not sent
again on a producer's behalf until they change.

**Cookies.** Both cookies are stored encrypted in `settings.vr_chat_auth_cookie_encrypted`, with the
username and user id they belong to beside them; a session belonging to a different username is
never offered. Stored cookies go into the client's cookie jar rather than the SDK's configuration, so
a cookie VRChat replaces stays replaced, and a replaced cookie is written back to the database.
Changing the credentials clears the session. The cookie and the token `GET /auth` returns are never
logged and never returned by any endpoint.

**What an operator sees.** The gate's state becomes `SignInWaiting`. `GET /api/health/gate` carries
`signInWait { reason, retryAt, secondsLeft }` (seconds counted on the server's clock), plus
`lastSignedInAt`, `signInsInLastHour` and `signInLimit`. The web app shows a red banner at the top of
every page of the app shell and the setup wizard, for anyone signed in, with no way to close it:
*"Service account authentication issue - ratelimited - retrying in 59 minutes and 32 seconds"*,
counting down each second and reading "retrying now" at zero until the state changes. The server log
gets one Warning when a wait starts and one Information when signing in works again. The Health page
and the Settings VRChat card show "Last signed in".

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
| | *(`groups.read` class total)* | *0.200* | |
| | **Sum** | **1.450** | |

**Global ceiling: 2 requests/second** across the types above.

**`users.read` runs in its own lane at 1 req/s and is *not* counted against that ceiling** — see
§4.2.5. VRChat rate-limits the user endpoint separately and far more permissively, so constraining it
to the same budget as the group endpoints would cost days of sync time for no benefit.

The ceiling is enforced by the top bucket of the hierarchy (§4.3.1) and never exceeded.

**The audit log's `offset` is capped at 7,500.** Measured 2026-09-13: `offset=7501` on
`GET /groups/{id}/auditLog` returns HTTP 400, not 429 — no cold stop, no retry. The audit-log
producer never sends an offset above it and treats reaching it as the end of readable history.

The 0.55 req/s between the sum and the ceiling is not spare capacity to be spent on faster sync — it
is reserved for **interactive work**: moderation actions, onboarding, and a moderator's live queries,
which preempt background sync (§4.1). A background scheduler that consumed the full ceiling would
make every ban wait behind a member page.

#### 4.2.1 These are caps, and they are configurable downward only

Every rate above, plus the global ceiling, is operator-configurable through a slider in settings.

- **The values above are hard maxima.** Configuration may only lower them. The cap is enforced
  server-side on write, not merely in the UI, so it cannot be raised by editing a request.
- Lowering is always allowed and always safe: a group on a constrained host, sharing an account, or
  simply wanting to be gentler can dial any of them down, at the cost of staleness.
- The settings UI shows the resulting total request rate as sliders move, and shows how far it sits
  under the global ceiling — so an operator can see how much room they are leaving for interactive requests.
  Both numbers are computed server-side, so the screen never re-derives this table's sum.

##### 4.2.1.1 Two knobs, not one — what the slider actually writes

> **Added 2026-09-13, on implementing this section.** It had been written as though a class has one
> configurable number. It has two, and conflating them produces a control that looks right and is
> off by the budget fraction.

§4.2's rates are **pacing caps in req/s**. §4.3.1's configurable value is an **estimate of VRChat's
limit**, which Modbot then runs at a fraction of (0.6 by default). `RateLimitClassOptions` has
carried both since it was written — `HardMaxPerSecond` and `DefaultCeilingPerSecond` — and the spec
described only the first.

So, precisely:

| | |
|---|---|
| The operator **writes** | `ceilingPerSecond` — the estimate of VRChat's limit |
| The screen **displays** | `effectiveRatePerSecond` = `min(hardMax, ceiling × budgetFraction)` |
| The slider's **maximum** | `hardMax / budgetFraction` |

At the default fraction that maximum equals the shipped ceiling, so the control starts at the top of
its range and can only move left — which is what "configurable downward only" is supposed to feel
like.

##### 4.2.1.2 Two honest exceptions to "downward only"

**"Group roles" is not independently configurable.** It shares the `groups.read` class with group
info, and the producer fetches roles through `GetGroup(includeRoles: true)` — one request, not two.
There is no separate rate for a slider to move.

**The poll rate knobs are not req/s and the rule does not literally cover them.** Lowering the
audit-log *maximum* interval makes the producer poll more often, not less. It is safe — the
limiter's cap binds whatever the poll rate asks for, which is exactly why the two are separate
mechanisms — but "configuration may only make Modbot gentler" is not true of that field, and
pretending otherwise would be the kind of claim somebody later discovers by reading the code. Only
§4.2's pacing floors are enforced on the pollRate.

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

When the global ceiling actually binds, because interactive work is using up the room left, background
sync yields in a defined order: full member sweeps stretch first, then instances and roles, then
bans. **The audit log is protected** — it is cheap and it is the authoritative fact source (§5.3).

The UI shows real last-sync times per data type, never an implied freshness guarantee.

#### 4.2.4 Persistence

All persistence is **batched** — one `SaveChangesAsync` per page, not per entity. The old
implementation issued two round-trips per member, so a 5,000-member group cost roughly 10,000
sequential database calls per sync.

Mapping from SDK models to entities uses **Riok.Mapperly** (source-generated, compile-time verified),
not AutoMapper.

#### 4.2.5 User profile sync — the expensive one

> **Revised 2026-09-15, again 2026-09-16.** VRChat has two reads of a person, not one, and they no
> longer carry the same fields: `GET /users/{userId}` (`users.read`) stopped returning the bio, and
> `GET /profile/{userId}` (`users.profile`) carries it. The public profile is now the main read and
> the user object is read about once a week per person. API specification v1.21.0 confirmed the
> split and widened it: the bio, the bio links, the badges, the user icon, the profile picture
> override and the current avatar pictures have all left the user object for good. The pacing, the
> lane and the exemption described in this section apply to both classes. See "Two reads of a
> person" below.

`GroupMember` carries membership data but **no profile**: no bio, no status, no avatar, no pronouns.
Those live on the two per-person reads and must be fetched **one user at a time**.
`Instance.Users` would have returned them in bulk, but VRChat populates it only for VRChat staff and
world owners (M3 §7.2.1).

So per-user sync is unavoidable. It is, however, **not subject to the global ceiling**: VRChat
rate-limits `users.read` separately and far more permissively than the group endpoints, so it runs in
**its own lane at 1 req/s**.

That changes the picture substantially:

| Group size | Full sweep @ 1 req/s | (had it shared the group budget, 0.2 req/s) |
|---|---|---|
| 8,000 (typical) | **~2.2 hours** | ~11 hours |
| 16,000 (typical upper) | **~4.4 hours** | ~22 hours |
| 150,000 (largest observed) | **~1.7 days** | ~8.7 days |

A typical group therefore refreshes **every** profile several times a day, and even the 150k outlier
completes in under two days.

##### The trade being made

Total outbound can now reach roughly **3 req/s** at peak — 2 for the group endpoints plus 1 for
users — where §4.3.1's global bucket is otherwise the backstop against an unseen account-wide limit.

This is a deliberate, evidence-based exemption: the user endpoint is observed to be governed
separately and laxly. It is also the single assumption in §4.2 most worth re-testing, so:

- the users lane is **configurable downward** like every other rate (§4.2.1);
- a 429 on `users.read` cold-stops that lane **and** applies the multiplicative decrease to the global
  bucket anyway (§4.3.1), because a limit hit anywhere is evidence the whole model is optimistic;
- if 429s appear on *other* endpoint classes shortly after user-sync bursts, that is the signature of
  an account-wide limit and the exemption should be withdrawn. Worth watching for explicitly rather
  than discovering slowly.

##### Priority still matters

Tiering is no longer about a sweep that never finishes — it is about **freshness where it counts**.
Someone in your instance right now should have a profile refreshed minutes ago, not four hours ago,
and a 150k group still cannot refresh everyone continuously.

##### Freshness is visible, never implied

Every profile records when it was last refreshed. Anywhere profile-derived data is shown — a bio, an
avatar, a screening result (§4.2.6) — the UI shows its age, and stale data is labelled stale.

This matters most for the negative case: **"no flags found" on a profile last refreshed in March is
not the same claim as "no flags found" on one refreshed an hour ago**, and a moderator must be able
to tell the difference.

##### Two reads of a person, and how often each one runs

VRChat answers two different calls about somebody, and since 2026-09-15 they carry different
fields. The full comparison is in `.agent/research/vrchat-public-profile-findings.md`; the summary
is:

| | `users.read` — `GET /users/{userId}` | `users.profile` — `GET /profile/{userId}` |
|---|---|---|
| Carries alone | status line, status, join date, the full tag list, last platform | **bio**, bio links, badges, trust tags, languages, the group being represented, VRC+ |
| Carries too | display name, pronouns, age verification, banner and nameplate | display name, pronouns, age verification, banner and nameplate |
| How often | on first sight, then **once every seven days** per person | the existing queue and schedule — the main read |

**The public profile is the main read.** It carries what Modbot uses most and what moderators look
at: the bio, the pronouns, the name, and the age verification that feeds the sticky 18+ flag.

**The user object is read rarely**, because what it carries alone either never changes (the join
date), moves on a scale of weeks (the tag list, including the trust rank and `system_troll`), or is
something nothing in Modbot decides anything from (the status line, the platform). A week of
staleness on those costs nothing; a week of staleness on a bio would cost a moderator the thing
they opened the profile to read. For a 10,000-person group the rare read is about 1,430 requests a
day — 0.017 req/s against a 3.5 req/s budget.

**Neither read carries a person's picture any more.** The profile picture override and the current
avatar pictures left the user object in specification v1.21.0, and the public profile returns its
own copies only to the account that owns it. The stored columns keep their last value and are never
filled again; the one picture still on offer for somebody else is the public profile's `iconUrl`,
the VRC+ user icon, which is a different field and is not written to them.

**Its own budget, at the same rate** (the maintainer, 2026-09-15). Two buckets rather than one, so
the rare read cannot eat the frequent one's allowance; two lanes rather than one, so they never
queue behind each other. Both are exempt from the global ceiling for the reason above, and
withdrawing that exemption means withdrawing both.

**Neither read may overwrite what it did not carry.** A response fills a column only when its body
has that key — present and empty clears the column (somebody did clear their bio), absent leaves it
alone. Without that rule the user object's missing bio would wipe the one the public profile had
just filled in. Each read also keeps its own copy of its own body (`raw_profile` and
`raw_public_profile`), so neither erases the other's record of what VRChat said.

**A 404 from one call is not a missing account.** Each read records its own "not found" mark, and
the person counts as gone only when both calls answered 404, or when one did and the other has
never succeeded for them. A success on either call clears that call's mark.

#### 4.2.5.1 User search — interactive only, never swept

`users.read` covers users Modbot already knows about. **Finding someone who is not in the group** —
to ban pre-emptively, or to invite — needs the search endpoint, and that is a different animal.

**Its rate limit is severe: 1 request per 3.5 seconds.** An order of magnitude worse than
`users.read`, and roughly 25× worse than the group endpoints.

So it gets its own bucket, its own lane, and one hard rule:

> **Search is never called by an automatic sync, a background job, or a scheduled task.**
> Only ever by a human who typed something and is waiting for the answer.

That is not a guideline to be relaxed later. Any background use — pre-resolving names, enriching a
list, warming a cache — would consume the entire lane and make interactive search unusable, which is
the only thing it is for.

Consequences for the UI, since 3.5 seconds is long enough that people will click twice:

- **Debounce and require submission.** No search-as-you-type; a query fires when the user asks for it.
- **Show the queue.** If a search is waiting behind another, say so, with position. A spinner that
  might mean three seconds or thirty is worse than a number.
- Deduplicate identical in-flight queries, and cache recent results briefly so a re-search of the same
  term is free.
- **Search the local cache first, always.** Most lookups are for people already in the group, and
  those are answered instantly from `pg_trgm` (§6.4) with no API call at all. The remote search is the
  fallback for "not found locally", offered explicitly rather than fired automatically.

That last point is the one that makes the limit tolerable: the expensive path is reached only when
the cheap one has already failed.

#### 4.2.6 Profile screening

Bios, display names, statuses and pronouns are user-authored text, and screening them is a core
moderation need — trolls who advertise themselves in their profile are a recurring problem in VRChat
communities, and finding them by hand at 16,000 members is not possible.

Screening runs on **every profile refresh**, so its coverage is exactly the sync coverage above.

##### Term lists are local, matching is normalised

- **Term lists are group-configured.** Modbot ships **no default list**, for the same reason it ships
  no default group-flag list (M8 §3.2): the project does not decide what a community finds
  unacceptable. Importable, shareable lists are the mechanism for groups that want to start from
  someone else's (M8 §3.2).
- Rules support literal terms, word-boundary matching and regular expressions, each with its own
  severity.
- **Matching normalises first, or it does not work.** Evasion is the default state of this problem:
  leetspeak (`1`/`l`, `3`/`e`), Unicode homoglyphs (Cyrillic `а` for Latin `a`), zero-width joiners,
  combining marks, fullwidth forms, and inserted spacing or punctuation. Normalise to NFKC, fold
  confusables, strip zero-width and combining characters, collapse separators — **then** match.
  A matcher without this catches only people not trying to evade it.

##### Action is the group's decision

A match raises a flag on the profile and a `Warning` notification (§4.5). Auto-action is **available
per rule, opt-in, and off by default.**

This differs deliberately from M8 §2's absolute prohibition, and the distinction is real: an M8
signal is *inherited or inferred* — another group's ban, or a model's judgement. A term match is a
**first-hand observation of text the user wrote about themselves**, verifiable by any moderator in
one click. That is direct evidence, and a group is entitled to act on it automatically if it chooses.

It is still off by default, because context defeats literal matching in both directions: quoting a
slur to condemn it, reclaimed language, and ordinary words that collide with an unfortunate acronym.
The UI shows **the matched text in context** so a moderator reviews the sentence, not the rule name.

##### It is a fact

Matches, dismissals and rule changes are all recorded (§5.9). Dismissal rates per rule are surfaced,
so a rule dismissed nine times out of ten is visibly noise rather than quietly ignored — the same
mechanism as M8 §4.4.

#### 4.2.7 Modbot Hub — term list distribution

> **Not built in M0.** The schema and the lists themselves exist
> (`src/Modbot.My/termlists/`); the download, update and toggle flow described here lands with the
> moderation work. Recorded now so the schema does not have to change later.

Curated term lists are published by **Modbot Hub**, served as static files by `Modbot.My` alongside
`my.modbot.co` (central services spec). They are **data, not policy** — Modbot ships none enabled.

##### Acquisition is opt-in, in two places

- **During onboarding**, as a skippable step: *"Select default term lists from Modbot Hub"*, with
  the picker suggesting lists by community type from each list's `suitableFor`. A group that skips
  it is not nagged.
- **From the settings page**, at any time afterwards. Most groups will arrive here rather than at
  onboarding, because you do not know what you want to screen for until you have been running a
  while.

##### Updates are checked on a schedule and **never applied silently**

Modbot checks periodically for new versions of the lists a group has imported, and surfaces them as
an `Info` notification (§4.5.1) — never an interrupt.

**An operator reviews a diff and accepts it explicitly.** This is the part that matters:

A term list update changes *what gets flagged in someone's community*. Auto-applying would mean the
Modbot project could silently alter moderation behaviour for every group that ever imported a list —
which is precisely the centralised power that central services §1.1 and M8 §5.1 exist to refuse. The
same reasoning gates client updates behind consent in M3 §9.2.

So the update flow shows **what changed**: rules added, removed, or re-scoped, with their `note`
text. A group can take some changes and not others, or stay on their current version indefinitely.

##### Rules toggle individually

Every rule carries an optional stable `id` (`_schema.json`), so a group can **disable one rule
without forking the list** and keep receiving updates for the rest. Disabled rules stay visible in
the UI with their `note`, because "we turned this off deliberately" is information a new moderator
needs — an invisible disabled rule looks like a missing one.

Per-rule dismissal rates (§4.2.6) feed this directly: a rule dismissed nine times in ten is the one
to turn off, and Modbot says so rather than leaving people to notice.

##### Local and shared lists

A group can write its own lists in the same schema, and **share them by instance URL** — another
deployment imports from `https://their-modbot/api/termlists/<id>`. Imported lists record their
`source`, and that attribution survives so an operator can always see where a rule came from.

There is no central registry of community lists, for the same reason there is no central peer
registry in M8 §5.1.

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
  and we cannot see it. **`users.read` is exempt from it** (§4.2.5): that endpoint is observed to be
  governed separately and far more permissively, and holding it to the group budget would cost days
  of sync time. The exemption is deliberate and evidence-based, and it is the assumption in §4.2 most
  worth re-testing — a 429 on `users.read` still applies the decrease to the global bucket, and 429s
  appearing on *other* classes shortly after user-sync bursts would be the signature that the
  exemption is wrong.
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

**The per-type pacing caps and the 2 req/s global ceiling in §4.2 are authoritative, and where this
table disagrees with §4.2, §4.2 wins.** It does disagree: this table's 0.29 req/s for bans and the
audit log predates §4.2's 0.5 and 0.125. Those older figures are kept for the record, not as
configuration. The table below records where the numbers came from -- the pacing the previous implementation actually ran in
production (`old/Modbot/Consumers/`), which is the best empirical evidence available -- and covers
endpoint classes §4.2 does not schedule:

| Endpoint class | Provisional rate | Source |
|---|---|---|
| `groups.members` | **0.67 req/s** | `MemberConsumer` — 1500 ms between pages |
| `groups.bans` | **0.29 req/s** | `BanConsumer` — 3500 ms between pages |
| `groups.auditlog` | **0.29 req/s** | `LogConsumer` — 3500 ms between pages |
| `groups.invites` | **0.29 req/s** | no prior data; matched to the conservative neighbour |
| `users.read` | **0.33 req/s** | `UserProducer` — 3000 ms |
| `users.profile` | **3.5 req/s** | the maintainer, 2026-09-15 — the same rate as `users.read`, its own budget |
| `users.groups` | **0.2 req/s** | **no data at all** — see §4.3.4.1 |
| `moderation.write` | **0.3 req/s** | unknown; interactive and low-volume, kept conservative |
| `auth` | negligible | login and re-login only; at most 4 counted requests an hour (§4.1.2) |
| `auth.verify` | **0.1 req/s** | **no data** — `GET /auth`, the session check; a guess to confirm (§4.1.2) |

##### 4.3.4.1 `users.groups` — an unmeasured endpoint, isolated rather than guessed at

`GET /users/{id}/groups` and `GET /users/{id}/groups/permissions` are used by exactly one thing:
picking the managed group during onboarding (§7.1 step 4). Nothing else calls them, and **no limit
has been measured for either.**

They exist as a pair because the obvious implementation is not viable at §4.2's pacing. "Groups
where this account holds moderator permissions" needs per-group permissions, and `/groups/{id}` is
one request per group at `groups.read`'s 0.2 req/s — an account in twenty groups would spend over a
minute and a half on a wizard step. `/users/{id}/groups/permissions` returns the whole map in one
call, so the step costs **two requests regardless of group count**.

The standing instruction above forbids inferring a limit from a neighbour, and the first
implementation did exactly that — it budgeted both under `users.read`, which is not merely a guess
but the *most permissive* one available, since §4.2.5 exempts that class from the global ceiling on
evidence these endpoints have none of.

**They now have their own class and their own lane**, which is the answer the per-endpoint model
(§4.3) is supposed to give when a limit is unknown:

- A 429 here cold-stops **only group selection**. Not profile fetches, not member sync, not the
  audit log. Isolation is what makes using an unmeasured endpoint acceptable at all — the blast
  radius is bounded to the one interactive step that needs it.
- The rate is `groups.read`'s 0.2 req/s: these return group data, so a group-shaped limit is the
  conservative reading. **The number is a deliberate underestimate, not a finding.**
- No global exemption. `users.read` has one on evidence (§4.2.5); this class has no evidence, so it
  gets no exemption.
- A burst of **2** — exactly the sequence it admits. Pacing a pair of one-off calls five seconds
  apart would stall a wizard step for nothing, the same reasoning the `auth` bucket already uses.

`BudgetCoverageTests` enforces the shape of this: every declared class has a budget, every budget
names a declared class, and this class specifically is asserted to be isolated from `users.read`,
no faster than `groups.read`, and not exempt from the global ceiling. If a real limit is ever
measured, change the rate here and the test goes on holding the isolation.

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

##### 4.3.4.2 `groups.auditlog.types` — withdrawn

> **Withdrawn 2026-09-13.** This section used to approve `GET /groups/{id}/auditLogTypes` for a
> daily "vocabulary check": ask VRChat which audit-log event types a group can contain, and flag any
> spelling Modbot's mapping table treats as real that VRChat does not declare. The endpoint class,
> its lane and budget, the check and its health-screen card have all been removed.

Two things were wrong with it.

**The endpoint does not answer the question.** It returns the event types the group has *seen in
roughly the last two weeks*, not the set it can emit. A group that has not banned anyone in fourteen
days would be told its ban mapping was "missing" — a defect warning on a healthy deployment — and a
check that cries wolf on healthy deployments is ignored within a week, which is how a real warning
gets missed.

**Live data showed it already wrong about the one thing it was built for.** VRChat's real ban event
is `group.user.ban`. The check's own table listed that spelling as a speculative *alias* — the row it
was designed to report as nothing — while the "primary" `group.member.user.ban` it would have raised
a defect over never appeared once. The classification the check depended on was upside down, and
the endpoint could not have told it so.

What replaces it is cheaper and honest: every event type VRChat is known to emit is mapped, an entry
whose type is not recognised is recorded under `modbot.unrecognised` with VRChat's own wording kept
(§5.3.1) and reported on the health screen, and the mapping table is checked against captured live
samples in the test suite rather than against a two-week window at runtime.

### 4.4 Time — one authority, never the local clock

Modbot has **one clock**: `IModbotClock`, served by the backend. Nothing anywhere — server, client,
overlay — reads `DateTime.Now` or `DateTimeOffset.UtcNow` directly for anything that becomes a fact,
a daily total boundary, or a rate-limit window.

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

### 4.4.1 Logging — Serilog, four tracks

Logging is **Serilog**, writing several tracks at once. Two are always on, two are opt-in.

**Console is its own profile.** File logging is **three separate streams**, each answering a
different question, and **each written in both JSONL and plain text**. Seq is optional on top.

| Profile | Level | Contents |
|---|---|---|
| **Console** | `Information` | What a human watching a terminal or `railway logs` needs. Concise, no noise. Its **shape** is `CONSOLE_LOG_MODE` — see below. |
| **Main** | `Information` | The application record — syncs, moderation, notifications, config changes. **Excludes HTTP.** |
| **Debug** | `Debug` | Everything, verbose, including HTTP. Off in production unless an operator turns it on. |
| **HTTP** | `Information` | VRChat and Discord API traffic **only**: method, endpoint class, status, duration, bucket state, retry-after. |
| Seq | `Debug` | Opt-in, structured browsing during development. |
| Log server | — | A future Modbot-native sink. Not built. |

Each file stream produces a matched pair:

```
logs/
  modbot_log_09-12-2026_1757707200.jsonl          ← queryable
  modbot_log_09-12-2026_1757707200.txt            ← readable
  modbot_log_debug_09-12-2026_1757707200.jsonl
  modbot_log_debug_09-12-2026_1757707200.txt
  modbot_log_http_09-12-2026_1757707200.jsonl
  modbot_log_http_09-12-2026_1757707200.txt
```

#### Why the console has three shapes

`CONSOLE_LOG_MODE` is `serilog` (the default, and what an unset or unrecognised value means), `json`
or `railway_json`. It changes the console and nothing else: the files and Seq carry the same events
at the same levels in every mode.

The console is the one sink whose reader is not known in advance. On a laptop it is a person, and
rendered text is the only shape worth printing. On Railway, Fly.io or Render it is a log explorer
that parses JSON into searchable attributes and prints everything else as one flat string — where
rendered text is actively worse, because it has already thrown away the properties the explorer
would have indexed. One deployment cannot be right for both, and the file streams cannot settle it:
on the platforms where structured console output matters most, the container's disk is discarded on
redeploy and the console **is** the log.

`railway_json` is Railway's documented shape rather than a general one: `message` and `level`, with
`level` reduced to the four words Railway matches (`debug`, `info`, `warn`, `error`), and every other
property as a top-level key so it becomes `@name:value` in the log explorer. Serilog's six levels do
not fit in four, so the exact level is also kept as `logLevel`. `json` is Serilog's own compact
format, for anything that already speaks it.

The same two variables — plus `LOG_LEVEL` — are read by every Modbot program, including the desktop
client, and the code that reads them lives in Modbot.Shared for that reason. Each event also carries
`Service` and `Version`, so one Seq server can hold every program at once and still tell them apart.

#### Why both formats of every stream

They serve different readers, and neither substitutes for the other.

**Text is what a self-hoster opens when something breaks**, at an hour when they are not going to
install `jq` or learn a query syntax. It has to be legible with `cat`, `tail -f`, or Notepad, and
pasteable into a GitHub issue.

**JSONL is what survives contact with a real question.** *"Every 429 on `groups.members` in the last
week, with the bucket state at the time"* is a one-line query against structured properties, and
completely unanswerable against a rendered sentence that flattened them into prose.

Pairing them per stream rather than picking one removes the choice between being readable now and
queryable later. The cost is disk, which §retention already bounds, and it is a good trade: the
moment you need the queryable version is usually the moment it is too late to start writing it.

The text rendering carries the same events and the same correlation ids as its JSONL twin — same
content, different presentation, never a different filter.

#### Why HTTP gets its own file

This is the one that earns its keep. §4.3 exists to reason about an opaque, punitive rate limiter,
and the questions that matter are things like *"every 429 on `groups.members` in the last week, with
the bucket state at the time"*, or *"did 429s on other endpoint classes follow user-sync bursts"* —
which is the specific signal §4.2.5 says would invalidate the `users.read` exemption.

Those are one-line `jq` queries against a file containing **nothing but API traffic**. Against a
combined log they mean grepping application noise at a ratio of hundreds to one, and against rendered
text they are unanswerable, because the interesting values were flattened into a sentence.

Main deliberately **excludes** HTTP so it stays readable for an operator diagnosing a sync problem.
Correlation ids (below) join the two files when you need both.

#### Why the filename carries a Unix epoch

`modbot_log_09-12-2026_1757707200.jsonl` — the date is for humans scanning a directory, the epoch is
the process start time.

It means **every run gets its own file**, so a restart is visible as a file boundary rather than a
seam in the middle of one. That matters directly for §4.3.2: rate-limit penalty state must survive
restarts, and a crash-loop is the failure mode being guarded against. Being able to see "this process
lived four seconds" by looking at filenames is how you spot one.

Streams roll by size within a run, and are pruned by count and age so a self-hosted appliance cannot
fill its own disk.

**Retention matters more with six files than it would with one.** The Debug stream in particular is
unbounded by design -- it is the one that records everything -- so its text twin is the largest
artefact Modbot produces. Defaults are conservative, per-stream, and configurable in `Settings`:
Debug is **off** unless an operator enables it, and when enabled it is retained for days rather than
weeks. Main and HTTP are retained longer because they are the ones worth going back to.

#### Routing

Events carry a `LogArea` property. The HTTP stream includes only `LogArea = Http`; Main excludes it;
Debug takes everything. One property, three filters — no separate logger instances to keep in sync.

#### Rules

- **Structured properties, never interpolation.** `Log.Information("Synced {Count} members for {Group}", n, id)` —
  not `$"Synced {n} members"`. The interpolated version is identical in the console and worthless in
  the JSONL, which defeats the entire point of having it.
- **Secrets never reach a sink.** VRChat credentials, auth cookies, device tokens, SMTP passwords,
  Discord tokens and proxy credentials are redacted at the logging boundary, not trusted to call
  sites. This pairs with §5.9.3's rule for the audit log, and the same reasoning applies: a log file
  is something operators are encouraged to read and paste into issues.
- **Correlation ids** on every request and every sync run, so one operation's lines can be pulled out
  of an interleaved file.
- **Seq is configured in `Settings`**, like everything else (§2.6) — absent config means the sink is
  simply not registered, never a broken logger or a stream of connection errors.
- Files roll daily with a retention limit, because a self-hosted appliance must not fill its own disk.

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

This is a **foundation**, not a feature, and it must exist in M0 because it is the only part of the
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
                       ├──▶ CURRENT STATE (GroupMember, GroupBan) — mutable
                       └──▶ DAILY TOTALS aggregates — always recomputable from facts
```

**Invariant: daily totals are recomputable from facts; facts are never mutated.** A bug in a metric is a
re-run, not lost data. A metric invented next year is filled in across all recorded history.

#### 5.2.1 The counted-only path — when *not* to write a fact

One deliberate exception. Some events are **high-cardinality and individually worthless**: nobody
will ever ask "who sent a Discord message at 14:32:07", only "how many did this person send this
week". Discord message volume is the canonical case, and a busy server produces thousands a day.

For these, Modbot **increments a daily total directly and writes no fact at all**:

```
  Discord message ──▶ DAILY TOTALS only (discord.messages, dimension = user)
                      no modbot_event row, no message content ever stored
```

Three reasons, and the third is the one that settles it:

1. **Volume** — a fact per message would dwarf every other source combined, for no query anyone runs.
2. **The invariant still holds** where it matters. These daily totals are not recomputable, which is a
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
  type             text         not null   -- vrchat.group.member.ban | vrchat.group.instance.kick |
                                           -- vrchat.instance.join | discord.voice.join | ... (§5.3.1)
  type_raw         text         null       -- the source's own wording when type is modbot.unrecognised
  subject_platform smallint     not null   -- VRChat | Discord
  subject_id       text         not null   -- usr_... or a Discord snowflake
  actor_platform   smallint     null
  actor_id         text         null       -- who caused it
  world_id         text         null       -- wrld_...
  instance_id      text         null       -- instance ids are unique per WORLD, not globally,
                                           -- so both columns are required to identify an instance
  source           smallint     not null   -- AuditLog | SyncDiff | Client | Discord | Manual
  data             jsonb        not null

  INDEX (subject_platform, subject_id, occurred_at DESC)
  INDEX (actor_platform, actor_id, occurred_at DESC)   -- moderator pattern detection, §5.8.5
  INDEX (type, occurred_at DESC)
  GIN   (data)
```

**Fact types from VRChat's group audit log** are all `vrchat.group.*` and all moderation retention:
membership (`member.join`, `member.leave`, `member.remove`, `member.ban`, `member.unban`), roles
(`role.assign`, `role.unassign`, `role.update`), invites and join requests (`invite.create`,
`request.create`, `request.reject`, `request.block`), posts (`post.create`, `post.delete`), group
instances (`instance.create`, `instance.close`, `instance.update`, `instance.announcement`,
`instance.kick`, `instance.warn`), calendar entries (`calendar-event.create`, `calendar-event.delete`,
`calendar-event.series.update`, `calendar-event.series.delete`) and `group.update`. The last is also
written by the group-info producer from its own polling; the two are told apart by `source`, and an
audit entry's `data` carries VRChat's `auditEntryId`, which is the key the producer deduplicates on.
The full entry is stored in `data` for every type; the scalars live data shows to be stable are
additionally lifted by type, every top-level `{old, new}` pair is lifted under `changed` for any type,
and instance events fill `world_id`/`instance_id` from the location string by its delimiters
(`.agent/research/vrchat-audit-log-findings.md` §6).

**`subject_platform` exists because Discord is a second fact source** (§9.1), not only a second
surface. A Discord snowflake and a VRChat `usr_…` must not collide in one text column, and once
accounts are linked (M5) a profile needs to query both sides of the same person. Keeping the
platform as its own column rather than namespacing the string (`dc:123…`) keeps the identifier
joinable against the user tables without parsing.

**`actor_id` gets its own index** because §5.8.5 asks actor-side questions — "everything this
moderator has done" — which the subject-side index cannot answer efficiently.

**Instances need both `world_id` and `instance_id`.** An instance id identifies one session of one
world and is unique only within that world. It is also **arbitrary user-controlled text** — usually a
VRChat-assigned number, but settable to any string through the API, which many groups do via VRCX.
Treat it as hostile input wherever it is displayed (M6 §4.1.1).

The **instance name** — a separate field VRChat added in mid-2026, returned in the instance JSON — is
**display only**: mutable, often absent, not unique. Nothing is ever keyed on it.

The raw location string is never stored. For non-group instances it carries `~nonce(…)`, the instance
secret, and Modbot must not persist instance secrets. Facts record the parsed components and the
non-secret qualifiers only. Grammar: `.agent/research/vrchat-log-format.md`.

**`occurred_before` and `source` are what make the analytics honest.** When the audit log reports a
ban at 14:32:07, that is exact. When a sync diff notices a member is gone, all that is actually known
is that they left *between the previous sync and this one* — a five-minute window. Collapsing both
into a single timestamp invents precision and produces fake spikes in hourly charts. Recording the
window instead lets daily totals distribute across the interval or exclude low-precision facts from
fine-grained series.

`source` also drives **deduplication**: the same ban arrives twice, once as a low-confidence
`SyncDiff` inference and once as an authoritative `AuditLog` fact. Same event, different confidence;
the authoritative one wins and the inference is superseded.

#### 5.3.1 Fact types are hierarchical strings, not an enum

> **Revised 2026-09-13.** `type` was a `smallint` enum with a "never renumber" rule. It is now a
> `platform.domain.action` string — `vrchat.group.member.ban`, `modbot.evidence.access` — stored
> as text, with a `type_raw` column beside it.

The enum's storage saving was real and irrelevant: about 20% on a fact measured at 326 bytes, which
is 240 MB a year becoming 290 MB for a typical group. What it cost was not.

VRChat's audit log types its events as free-form strings. Modbot translated them into numbers, and
an event with no number was **counted and not recorded** — the cursor moved on, and because VRChat's
own log ages out, those entries were gone for good. That is §5.1's failure exactly: history cannot
be filled in later, and here Modbot was the one discarding it. Around that sat the ordinary friction —
every new producer had to append an enum member before it could record anything, evidence access
had no member and so was never recorded at all, and a runtime vocabulary check (§4.3.4.2, since
withdrawn) existed only to catch a mapping whose spelling is wrong.

Now:

- **An event Modbot has no name for is recorded** under `modbot.unrecognised`, with the upstream
  system's own wording preserved in `type_raw`. It can be understood later; it can never be fetched
  again.
- **Retention is a prefix test.** `vrchat.instance.*`, `vrchat.avatar.*`, `discord.voice.*` and
  Modbot's operational prefixes are the presence class; everything else — including anything
  unrecognised — is kept forever. The default is the safe direction and it is load-bearing.
- **Well-formedness is checked once, at the writer**, for shape and never against a list. A type
  nobody has seen is legitimate; `"Banned"` is a bug in a producer.
- **Renaming a type is a data migration**, exactly as renumbering was. Adding one is not.

The constants live in `FactType` as `const string`, so `FactType.MemberBanned` reads as it always
did and still works in a `switch`. That is why the move cost one rewritten method rather than three
hundred edits.

### 5.4 Daily totals

```sql
modbot_daily_total
  day        date      not null
  metric     text      not null   -- 'members.total', 'members.joined', 'bans.added',
                                  -- 'moderator.actions', 'instance.minutes', ...
  dimension  text      not null   -- '' when the metric has no dimension (see below)
  value      numeric   not null
  origin     smallint  not null   -- Computed | Counted (§5.2.1)
  PRIMARY KEY (day, metric, dimension)
```

**`dimension` is `NOT NULL` with an empty-string stand-in**, not nullable. An earlier draft of this
section wrote `dimension text null` inside the primary key, which **PostgreSQL rejects** — no primary
key column may be nullable. The store marker is the standard workaround and it has a second benefit:
`ON CONFLICT (day, metric, dimension)` works, where a nullable column would make every undimensioned
row conflict with nothing (`NULL != NULL`) and silently accumulate duplicates.

**`value` is `numeric`, not an integer**, because imprecise facts are apportioned fractionally across
days (§5.4.1).

A generic metric/dimension shape so new metrics require no migration. Recomputed by `DailyTotalsJob`;
a full rebuild from facts is always available and is the supported fix for any aggregation bug.

### 5.5 Retention — off by default, and the numbers say why

> **Revised 2026-09-12.** An earlier draft pruned presence facts after 90 days. That contradicted
> §5.1, which argues history cannot be filled in later and is the reason the fact log exists at all.
> **Modbot now deletes nothing by default.**

#### Measured, not estimated

One fact costs **326 bytes** including all four indexes — measured by inserting 200,000
representative rows into the real schema, not guessed from column widths.

| Group | Facts/day | Per year | At ~$0.25/GB/month |
|---|---|---|---|
| Typical — 100–10,000 members, 1–2 instances | ~2,000 | **238 MB** | ~$0.06/mo |
| Busy — 16,000 members, 3–5 instances | ~20,000 | **2.4 GB** | ~$0.60/mo |
| Extreme — 150,000 members, 30 instances | ~432,000 | **51 GB** | ~$13/mo |

Most groups sit in the first row. Deleting their history saves pennies and costs them the ability to
answer *"who are our regulars"* about anyone who joined more than three months ago — which is most of
the value §5.10 exists to deliver.

The third row is real money, and that group should be able to set a window. **That is what makes this
a setting rather than a policy.**

#### There is no default retention window

`ModerationFactRetentionDays` and `PresenceFactRetentionDays` both default to **0 — keep forever**.
Daily totals are kept forever regardless.

The pruning machinery still exists and is tested; it simply does not run unless an operator turns it
on. That ordering matters: a tool that quietly deletes data and lets you opt out is very different
from one that keeps everything and lets you opt in, even when both end up configured identically.

#### The settings page shows what it actually costs

An operator asked to choose a retention window with no idea what they are trading cannot make that
decision, so Modbot measures and shows:

- **Current usage** — real bytes, from `pg_total_relation_size` over the fact tables and their
  indexes. Not a row count multiplied by a constant.
- **Observed growth rate** — facts per day over recent history, from the fact log itself.
- **Estimate** at 6, 12 and 24 months at the current rate, stated as an extrapolation rather than a
  promise. Growth is not linear: a group that opens more instances generates more facts per member.

  > **Revised 2026-09-14.** The three fixed points and their table are gone. The chart now shows
  > the past year of recorded sizes, today in the middle, and a year of estimate to the right,
  > drawn from a growth rate the server sends. PostgreSQL keeps no record of past sizes, so
  > Modbot records one row per day in `modbot_storage_day`. History starts at the first recorded
  > day and cannot be filled in. The per-GB cost is now applied in the browser rather than sent
  > to the server.
- **What it means in their terms**, from one of two inputs the operator provides:
  - a **per-GB monthly cost**, for hosted deployments → projected monthly spend
  - a **disk capacity**, for home hosting → the date they would run out

"Keep everything: about 240 MB a year, roughly six cents a month" is a decision someone can make.
"Choose a retention period" is not.

#### Running fresh over everything

With nothing pruned, daily totals and subject profiles (§5.10) recompute across the **entire** history, and
§5.10.2's caveat about being bounded by surviving facts applies only where an operator has configured
a window.

#### Privacy

Presence data — who was where, for how long, wearing what — is personal information, even though it is
all data the group could already observe directly. The position is unchanged by keeping it longer: it
lives on the group's own server, retention is configurable, and **purge-user works regardless of
retention settings**. A person asking to be erased is not asking about disk space.

##### Pruning: drop partitions, and the case the simple rule misses

Pruning is a **`DROP TABLE` on a whole partition**, never a mass `DELETE` — avoiding the dead tuples
and the vacuum cost is most of why §5.7.2 partitions at all.

The obvious rule — *"drop a partition once everything in it is past retention"* — is correct and, on
its own, **unsatisfiable whenever the two classes are configured differently**. A month's partition
mixes both, so if moderation is left at forever and only presence has a window, no partition is ever
fully expired and the presence window quietly becomes a comment that never runs.

So there are three cases per partition:

| Situation | Action |
|---|---|
| Every class in it has expired | `DROP TABLE` |
| Presence expired, moderation retained, expired rows actually present | **Move out** (below) |
| Nothing expired yet | Leave alone |

**Moving out** is one transaction: detach the partition, attach an empty replacement, copy the
surviving classes back, drop the old table. The volume still dies by `DROP TABLE`; what gets copied
is the low-volume class the spec sizes at hundreds per day (§5.5), and it happens once per partition
per boundary rather than continuously.

A partition is only ever acted on **whole** — never when a retention cutoff falls inside it — and
only when its name matches the one the partition maintainer generates. A hand-attached partition is
left alone.

The alternative is sub-partitioning by retention class, which makes every drop trivial. It is
rejected for M0: it needs the class in the partition key, a rewrite of the fact table, and a default
sub-partition whose failure mode is **silent ingest loss** — the worst shape of bug in this system.

A **purge-user** action erases every fact for one user on request.

Presence data — who was where, for how long, wearing what — is genuinely personal information, even
though it is all data the group could already observe directly. The defensible position is: it lives
on the group's own server, retention is configurable, and deletion works. This is documented in one
paragraph in `docs/`, stated plainly and without editorialising.

### 5.6 Surfaces in this spec

| Surface | In M2.5 | Description |
|---|---|---|
| **Profile** | yes | One user, everything Modbot knows: join date, roles, **full moderation history** (kicks, warns, bans, by whom, with classifications), every audit-log mention, and (from M3) sessions, time spent, avatar history. "Who is this person" is the question staff ask most often. |
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

Sustained peak is therefore ~18k facts/hour ≈ 430k/day. Since nothing is pruned by default (§5.5),
that accumulates: ~158M rows and ~51 GB after a year, at a measured 326 bytes per fact including
indexes. Comfortable for partitioned Postgres, and the one profile where an operator may genuinely
want a window — which is why §5.5 shows them the number rather than choosing for them. Typical
groups (100–10,000 members, one or two instances) sit three orders of magnitude below this, at a
few hundred megabytes a year.

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

Surfaced two ways: passively in the profile (§5.6), and **proactively at the moment of action** —
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

### 5.9 Modbot's own audit log — one table, merged in the UI

Modbot records what happens *inside Modbot* — logins, settings changes, warns, tickets, sync
failures — into **the same `modbot_event` table** as everything ingested from VRChat, discriminated
by `source`.

There is no second audit system. The fact log already is the audit log.

| | `source` | `subject_platform` | `actor_platform` |
|---|---|---|---|
| VRChat banned someone | `AuditLog` | `VRChat` | `VRChat` |
| Modbot banned someone | `Modbot` | `VRChat` | **`Modbot`** |
| Alice changed a setting | `Modbot` | `Modbot` | `Modbot` |
| Discord role granted | `Discord` | `Discord` | `Discord` |

`Modbot` is added as a third member of both the `source` and the platform enums (§5.3).

#### 5.9.1 Why merging matters more than it looks

VRChat's audit log attributes **everything Modbot does to Modbot's single VRChat account** (§2.3).
When Alice bans someone through Modbot, VRChat records "ModbotBot banned user X" — and per-moderator
attribution, the thing §5.8 accountability is entirely built on, is *destroyed at the VRChat
boundary*.

Modbot's own entry is the only place that attribution exists.

So the two entries are not redundant. One records **what VRChat believes happened**; the other
records **who actually did it**. A merged timeline puts them adjacent, and where Modbot can correlate
a VRChat audit entry with its own originating action it **enriches** the VRChat entry with the real
actor — turning "ModbotBot banned X" into "ModbotBot banned X *(via Modbot — Alice)*".

Without the merge, a group using Modbot for moderation would have *worse* attribution than one
clicking buttons in VRChat directly, which would be an absurd outcome for a moderation tool.

#### 5.9.2 What gets recorded

| Category | Examples | Retention class |
|---|---|---|
| **Auth** | Login, failed login, password change, API key created/revoked, device token paired/revoked (M3) | Moderation |
| **Config** | Settings changed, sync rates adjusted, retention changed, classification enum edited | Moderation |
| **Moderation (Modbot-side)** | Warn issued, note added, watch set, ticket opened/resolved (M4) | Moderation |
| **System** | Sync failure, rate-limit cold stop, WAF block, migration applied, retention pruned, partition created | **Presence** — operational noise, not history |
| **Group (from VRChat's audit log)** | Membership, bans, roles, invites and join requests, posts, group instances including instance kicks and warns, calendar entries, group settings (§5.3) | Moderation |
| **Federation** | Peer added/removed, signal published or received (M8) | Moderation |

System events take the short retention class deliberately. "A sync failed last March" is not history
anyone needs, and letting operational noise accumulate forever alongside moderation records would
bury the latter.

#### 5.9.3 Secrets are never the payload

A config-change event records **which setting changed and by whom** — and for secret-bearing fields,
nothing else. No old value, no new value, not even a masked one.

An audit log that records "SMTP password changed from ●●●● to ●●●●" is harmless; one that helpfully
stores both is a credential history in a table people are encouraged to read. Non-secret settings may
record before/after, because seeing that someone dropped the audit-log sync rate to its minimum right
before an incident is exactly what an audit log is for.

#### 5.9.4 Access is separately gated

`ViewAuditLog` and `ViewOperationalLog` are distinct permissions. A moderator who should see bans and
kicks does not automatically need to see that the owner reconfigured SMTP or which API keys exist.

#### 5.9.5 In the UI

One timeline, with **source filter chips** (VRChat · Modbot · Discord · Client) on by default, so the
merged view is what you get without asking. Each entry is visually attributed to its source, and
filtering to a single source gives you the old separate-logs view whenever that is what you want.

The merged default is the right one because the questions people actually ask span sources: *"who
changed the ban threshold just before these bans?"* is unanswerable in either log alone.

### 5.10 Derived profiles — the questions people actually ask

The questions a community manager asks are not the metrics a time series produces:

> *"What worlds get the most users?"*
> *"Is TrinityMaster58 a regular?"*
> *"Where does TrinityMaster58 usually turn up?"*
> *"What time does TrinityMaster58 join events?"*
> *"When do people actually come to our events?"*

Only the first is a daily time series. The rest are **three different analytical shapes**, and §5.4
supports one of them.

| Shape | Question | Supported by |
|---|---|---|
| **Time series** — a value per day | "members over time", "what worlds get the most users" | `modbot_daily_total` ✅ |
| **Cyclic** — a distribution over hour-of-week | "when do people come to events" | nothing yet |
| **Subject profile** — one person's habits | "is X a regular", "where does X go" | nothing yet |

A daily total cannot answer a cyclic question. *"Tuesdays at 8pm are our busiest hour"* is not
derivable from a series of daily totals — the information was thrown away when the day was summed.

#### 5.10.1 `modbot_subject_profile`

One row per subject, **entirely derived from facts** and recomputed on a schedule, holding:

| | |
|---|---|
| `first_seen`, `last_seen` | recency |
| `distinct_days_30d`, `distinct_days_90d` | the basis of "regular" |
| `minutes_30d`, `sessions_30d` | depth of engagement |
| `top_world_id`, `top_world_share` | "where do they usually turn up" |
| `activity_hour_of_week` | a 168-bucket histogram, `jsonb` |
| `regularity` | a computed classification (§5.10.3) |

The hour-of-week histogram is `jsonb` rather than 168 rows per person, because it is always read
whole and never queried by bucket. A group-level row under a reserved subject id carries the same
histogram for *"when do people come to our events"* — same shape, same code, one aggregate instead of
one person.

#### 5.10.2 Profiles are a cache, never a source

Everything here is recomputable from the fact log, which keeps §5.2's invariant intact: a bug in a
profile is a re-run. Nothing is ever written to a profile that was not derived from a fact, and no
query answers from a profile when the fact log disagrees.

**By default there is no constraint**: nothing is pruned (§5.5), so a profile is rebuilt across the
entire history every time, which is the point of keeping it.

Where an operator *has* configured a window, the rebuild is bounded by the facts that survive it.
Long-range figures then come from **daily totals**, which are never aged out — and a profile that claimed
a two-year history when only ninety days of facts survived would be lying. The rebuild is bounded by
what remains, and the UI shows the window it actually had.

#### 5.10.3 "Regular" is a definition, and the group owns it

Modbot must not silently decide what a regular is. A group running nightly events and one running a
monthly meetup mean entirely different things by the word, and a hardcoded threshold would be wrong
for one of them while looking authoritative to both.

So `regularity` is computed from a **stated, configurable rule** — by default something like *seen on
8 or more distinct days in the last 30* — and **the UI shows the rule next to the answer.** "Regular
(14 of the last 30 days)" is a claim a moderator can check; "Regular" alone is a claim they have to
trust.

The classes are deliberately few: **regular, occasional, new, lapsed**. More gradations invite
arguments about boundaries without improving any decision.

#### 5.10.4 What this unlocks

Subject profiles are the foundation M7's segments and giveaways run on. *"Members with more than ten
hours in our worlds this month and no bans"* is a query over profiles, not a scan of millions of
presence facts — which is the difference between a segment that returns in a second and one that
times out.

It is also the answer to the question this whole section exists for: a small community that cannot
afford an analyst can still find out when to run its events and who its regulars are, from data it
already generates.

---

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
| `modbot_daily_total` | Aggregates (§5.4). |

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

Still true after the evidence storage design. Railway's injected bucket variables (`BUCKET`,
`ACCESS_KEY_ID`, `SECRET_ACCESS_KEY`, `REGION`, `ENDPOINT`) are read **once, at first boot, purely to
pre-fill a wizard step** and never again — they configure nothing, and none of them is required. See
`.agent/specs/2026-09-13-evidence-storage-design.md` §16.1 for why re-reading them later would be a
data-loss bug rather than a convenience.

### 8.2 Migrations

EF Core migrations applied automatically at startup, as in the old implementation. A self-hosted
appliance must not require the operator to run a migration command.

### 8.3 Secret storage — settled

Secrets in `Settings` (VRChat password and 2FA secret, Discord bot token, SMTP password, proxy
credentials) are encrypted at rest with a key generated on first boot and **stored in the database**.

No environment variable, no volume, no key-management step. This deliberately protects against
casual reading of a database dump and **nothing more** — an attacker with full database access has
the key too.

> **Narrowed 2026-09-13.** "No volume" is a claim about **where the key lives**, not a rule that
> Modbot may never use persistent storage. The evidence storage design
> (`.agent/specs/2026-09-13-evidence-storage-design.md` §3) introduces a deployment profile that does
> mount one, for moderation evidence and logs. The key is unaffected: it remains a row in the
> database in every profile, because a volume is no safer a home for it and reintroduces exactly the
> thing-to-lose failure this section rejects. What changes is only that **volume-lessness is a
> property of the recommended configuration rather than of Modbot itself.**

That is the correct trade for this project. Modbot is run by community groups on managed hosting,
where in practice nobody touches the database directly, and where Railway already stores environment
variables in plaintext and displays them in its dashboard — so a key held there would not be
meaningfully safer. What a required key *would* reliably do is permanently brick installs when
someone redeploys and loses it.

The threat model is stated in one honest paragraph in `.agent/docs/security.md`. Operators who need real
encryption at rest should encrypt the database itself, which is where that control belongs.

---

## 9. Discord bot (basic)

In scope for this spec:

- **Audit log → Discord channels.** Rich embeds per event type, routable to multiple channels with
  per-type filtering.
- **Lookup commands** — `/user`, `/member`, returning profile summaries.

Deferred to M5: ban synchronisation in both directions, role synchronisation, VRChat↔Discord account
linking and auto-invite.

Runs as an `IHostedService` inside `Modbot.Server`. If no bot token is configured, it does not start
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
| **Messages sent** | **daily total only** (§5.2.1) | High volume, individually worthless, and content is never stored |
| Current member count, online count | daily total snapshot | A gauge, not an event |

Facts carry `subject_platform = Discord` (§5.3). Once accounts are linked (M5), a profile answers
"this person" across both platforms rather than "this VRChat account" and "this Discord account"
separately — which is what makes a linked account worth having.

**Voice presence is treated exactly like instance presence**: same session model, same time-spent
daily totals, same retention class and same purge-user coverage (§5.5). A community running events in
Discord voice rather than in-world gets the same regulars detection and the same giveaway
eligibility as one running instances.

---

## 10. Web UI

React + TypeScript + Vite, built into `Modbot.Server/wwwroot` and served by Kestrel. One container,
no separate frontend deployment.

Screens in this spec: setup wizard, login, members, bans, invites, audit log viewer, the analytics
sections below, settings — and the **subject popup** (§10.2), which is not a screen.

### 10.1 Analytics is five sections, not one dashboard

A single "metrics" page collapses five unrelated questions into one scroll. They are separated
because they are asked by different people at different times:

| Section | Answers | Built from |
|---|---|---|
| **My Group** | *Is the community growing or shrinking, and what changed?* Member count over time, join and leave rates, net growth, role distribution, tenure spread, invite acceptance. | Member/ban/invite facts (M1), daily totals |
| **My Team** | *Who is doing the moderation work, and when is nobody covering?* Actions per moderator, breakdown by classification, activity over time, and the §5.8 accountability signals. | Audit log + Modbot-side facts (M2) |
| **Worlds** | *Which of our worlds actually get used?* Time spent per world, unique visitors, popularity over time. | Presence facts (M3) |
| **Instances** | *When is the community actually active?* Instances opened and closed, concurrent count, peak population, duration, a time-of-day heatmap. | Instance facts (M3, M6) |
| **Tracked Groups** | *How are we doing compared to groups like ours?* — **later feature, §10.3** | Public group info, polled |

**My Team has one metric worth calling out: coverage gaps.** Cross-referencing moderator presence
against instance activity answers *"when was the community busy with no moderator present"*, which is
a scheduling question no group can currently answer and which falls straight out of data Modbot
already holds. It is also the one analytics figure that suggests an action rather than describing a
state.

### 10.2 The subject popup — a component, not a page

**Clicking a person, a world or an instance anywhere opens a popup over the current view.** From the
audit log, the member list, the ban list, the Worlds and Instances pages, another popup — the same
popup for the same kind of thing.

**This replaces the standalone "profile" page** the milestone table previously listed, and, since
2026-09-14, the right-hand pane this section first described.

#### Why over the page and not instead of it

Moderation is interruption-driven. A moderator scanning an audit log for one thing notices a name and
wants to know about it *without losing the scan*. A separate page means navigating away, losing
scroll position and filters, and navigating back — so in practice people don't check, and the
information Modbot spent §5.8 collecting goes unread at the moment it would have mattered.

**That reason is unchanged, and the popup keeps it.** The page behind stays mounted — its scroll
position, its filters and its loaded rows — and closing the popup puts the moderator exactly back
where they were.

#### What changed from the pane, and why

- **A centre popup rather than a right-hand pane.** Every kind now lays out the same way: who or what
  it is on the left, tabs on the right. A narrow pane had room for one column, and the person's
  history, case files and presence figures were one long scroll.
- **Three kinds rather than one.** A person, a world and an instance, because each names the others:
  an instance has a world and people, a world has instances, a person was seen in instances. When
  only a person could be opened, every world and instance id on screen was a dead end that merely
  displayed an id.
- **Popups stack.** A world opened from inside an instance popup opens *on top of* it, and closing it
  returns to the instance. Only the top popup is drawn — a second dimmed layer under the first is
  unreadable and makes Escape ambiguous — and the one underneath is named in a "Back to the …"
  control in the header.

| Kind | Left | Tabs |
|---|---|---|
| Person | Their stored VRChat profile; membership and roles | **Logs** (history counts and every fact about them), **Cases**, **Metrics** (time seen, instances and worlds visited, arrivals, last seen) |
| World | Name, author, picture, capacity, who can find it, when first seen | **Instances** (newest first), **Metrics** (time seen, visitors, instances opened per day) |
| Instance | World (a link), instance number, who can join, region, opened and closed, people now, most at once, how long it ran | **People** (who a moderator's client saw there), **Logs** (facts recorded there while it was open) |

Everything in all three comes from Modbot's own tables. Opening a popup asks VRChat for nothing, so
it costs no API budget however often a moderator does it (§4.3.4). An instance is opened by Modbot's
own id for it, never VRChat's number, which VRChat hands out again once a room closes.

#### The link carries the whole stack

`?subject=` repeated, in the order things were opened:

- `?subject=usr_…` — one person. **The link this section always specified**, so every link already
  pasted somewhere opens the same thing.
- `?subject=world:wrld_…` — one world.
- `?subject=usr_…&subject=instance:<id>` — a person, then an instance opened from inside it.

A repeated parameter rather than one separated list, because each value is encoded on its own and a
separator could not be told apart from the same character inside an id — VRChat ids are arbitrary
text (§3.1.1). The `world:` and `instance:` prefixes are Modbot's own labels on a value Modbot wrote,
not a reading of the id's shape; anything unprefixed is a person.

Opening pushes a browser history entry. So **Escape, the close button, the back control and the
browser's back button all do the same thing: close one popup.** A stack somebody was sent as a link
has no history of ours behind it, so closing there rewrites the address instead — otherwise the first
Escape would leave Modbot.

#### Consequences that still follow

- **The popup is deep-linkable**, so it survives a refresh and can be pasted to another moderator. A
  popup that cannot be linked is a popup nobody shares.
- **Every list that renders a person, a world or an instance is a launcher.** One component per kind,
  one fetch shape, one place to add anything new.
- **Repeat-offender context (§5.8.4) lives on the person's Logs tab**, which is what makes it visible
  at the moment of action rather than only on a page someone has to think to visit.
- It must open **fast**, because it is opened speculatively. Identity first, tabs as they arrive.

#### Facts read as sentences, written when the page is drawn

A log row that reads `vrchat.group.instance.kick`, followed by two ids, is a row a volunteer cannot
use. Every fact type has a sentence — *Mira kicked Ada out of instance 39047 in The Black Cat* — and
every name in it opens its own popup. What a fact's subject *is* (a person, an instance, the group, a
role, a Modbot account) comes from the fact's type, never from the shape of its id.

Names are looked up against `vrchat_user` and `vrchat_world` **once per page of rows, never once per
row**, and the instance a fact happened in is matched by its time falling inside an instance's own
open and close times. The name recorded at the time stays in the payload beside the name as it is now.

**The sentence is built when the page is drawn and never stored.** Facts are never mutated (§5.2),
and nothing needs rewriting anyway: the audit-log producer keeps VRChat's whole entry in the payload,
and an event Modbot has no name for is stored as `modbot.unrecognised` with VRChat's own word in
`type_raw`. Sentences are looked up by `type_raw` as well as by `type`, so a row recorded before
Modbot understood an event reads correctly the moment a sentence for it is written. **That is the
catch-up, and there is no job:** improving the wording improves all of recorded history the next
time somebody opens the page. Until then, the row says *"Modbot doesn't recognise this event yet"*
and shows VRChat's `eventType` and the payload underneath, so the moderator can still read what
happened.

Two limits, stated rather than papered over:

- **A sentence can only say what the stored event captured.** Where a producer did not keep a field,
  old rows stay thinner however good the wording gets — VRChat's audit log ages out and the entry
  cannot be fetched again. Fixing a producer to keep more helps only facts recorded afterwards.
- **Charts do not catch up this way.** If a newly understood event type should count towards a daily
  total, that needs the existing recompute from facts (§5.2); drawing a sentence changes no number.

### 10.3 Tracked Groups — recorded, not built

Track other **public** groups by id and chart their member counts alongside your own, so a group can
tell "we lost 200 members" from "everyone lost members this week".

Deferred, and noted now so the daily total shape accommodates it: it needs `groups.read` budget on a
schedule (§4.2), and the tracked group's id as a **daily total dimension** rather than a new table.

Only ever public, already-visible information — member counts and group metadata. No member lists,
no moderation data, nothing that is not on the group's own public page. This is comparison, not
surveillance of another community.

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
| Sync jobs | Fed recorded VRChat payloads; assert both the current-state tables **and** emitted facts, including `occurred_before` windows on inferred events. |
| **Ingest deduplication** | Replay the same instance event as reported by six independent clients with jittered timestamps; assert **exactly one** fact is written and time-spent totals match a single-client replay. Must include the **boundary case** that killed the bucketed design (reports at `14:00:04.9` / `14:00:05.1`) and the **floor case** (a genuine rejoin 15 s later yields *two* facts, not one). |
| **Time provider** (§4.4) | SNTP offset estimation under asymmetric latency; outlier round-trips discarded; a client whose clock is minutes off still produces correctly-ordered facts; a client `occurred_at` beyond plausible transport delay is clamped **and flagged**, not silently accepted or dropped. |
| Analytics | Property test the core invariant: daily totals recomputed from facts equal daily totals built incrementally. |
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
| **M0** | Foundation: repo, licensing, CI, `IVRChatGate`, `IModbotClock`, auth, onboarding, **fact log + daily totals + retention** | this |
| **M1** | Member/ban/invite sync, search UI, **facts emitted on diff** | this |
| **M2** | Audit log ingestion, parsing, viewer — **authoritative facts, dedups M1's inferences** | this |
| **M2.5** | Subject pane (§10.2), the My Group and My Team analytics sections (§10.1), repeat-offender + moderator pattern detection (§5.8) | this |
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
be filled in later.**

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
- The M0 foundation the client depends on — the fact log, `IModbotClock`, the deduplication window —
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
7. **Nothing is deleted by default** (§5.5) — both fact classes and daily totals are kept forever, with
   retention configurable per class. History cannot be filled in later, and at a measured 326 bytes per
   fact a typical group costs pennies a year to keep whole. The settings page projects that cost
   from measured usage so the operator decides against numbers rather than against a guess.
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
    becomes a fact, a daily total boundary, or a rate-limit window. Clients synchronise to server time
    (SNTP round-trip estimate) *and* the server independently records `observed_at` and clamps
    implausible client timestamps — corrected and tolerated, not one or the other.
15. **Rate limits are provisional and endpoint-specific** (§4.3.4). Assume 0.3–1 req/s per endpoint
    class, seeded from the previous implementation's production pacing. **Implementers must ask
    before building against a new endpoint** rather than inferring a limit from a neighbour.
16. **Versioning is calendar-based** `YYYY.M.PATCH`, patch resetting monthly (§2.7.1), and the
    **API version is a separate integer** bumped only on breaking change (§2.7.3). Comparison is
    componentwise and numeric — unpadded components make lexical sorting wrong. MSI `ProductVersion`
    cannot hold a 4-digit year (major ≤ 255), so it uses `(year-2000).month.patch` (§2.7.2); a wrong
    mapping makes upgrades silently refuse to apply. A day-plus-letter scheme was considered and
    rejected mainly because it forced packing arithmetic into that mapping.
17. **Upgrade risk is release metadata, not inferred from the version** (§2.7.5): `breaking`,
    `requiresMigration`, `minimumUpgradeFrom`, `upgradeNotesUrl`. `minimumUpgradeFrom` is enforced
    **locally at startup** against the version recorded in the database, so air-gapped and
    update-check-disabled deployments get the same protection. It exists for squashed migrations,
    where an ancient install otherwise fails partway into a state nobody designed. Update checking is
    on by default, disableable, and transmits nothing about the deployment.
18. **Vertical slice architecture, not layered MVC** (§2.8). One folder per feature. Chosen because
    agents write most of this code and a slice is one unit of context; parallel work does not collide
    in shared layer directories; review is a folder diff. VSA's duplication failure mode is headed
    off by the §4 abstractions already being the shared kernel. Minimal APIs, and **no MediatR** —
    little benefit at one handler per slice, harder call paths for humans and agents alike, and its
    current versions are commercially licensed.
19. **Sync pacing is fixed per type with a 2 req/s global ceiling** (§4.2), configurable downward
    only, with the cap enforced server-side. Room left below the ceiling is reserved for interactive
    work, not spent on faster sync. **`users.read` is the one exemption** — its own 1 req/s lane
    outside the ceiling (§4.2.5), because VRChat governs that endpoint separately and laxly. It turns
    a 150k-member profile sweep from ~8.7 days into ~1.7 days, and a typical group from ~11 hours
    into ~2.2. Peak total outbound is therefore ~3 req/s: a conscious trade, recorded alongside the
    signals that would invalidate it.
20. **The gate owns authentication; no SDK convenience helpers** (4.1.1). `LoginAsync` and
    `TryLoginAsync` exist to get a script running quickly and discard the HTTP status, collapsing
    401, 429, 403 and a WAF block into one bare `null` -- when 4.1 requires a different response to
    each. Modbot drives `GetCurrentUserWithHttpInfoAsync` and `Verify2FAWithHttpInfoAsync` itself,
    and calls `...WithHttpInfoAsync` everywhere. A plain convenience-overload call anywhere in the
    codebase is a review failure.
21. **Scheduling is never wall-clock aligned** (§4.2.2). Fixed-clock scheduling would make every
    Modbot instance worldwide hit VRChat on the same second -- synchronised spikes that are worse
    for VRChat than the same volume spread out, and traffic indistinguishable from a coordinated
    botnet. Offsets are per-process, per-type, regenerated at start, with per-tick jitter, measured
    from a monotonic source.
22. **Moderation friction scales with reversibility** (§5.8.1). Kicks and warns take an *optional*
    one-tap classification; only bans require a report. Requiring a form per kick does not produce
    better records, it produces moderators who abandon the tool or type "troll" five hundred times.
    Togglable to required in settings; default optional.
23. **Classification is a one-tap enum, never a text box** (§5.8.2) — and it is the *signal* that
    makes moderator pattern detection possible rather than an accusation generator, not paperwork.
24. **Accountability surfaces patterns for review; it never accuses or auto-punishes** (§5.8.5).
    Wrongly flagging a volunteer handling a persistent troll is corrosive in a way a missed
    detection is not, so the thresholds are asymmetric too. Good classification behaviour
    auto-resolves tickets, which is the incentive that makes the optional field actually get used.
25. **`subject_platform` / `actor_platform` columns** (§5.3), because Discord is a second fact
    source (§9.1) and a snowflake must not collide with a `usr_…` in one text column. `actor_id`
    gets its own index for §5.8.5's actor-side queries.
26. **A counted-only path exists for high-cardinality events** (§5.2.1). Discord message volume
    increments a daily total and writes no fact — too voluminous, individually worthless, and counting
    means there is no social graph to leak. The test: would anyone ever query an individual one?
27. **Deduplication is windowed (±5 s), not bucketed** (§5.7.1). A `floor(t / bucket)` hash fails
    silently at bucket boundaries. The window is bounded below by a genuine 15-second leave-and-
    rejoin and is only narrow enough to fit because of §4.4 — server-time sync is a correctness
    prerequisite for deduplication, not an optimisation.
