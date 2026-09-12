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

### 1.1 What the project will never centrally operate

Recorded as a boundary, so nobody adds one later reasoning that it is small and useful:

- **No instance registry.** The project does not maintain, and cannot enumerate, a list of Modbot
  deployments.
- **No telemetry or phone-home**, including "anonymous usage statistics."
- **No central authentication.** Accounts live in each deployment.
- **No central moderation data** of any kind — that is M8's explicit non-goal (M8 §5.1), and it
  applies here too.
- **No central error reporting** from deployments.

If a future feature appears to need one of these, that is a signal the feature is wrong, not that
this list is.

---

## 2. `my.modbot.co`

### 2.1 What it is

A single static page, modelled on Home Assistant's approach. It holds a list of Modbot instance URLs
in the browser's `localStorage` and lets you pick one.

**No backend. No database. No accounts. No server-side state of any kind.** It is HTML, CSS and
JavaScript on a CDN.

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

### 2.3 The instance URL goes in the fragment, not the query string

This is the one detail that matters, and it is easy to get wrong.

A query string (`?instance=…`) is **sent to the server** and lands in access logs, CDN logs and any
analytics. Even with no application code reading it, the project would end up holding a de facto
registry of Modbot deployments as a side effect of hosting a static page — precisely the thing §1.1
forbids.

A **fragment** (`#instance=…`) is never transmitted. The browser keeps it; only the page's own
JavaScript can read it. The property becomes structural rather than a promise: **the project cannot
learn which instances exist, because the data never reaches it.**

Nothing else about the design changes. This is a one-character difference from the obvious
implementation and it is the difference between a privacy claim and a privacy guarantee.

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
      "version": "2026.1.1a",
      "assemblyVersion": "2026.1.1.1",
      "msiProductVersion": "26.1.11",
      "url": "https://…/Modbot-Client-2026.1.1a.msi",
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

## 4. Operational notes

- Both services are static and cheap enough to be uninteresting as a cost centre, which matters for a
  non-commercial project that must be able to run them indefinitely.
- Domains are the real single point of failure — not the hosting. Losing `modbot.co` breaks the
  selector's saved links and the default update feed. The mitigations are the ones already in the
  design: manual bookmarks, GitHub Releases, and a configurable feed URL.
- Neither service is in the critical path of any deployment's operation. A Modbot instance never
  contacts either one; only browsers and clients do.

---

## 5. Non-goals

- Any dynamic behaviour on `my.modbot.co`. If it needs a backend, the feature is wrong.
- Server-side storage of instance URLs, including "sync your instance list across devices." That is
  an account system, an instance registry, and a breach waiting to happen, in exchange for saving
  someone one paste.
- Hosting Modbot deployments. The project publishes software, not a service.
- Update delivery for anything other than the Windows client. Server deployments update through
  Railway or the operator's own pipeline.
- Crash reporting, telemetry, or usage analytics from either service.

---

## 6. Open questions

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
