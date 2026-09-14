# Modbot Central Services — `my.modbot.co` and the Release Host

- **Date:** 2026-09-11
- **Status:** Draft, awaiting review
- **Covers:** the two services the Modbot project itself operates — the instance selector and the client release feed
- **Depends on:** nothing. **Depended on by:** M3 (client updates) — the release host blocks M3 shipping
- **Related:** foundation §1 (`my.modbot.co`), M3 §9 (updates)

---

## 1. The governing rule

> **Modbot must be completely functional with zero contact to any project-operated service, forever.**

A self-hostable tool that quietly depends on infrastructure the project runs is not self-hostable; it
is a hosted product with extra steps. The moment a central service becomes load-bearing, the project
acquires the power to break every deployment by shutting it off, losing a domain, or losing interest.

So both services here are **conveniences with working manual alternatives**, and that is a permanent
constraint rather than a current state:

| Service | What it does | If it vanishes |
|---|---|---|
| `my.modbot.co` | Remembers which Modbot instances you use, and switches between them | Bookmark your instance's URL. Nothing is lost but convenience. |
| Release host | Serves signed client updates | Download releases from GitHub and install manually, or point the client at a mirror. |

Both are in the main repository and both are self-hostable by anyone who wants to run their own.

### 1.1 What the project does and does not centrally operate

**Revised 2026-09-12.** An earlier draft of this section forbade any instance registry and any
telemetry outright. That was a constraint this document invented rather than one the project asked
for, and it is reversed here. Knowing which versions are live and how many deployments exist is
ordinary, useful, and how a maintainer decides what to support.

**Modbot Hub and the registry do operate centrally:**

- **An instance registry** (§4). Deployments register themselves; the project can enumerate them.
- **Usage analytics** (§5). Opt-out, default on, disclosed at onboarding.
- **Term list distribution** — Modbot Hub (foundation §4.2.7).
- **Client releases** (§3).

**These remain permanently out of scope:**

- **No central authentication.** Accounts live in each deployment, and the registry never holds
  credentials or sessions.
- **No central moderation data.** No bans, no member lists, no facts, no profile text. That is M8
  §5.1's rule and it is unaffected — the registry knows a deployment *exists*, never who is in it.
- **No public directory.** The registry is not browsable and not enumerable by third parties (§4.3).
- **No content of any kind from a group's database.**

### 1.2 The governing rule still holds

> **Modbot never requires contact with a project-operated service.**

This survives the registry, because neither half of "registration" is mandatory:

- The **register page** (§4.1) runs in the operator's browser and saves to their own
  `localStorage`. It is a bookmark manager. Skipping it costs a convenience.
- The **register API** (§4.2) is called by the deployment *only when usage analytics are enabled*.
  Turn analytics off and the call never happens.

An earlier draft justified the skip path by pointing at air-gapped deployments. **That was wrong** —
Modbot cannot do anything without reaching VRChat's API, so an offline Modbot is not a degraded
deployment, it is a non-functional one. The skip path is justified by consent, not by connectivity.

---

## 2. `my.modbot.co`

### 2.1 What it is

A single static page, modelled on Home Assistant's approach. It holds a list of Modbot instance URLs
in the browser's `localStorage` and lets you pick one.

**No backend. No database. No accounts. No server-side state of any kind.** It is HTML, CSS and
JavaScript on a CDN.

> **Revised 2026-09-14.** The page is still static and still keeps each person's instance list in
> their own browser, but the service that serves it now has a PostgreSQL database for the instance
> registry (§4). It is built as `src/Modbot.My`, one folder per feature under `Features/`, and needs
> `DATABASE_URL`. See §6.

### 2.2 The flow

A deployment that does not know who you are sends you here to be remembered:

```
  modbot-vrckings.up.railway.app
        │  first visit, unknown browser
        ▼
  my.modbot.co/register#instance=modbot-vrckings.up.railway.app
        │  page JS reads the fragment, stores it in localStorage
        ▼
  back to modbot-vrckings.up.railway.app
```

Afterwards, `my.modbot.co` shows your saved instances and you pick one.

**Going to a page on your instance** is `my.modbot.co/go?redir=<path>`. The page lists the saved
instances and sends the browser to that path on the chosen one, going straight there when only one is
saved. `redir` must be a plain path on the instance; anything that could send the browser elsewhere is
refused.

It is the only redirect route. The desktop client's "Pair with a server" opens
`my.modbot.co/go?redir=/pair` (client protocol §3.1), and documentation links use the same route for
any other page. `my.modbot.co` never sees a pairing code; the instance issues it.

> **Revised 2026-09-14.** `/go?redir=` replaced `/instanceredirect?path=` and a separate `/pair`
> route, and `/register` takes `url` rather than `modbotInstanceUrl`.

### 2.3 Fragment vs query string — and why registration uses a query string

This is the one detail that matters, and it is easy to get wrong.

A query string (`?instance=…`) is **sent to the server** and lands in access logs, CDN logs and any
analytics. Even with no application code reading it, the project would end up holding a de facto
registry of Modbot deployments as a side effect of hosting a static page — precisely the thing §1.1
forbids.

**Superseded 2026-09-12.** Registration now uses a **query string** —
`my.modbot.co/register?url=…` — precisely *because* the server is meant to see it
(§4). The fragment technique above is recorded because it remains the right answer for any future
parameter the project should not receive, and because the reasoning is easy to lose.

The selector still stores the instance list in `localStorage`, so **which instances a given person
uses** stays in their browser. The registry learns that a deployment exists; it does not learn who
opens it.

### 2.4 Behaviour

- Multiple saved instances, user-named ("Main group", "Test"), reorderable.
- Remembers the last used one and offers it first.
- Removing an instance removes it from `localStorage`; that is the whole of deletion.
- Warns when adding a non-HTTPS instance, and refuses to auto-redirect to one.
- Works offline for instances already saved, since there is nothing to fetch.
- No cookies, no analytics, no third-party requests, no fonts loaded from elsewhere.

### 2.5 Validation without trust

`my.modbot.co` cannot verify that a URL is really a Modbot instance without contacting it, and
contacting it from the page would leak the instance to nothing useful but would still be a request
the user did not ask for.

So: it does not validate. It stores what it is given and navigates there. The user typed or was
redirected from the instance in question; the page is a bookmark manager, not an authority. It does
check that the URL is well-formed and uses HTTPS before offering to go there.

### 2.6 Self-hosting it

Shipped in the repository and deployable as static files anywhere. A group that would rather not use
a project-run domain can host their own selector, and a group with one instance can ignore it
entirely.

---

## 3. The release host

### 3.1 What it is

A static file host serving **signed client release manifests and payloads**, consumed by the Windows
client's updater (M3 §9).

Like the selector, it has no application server: manifests and installers on object storage behind a
CDN. This is deliberate — the smallest possible thing that can be attacked.

### 3.2 It is untrusted by design

M3 §9.2 names this host as the most dangerous component in the system: it can, in principle, ship
code to every Modbot client everywhere.

The design removes that power rather than guarding it. **Signing happens in CI, with keys the host
never holds, and the client verifies the Authenticode signature and the pinned publisher identity
before touching a payload.**

Consequently:

- Compromising the host yields **nothing**. An attacker can serve an unsigned or wrongly-signed
  payload, which every client rejects; or delete files, which is an outage, not a breach.
- Producing a malicious update requires compromising the **signing identity** (M3 §8.2), not the web
  host — a far harder and far more auditable target.
- The host holds no secrets, so there is nothing on it to steal.

An outage is the realistic failure mode, and its consequence is that clients keep running the version
they have.

### 3.3 The manifest

Per channel, a small JSON document listing available releases: version, payload URL, hash, minimum
supported OS, release notes URL, and — critically — **the range of Modbot API versions each release
supports** (foundation §2.7.3).

```json
{
  "channel": "stable",
  "releases": [
    {
      "version": "2026.1.7",
      "assemblyVersion": "2026.1.7.0",
      "msiProductVersion": "26.1.7",
      "url": "https://…/Modbot-Client-2026.1.7.msi",
      "sha256": "…",
      "apiVersionMin": 3,
      "apiVersionMax": 4,
      "minimumOs": "10.0.19041"
    }
  ]
}
```

That `apiVersionMin`/`apiVersionMax` pair is what makes foundation §2.7.4 work **without a backend**.
The client reads its server's API version, fetches this static file, and picks the newest release
whose range covers it. All selection logic runs in the client; the host serves bytes and learns
nothing about who asked or which server they are pairing with.

Clients poll the manifest on a **jittered** interval — the same reasoning as foundation §4.2.2, one
layer out. Tens of thousands of clients waking on the same minute is a self-inflicted thundering herd
against a CDN for no benefit, and it is avoided the same way: an offset generated per install, not a
shared wall-clock schedule.

Old versions stay available. A client that skipped six releases must be able to reach the current
one, and pulling old payloads breaks exactly the stale installs most in need of updating.

### 3.4 Channels

`stable` and `beta`. Beta is opt-in in the client, and exists mainly so VRChat log-format breaks
(M3 §2.1) can be fixed and validated quickly by people who volunteered for that.

### 3.5 Mirroring and pinning

The feed URL is client-configurable and updates can be disabled entirely (M3 §9.2). A group with a
strict change-control policy can mirror the feed, pin a version, or never call out at all.

Because payloads are signed independently of transport, **a mirror is exactly as trustworthy as the
origin** — which is what makes mirroring a real option rather than a downgrade.

### 3.6 Release process

1. CI builds the client and the WixSharp MSI (M3 §8.4).
2. CI signs binaries and the MSI via Azure Trusted Signing (M3 §8.2).
3. The clean-VM SmartScreen and Defender gate runs (M3 §8.5). **This gate blocks publication** — a
   release that has not been installed from its own signed MSI on a clean machine has not been
   tested.
4. Payloads and an updated manifest are published to object storage.
5. GitHub Releases carries the same signed artefacts, so manual installation never requires the
   release host.

Step 5 is what keeps §1's rule true: if the release host disappears permanently, the project's
releases are still exactly where anyone would look for them.

---

## 4. The instance registry

Deployments register themselves with `my.modbot.co` so the project knows how many exist, what
versions are live, and roughly how they are configured.

### 4.1 The register page — browser-side, for the operator

During onboarding (and from settings afterwards) Modbot offers a button that opens a **new tab**:

```
my.modbot.co/register?url=https://modbot-vrckings.up.railway.app
```

The page saves that URL into the browser's `localStorage` and confirms. That is all it does.

**This is for the operator, not for the project.** It is what makes `my.modbot.co` a usable jumping-off
point — someone who runs Modbot for two groups, or who arrives from the documentation site, picks
their instance from a list instead of hunting for a Railway URL.

A **new tab rather than a redirect-and-return**: the deployment never depends on the round trip
completing, so a slow or unreachable `my.modbot.co` cannot stall onboarding. The wizard continues
behind it.

The instance list lives in that browser and nowhere else — **which instances a given person uses**
stays local, and the selector has no backend for it.

**The instance URL itself is recorded server-side**, as a backup registry. A deployment whose
operator turned analytics off, or who never got as far as self-registering, is still counted. The
page visit contributes the URL and nothing else: no analytics, no group, no version, no operator.

**Revised 2026-09-14.** Page visits are kept in a table of their own, `register_page_instance` —
the instance's origin, when it was first and last seen, and how many visits — apart from
`registered_instance`, which holds what deployments sent about themselves. A page visit therefore
cannot overwrite a self-registered row, and the two can still be compared by URL: both store only the
origin of an absolute `https` address, and a URL carrying a username or password is refused.

### 4.2 The register API — server-side, only with analytics enabled

Separately, a deployment with usage analytics enabled calls `POST /api/instances/register` itself,
then reports periodically (§5).

**This is the only part that tells the project anything**, and it happens only with consent. A
deployment with analytics off never calls it, and works identically.

### 4.3 What registration stores

| Field | Why |
|---|---|
| `instanceId` | The identifier. Random, self-assigned. |
| `instanceUrl` | So the selector can offer it, and so a dead URL can be aged out. |
| `version` | Which releases are live — the input to deprecation decisions. |
| `registeredAt`, `lastSeenAt` | Activity, and pruning abandoned registrations. |

Nothing else. No group id, no group name, no member count, no operator identity, no credentials.

### 4.4 The registry is not a directory

**It is never publicly browsable or enumerable.** There is no endpoint that lists deployments, and
no page that shows them.

A list of every Modbot deployment is a map of VRChat moderation infrastructure — which is exactly
what someone would want if they were looking for a group's moderation server to attack or probe. The
registry exists so the *project* can count and support deployments, not so anyone can find them.

Aggregate figures (how many deployments, version distribution) may be published; the underlying rows
may not.

> **Revised 2026-09-14.** The rows can now be read, but only by the project: `GET /api/instances`,
> `GET /api/instances/{instanceId}`, `GET /api/register-page-instances` and `GET /api/stats` all
> require `Authorization: Bearer <ROOT_API_KEY>`. With no key set, all four refuse everyone. A
> missing key, a wrong key and an unset key get the same 401. Nothing that reads the registry is
> public, and registering and usage reporting stay open because a deployment has no key.

---

## 5. Usage analytics

### 5.1 Opt-out, default on, disclosed at onboarding

The wizard asks, with the toggle already on, and states plainly what is sent. An operator who turns
it off gets a working Modbot — see §5.3 for what actually changes.

### 5.2 What is sent

A periodic report carrying:

| | |
|---|---|
| `instanceId`, `version` | Which build is running |
| Feature flags in use | Discord connected, client paired, term lists imported — **which**, never their contents |
| Scale bucket | Member count as a bucket (`<1k`, `1k-10k`, `10k-50k`, `50k+`), never an exact figure |
| Paired client count | How many moderators run the Windows client |
| Health summary | Counts of rate-limit cold stops and WAF blocks |

**Never sent:** group id or name, any member identity, any moderation data, any fact, any profile
text, any credential, any VRChat instance id, any log content.

That last group is not a promise to be careful — it is the list of things the analytics payload has
no field for.

The health counters are the ones worth having. §4.3's rate limiter is built on estimates about an
undocumented system; knowing that cold stops spiked across many deployments after a VRChat change is
how the project finds out its numbers are wrong, and no single operator can see that pattern.

### 5.3 What declining actually costs

Stated accurately rather than as pressure:

- **Nothing stops working.** Sync, moderation, analytics, the client and term list downloads are all
  unaffected. Modbot Hub does not check registration.
- The project cannot tell you about a version-specific problem affecting *your* configuration —
  general release notes still reach you, targeted warnings do not.
- The project cannot count you when deciding what to support and prioritise.

Support requests may also be harder to diagnose without the health counters, which is a real cost
rather than a threat.

---

## 6. Operational notes

- Both services are static and cheap enough to be uninteresting as a cost centre, which matters for a
  non-commercial project that must be able to run them indefinitely.
- Domains are the real single point of failure — not the hosting. Losing `modbot.co` breaks the
  selector's saved links and the default update feed. The mitigations are the ones already in the
  design: manual bookmarks, GitHub Releases, and a configurable feed URL.
- Neither service is in the critical path of any deployment's operation. A Modbot instance never
  contacts either one; only browsers and clients do.
- `my.modbot.co` needs `DATABASE_URL` (a `postgres://` URL or a keyword string) and refuses to start
  without it, then applies its migrations before serving. `ROOT_API_KEY` unlocks reading the
  registry. `/health/ready` answers only while the database is reachable.
- Its image builds from the repository root, because package versions are pinned in
  `Directory.Packages.props`: `docker build -f src/Modbot.My/Dockerfile .`. On Railway, leave the
  Root Directory empty and set `RAILWAY_DOCKERFILE_PATH=src/Modbot.My/Dockerfile`.

---

## 7. Non-goals

- Any dynamic behaviour on `my.modbot.co`. If it needs a backend, the feature is wrong.
- Server-side storage of instance URLs, including "sync your instance list across devices." That is
  an account system, an instance registry, and a breach waiting to happen, in exchange for saving
  someone one paste.
- Hosting Modbot deployments. The project publishes software, not a service.
- Update delivery for anything other than the Windows client. Server deployments update through
  Railway or the operator's own pipeline.
- Crash reporting, telemetry, or usage analytics from either service.

---

## 8. Open questions

1. **Whether `my.modbot.co` should offer an export/import of the saved instance list**, so a user can
   move it between browsers without anything server-side. A downloadable JSON file is probably the
   whole answer.
2. **Manifest signing in addition to payload signing.** Payload signatures already prevent malicious
   code execution; signing the manifest additionally prevents a compromised host from *withholding*
   an update by serving a stale manifest. Cheap, and probably worth it.
3. **Object storage and CDN choice**, weighted toward a provider with a durable free or near-free
   tier, since these must be affordable in perpetuity.
4. **Whether the beta channel needs a separate signing identity.** Probably not — same publisher,
   different channel — but reputation (M3 §8.2) accrues per identity and beta builds are downloaded
   far less, so splitting them would slow reputation accrual for both.
