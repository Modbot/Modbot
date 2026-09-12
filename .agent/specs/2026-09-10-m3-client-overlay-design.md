# Modbot M3 — Windows Client & SteamVR Overlay

- **Date:** 2026-09-10
- **Status:** Draft, awaiting review
- **Covers:** M3 — presence ingestion, client authentication, the SteamVR overlay
- **Depends on:** M0 foundation (`.agent/specs/2026-09-04-modbot-foundation-design.md`) — fact log, `IModbotClock`, dedup window, `INotifier`
- **Prerequisite for:** M7 (segments, cohorts, giveaways)

---

## 1. Why this is third, not sixth

Presence data is the only thing in Modbot that **cannot be backfilled**. Every week the client does
not exist is a week of instance history no future feature can recover — "who are our regulars",
"who was in the instance when that happened", and every giveaway weighted by time spent are all
questions whose answers are being destroyed right now, not merely deferred.

Moderation actions, Discord sync and instance tooling all defer at the cost of staff *time*. This
defers at the cost of *history*. See foundation §14.1.

---

## 2. What the client is, and what it is not

**It is:** a small tray application that watches VRChat's own log output, recognises instance
join/leave and avatar-change events, and reports them to a Modbot server the moderator has
explicitly configured. Plus a SteamVR overlay that shows moderation context inside the headset.

**It is not:** a mod, a patch, or an injector. It does not attach to the VRChat process, does not
modify game files, does not hook anything, and does not use VRChat's API on the user's behalf.

That last paragraph is a hard product boundary, not a description of the first version.

### 2.1 Why it reads logs rather than anything cleverer

VRChat writes a plain-text diagnostic log containing instance transitions and avatar loads. Reading
it is:

- **Passive.** Nothing is injected, patched or hooked; VRChat is unmodified and unaware.
- **Legible.** A suspicious moderator can open the same file in Notepad and see exactly what Modbot
  reads. This is what makes §3.2 of the foundation spec enforceable rather than aspirational.
- **Not a TOS problem.** Reading a file the application wrote for its own diagnostics is categorically
  different from modifying the client, which is what gets people banned.

The format is undocumented, but the usual worry about that does not apply here.

**There is only ever one log format in the wild.** VRChat refuses connections from outdated clients,
so every player is on the current build by force. There is no long tail of old versions to support,
no format-version detection, no fallback chain, and no compatibility shims. The format has also been
stable for roughly six years.

So: **one parser, for the current format, and nothing else.** Fixtures exist as regression tests, not
as a compatibility matrix.

The real risk is not *supporting* a change — it is the **time between a change landing and a fixed
client reaching moderators**. And forced updates make that risk sharper rather than softer: when
VRChat changes the format, **every Modbot client breaks at once**, with no gradual rollout to notice
it happening.

That reframes two other parts of this spec. §2.2's loud-failure alarm is what makes the break
*visible* quickly, and §9's update mechanism is what makes the fix *arrive* quickly — which is why
updates are near-mandatory (§9.3) rather than a convenience. Detection speed and delivery speed are
the entire mitigation.

### 2.2 Failing loudly

A log parser that silently stops matching is the worst outcome available: presence history quietly
stops accruing, nobody notices for weeks, and the gap is unrecoverable.

So the client tracks **matched-line rate** and raises a `Critical` notification (foundation §4.5)
when VRChat is demonstrably running and producing log output but Modbot has recognised nothing for
a threshold period. "I can see the log growing and I no longer understand it" is a fault worth
waking someone for.

### 2.3 Verbose logging is a prerequisite, and it needs a launch flag

The log detail Modbot depends on is only produced when VRChat is started with a **command-line
flag**. Without it the client runs, reads the log, and silently learns nothing useful.

This makes flag setup a **first-run blocker** for the client, not a settings-page detail.

#### 2.3.1 Detect from the log, not from Steam's configuration

The obvious approach — read Steam's `localconfig.vdf` to see whether the flag is set — is the wrong
one on three counts:

1. **It does not generalise.** VRChat is also launched from the Oculus store, from a desktop
   shortcut, and from a non-Steam copy. Steam's config answers the question for one launcher.
2. **It answers the wrong question.** What matters is whether *the running VRChat* has verbose output,
   not whether a config file contains a string.
3. **It reads another application's configuration**, which is squarely the behaviour §8.1 is trying
   to avoid looking like.

Instead: **detect from the log itself.** The client watches for a sentinel — a line shape that
appears if and only if verbose logging is on — and concludes the flag is missing when VRChat is
demonstrably running and writing output but the sentinel never appears.

This works for every launcher, needs no file access outside the log directory, and verifies the thing
that actually matters. It is also self-verifying after the fix: the user restarts VRChat, the
sentinel appears, the client confirms it without being asked.

#### 2.3.2 Instruct, verify — do not silently reconfigure

When the flag is missing, the client shows **platform-specific instructions** with the exact flag on a
copy button: Steam launch options, Oculus, or a desktop shortcut's target. Then it waits, watches, and
confirms once VRChat is restarted.

**Writing the flag into Steam's configuration automatically is available only as an explicit,
consented action**, never a default and never silent. If offered at all it must:

- state exactly which file it will modify and what it will add;
- **require Steam to be closed**, because Steam rewrites `localconfig.vdf` on exit and will discard
  the change otherwise;
- back the file up first;
- handle Steam installed outside the default path, and multiple Steam user profiles.

The reason for the caution is §8.1. Modbot's client already looks like an infostealer to a heuristic
scanner; software that also **edits another application's configuration without asking** looks like a
game-hacking tool, and the trust argument in §3 is not compatible with doing it quietly. A moderator
pasting one flag into a text box is a small cost, and it keeps the client's behaviour inside what
§3.2's comments can honestly describe.

#### 2.3.3 Ongoing

The flag can be removed later — a Steam reinstall, a shortcut replaced, a second PC. The client
therefore treats the sentinel as an ongoing health signal rather than a one-time check, and raises the
missing-flag prompt again rather than reporting nothing forever.

This shares the mechanism with §2.2's format-break alarm; the two conditions are distinguished by
whether *any* recognised lines are appearing at all.

---

## 3. Trust

The client is the piece most likely to be refused by the people who need to install it. A volunteer
moderator is being asked to run background software on a personal machine that watches what they do
in VRChat. Suspicion is the correct response, and the design must answer it rather than ask for
faith.

### 3.1 Data minimisation, decided at the edge

**Filtering happens on the moderator's PC, before transmission — never server-side.** The
distinction matters: "we discard it on receipt" requires trusting the server operator; "it never
left your machine" does not.

| Read from the log | Transmitted | Never transmitted |
|---|---|---|
| Instance join/leave lines | VRChat user id, instance id, timestamp | The raw log line |
| Avatar change lines | Avatar id, timestamp | Anything about the world outside the managed group |
| — | — | Chat, friends list, DMs, private worlds, screenshots, keystrokes, process list |

**Instances belonging to other groups, and private or friends-only instances unrelated to the
managed group, are dropped locally and never reported.** The client is a group moderation tool; it
has no business observing a moderator's personal VRChat use, and it does not.

### 3.2 Commented source as a first-class deliverable

Foundation §3.2 applies in full: every read, capture and transmit function carries a plain-language
comment for a suspicious reader; redaction is commented at the point it happens; the README's
summary table of files-read and fields-sent is kept in sync with the code, and drift is a review
failure.

### 3.3 Visible, not ambient

- A tray icon is always present while running. The client never runs invisibly.
- **A live "what I have sent" view** — the last N events transmitted, in plain language, on demand.
  Not a log file; a screen.
- **A pause button** that stops all transmission immediately, with a visible paused state.
- Uninstall removes the credential and stops reporting; it does not require server-side action.

### 3.4 Consent is per-moderator and revocable

Installing is a decision by the moderator, not a policy pushed by the group owner. A group owner can
*ask*; they cannot silently enrol someone. Purge-user (foundation §5.5) covers client-sourced facts,
so a moderator who leaves can have their own presence history erased on request.

---

## 4. Client authentication

A new auth surface. Staff sign in with a username and password; a client running unattended cannot
hold one.

**Device tokens**, distinct from the `ApiKey` used for the read API:

- **Scoped to ingest only.** A stolen device token cannot read the member list, cannot ban, cannot
  read the dossier. It can submit presence facts and nothing else.
- **One per device, individually revocable.** A moderator leaving the team must not require rotating
  every other moderator's token.
- **Issued by a short-lived pairing code** shown in the web UI and typed into the client once, so the
  token itself never travels through a chat message or email.
- **One per server, per device.** A moderator staffing two groups pairs the same client twice and
  holds two unrelated tokens; neither group's operator learns of the other (5.5.1).
- **Attributable.** Every fact records which device token submitted it, which makes a compromised or
  misbehaving client identifiable and its facts revocable as a set.
- Last-seen timestamp and client version surfaced in settings, so an operator can see which
  moderators are actually reporting and which installs are stale.

---

## 5. Ingest

### 5.1 Deduplication is the load-bearing property

Four to six moderator clients in one instance all observe the same join and all report it — a **6×
amplification** (foundation §5.7). Deduplication happens at the **ingest boundary**, using the
windowed ±5 s range check of foundation §5.7.1 with a transaction-scoped advisory lock.

Its failure mode is silent: nothing errors, and every time-spent metric is simply wrong by six.
That is why it carries the heaviest test burden in the project.

### 5.2 Clients report in server time

Clients synchronise to `IModbotClock` via the SNTP round-trip estimate (foundation §4.4) and report
`occurred_at` already offset-corrected, alongside their measured offset and its confidence. The
server independently records `observed_at`, which is authoritative for ordering, and clamps-and-flags
client timestamps beyond plausible transport delay.

This is what makes a ±5 s window possible at all: against raw machine clocks the window would have
to exceed worst-case clock skew, which would swallow the 15-second genuine rejoin it must preserve.

### 5.3 Offline buffering

VRChat sessions outlive network blips, and a moderator's home connection is not a datacentre. The
client buffers locally and replays on reconnect, bounded in size and age, with the buffer visible in
the "what I have sent" view. Replayed facts carry their original `occurred_at`; the server's
`observed_at` reflects actual receipt, so the gap between them is itself visible in the data.

### 5.4 No hardcoded capacity

Foundation §3.1 applies. Instance capacity is read from the API, never assumed to be 80 — VRChat
grants exemptions raising it to 200–300, and a group with one must not see "80/80 — full" for an
instance holding 240 people.

---

### 5.5 One client, many servers

A moderator may be staff in several groups, each running its own Modbot on its own upgrade schedule
(foundation §2.7.4). The client therefore holds **many server connections**, not one.

```
                    ┌──────────────┐
   VRChat log ─────▶│  log reader  │   read ONCE
                    └──────┬───────┘
                           │  parsed events, tagged with owning group
                    ┌──────▼───────┐
                    │   router     │   which server manages this group?
                    └──┬────────┬──┘
                       │        │
              ┌────────▼──┐  ┌──▼─────────┐
              │ Server A  │  │ Server B   │   separate device tokens,
              │ api v6    │  │ api v4     │   separate negotiated versions,
              └───────────┘  └────────────┘   separate buffers and pause state
```

- **The log is read once.** Running one client per server would duplicate parsing, duplicate
  reporting, and give the moderator two tray icons and two updaters for one machine.
- **Each connection negotiates independently** and uses the protocol implementation matching its own
  negotiated API version. The rest of the client is unaware which version any given server speaks.
- **Per-server everything**: device token, pairing, buffer, pause state, health, and "what I have
  sent" (§3.3). A moderator must be able to pause reporting to one group without pausing the other.

#### 5.5.1 Cross-group leakage is a hard boundary

**Events for one group must never reach another group's server.** Not filtered out on receipt —
never sent.

This follows directly from §3.1: filtering happens on the moderator's PC, and the whole trust
argument is "it never left your machine." A moderator staffing two unrelated communities must be able
to install one client without either group's operator gaining visibility into the other's instances.

Routing is therefore by **owning group**, established before transmission, with events whose group
cannot be determined dropped rather than broadcast. This is the single most important test in the
multi-server work, and its failure mode is silent — everything appears to function while one
community's data quietly accumulates in another's database.

**This is implementable purely locally**, because VRChat carries the owning group inside the instance
id:

```
wrld_4b34…:39911~group(grp_2d8c…)~groupAccessType(members)~region(use)
                 ^^^^^^^^^^^^^^^^
```

The client parses the id and matches `grp_…` against the managed group each paired server declared at
pairing (§4). No API call, no server round-trip, nobody asked. Had ownership required a lookup, the
client would have had to ask *some* server which group an instance belonged to — and asking the wrong
one is precisely the leak this boundary exists to prevent.

It also makes §3.1's filter exact rather than heuristic: **an instance id with no `~group(…)`
qualifier is not a group instance** and is dropped before transmission. A moderator's public,
friends-only and private VRChat use is excluded by structure. Full grammar in
`.agent/research/vrchat-log-format.md`.

#### 5.5.2 The overlay follows the instance

With multiple servers configured, the overlay shows context from **whichever server manages the
instance the moderator is currently in**. Flagged-user alerts, history lookups and roster come from
that group's Modbot, and the overlay makes clear which group it is speaking for — a moderator seeing
a flag needs to know whose flag it is.

---

## 6. The SteamVR overlay

### 6.1 It is the only channel that exists in-headset

A moderator inside VRChat cannot see email, Discord, or a browser. For the person doing moderation
at the moment it matters, the overlay is the entire notification surface (foundation §4.5.2).

### 6.2 What it shows

- **Flagged-user alerts** — a user with prior kicks, an active warning, or a flag just joined this
  instance. This is the highest-value thing the overlay does.
- **Repeat-offender context on demand** — pull up a user and see their moderation history without
  leaving VR (foundation §5.8.4).
- **Instance roster** — who is here, who is a group member, who is staff.
- **Critical Modbot health** — credentials rejected, WAF block, ingest stopped.

### 6.3 Interaction is glanceable and low-friction

Reading is fine in VR; typing is hostile. So: dismissible cards, large targets, and **no text entry
in the primary flows**. This is the surface that makes foundation §5.8.2's one-tap classification
enum matter most — a row of buttons works in a headset, a text box does not.

Alerts must be dismissible and rate-limited. An overlay that interrupts constantly gets disabled,
and a disabled overlay notifies nobody.

---

## 7. Facts emitted

| Fact | `source` | Notes |
|---|---|---|
| `InstanceJoined` | `Client` | Deduplicated across reporting clients |
| `InstanceLeft` | `Client` | Session duration derived from the pair |
| `AvatarChanged` | `Client` | Avatar id only |

All carry `subject_platform = VRChat` (foundation §5.3), fall under the **Presence** retention class
(90 days by default, foundation §5.5), and are covered by purge-user.

---

## 8. Distribution, signing, and not being flagged

### 8.1 State the problem honestly

Modbot's client reads another application's files, transmits what it finds to a network endpoint,
runs at login, sits in the tray with no main window, and updates itself. **That is, feature for
feature, the behavioural signature of an infostealer.** No amount of good intent changes what the
heuristics see.

So the approach is not to argue with the classifier. It is to **establish a verifiable identity**
that makes behaviour a secondary signal, and to avoid the specific behaviours that look like evasion.

Two separate systems are in play and they fail differently:

| | SmartScreen | Defender / AV engines |
|---|---|---|
| Basis | **Reputation** — per signing identity and per file hash | **Heuristics + cloud ML** on behaviour and file shape |
| Symptom | "Windows protected your PC", blue box, Run anyway hidden | Quarantine, silent deletion, blocked download |
| Fixed by | Code signing + accrued download reputation | Not looking like a dropper; false-positive submission |

### 8.2 Code signing

**Everything ships signed**: the EXE, the DLLs Modbot authors, the MSI, the bootstrapper if one
exists, and **every update payload**.

Since the CA/Browser Forum's June 2023 change, all code-signing private keys must live in certified
hardware or an HSM — there is no longer a "download a .pfx and sign locally" option at any
assurance level. That leaves three realistic routes:

| Option | Cost | SmartScreen reputation |
|---|---|---|
| **Azure Trusted Signing** ← recommended | ~$10/month, no hardware token | Accrues; Microsoft-operated issuer helps |
| EV certificate + hardware token | ~$300–500/year plus token | **Immediate** — the main reason to pay for EV |
| OV certificate + HSM | ~$200–400/year | Accrues slowly over downloads and time |

Azure Trusted Signing is the recommended default: it removes the hardware-token logistics that make
CI signing painful, and the price is compatible with a non-commercial community project. If early
adopters hit SmartScreen walls and abandon installation, EV is the escape hatch — that is the
specific problem EV solves and the only reason to spend the money.

**Reputation is per signing identity.** Changing certificate, publisher name, or product name resets
it. Pick the identity once and keep it — including across a certificate renewal, which should be a
renewal of the same subject rather than a new one.

### 8.3 Do not look like a dropper

Concrete build and packaging rules, each of which maps to a heuristic:

- **No single-file self-extracting publish.** .NET's single-file mode extracts to a temp directory
  and executes from there, which is precisely the dropper pattern. Ship a normal folder layout
  installed by the MSI.
- **No obfuscation, no packing, no compression of the payload executable.** Packers are used almost
  exclusively to evade analysis, and engines treat them accordingly.
- **Install to `Program Files`** via the MSI, with a proper Add/Remove Programs entry and a working
  uninstaller. Software that installs to `%APPDATA%` and cannot be uninstalled is scored as hostile,
  correctly.
- **Never download-and-execute.** The updater verifies a payload's Authenticode signature *and its
  publisher identity* before doing anything with it — not merely that a signature is present.
- **Stable file names, paths and product identity across versions**, so reputation accumulates on
  one thing rather than being spread across many.
- **Keep the visible tray icon and UI** (§3.3). Silent, windowless, tray-less background software is
  a heuristic input in its own right.

`★` The convergence here is worth noticing: **the things that make a suspicious moderator trust the
client are the same things that make Defender trust it.** Visible UI, honest installer, real
uninstaller, no obfuscation, signed and attributable publisher, no download-and-run. §3 and §8 are
the same design pressure arriving from two directions, which is a good sign that neither is
security theatre.

### 8.4 Installer: WixSharp

Installers are authored with **WixSharp** — a C# DSL over WiX/MSI, which keeps packaging in the same
language and repository as the rest of the project rather than in hand-written XML.

It provides what §8.3 requires: `Program Files` installation, Add/Remove Programs registration, a
real uninstaller, and upgrade codes for clean in-place version upgrades. The installer build is part
of CI and its output is signed in the same pipeline step as the binaries.

The MSI is signed **in addition to** its contents. An unsigned MSI wrapping signed binaries still
triggers SmartScreen at the moment that matters — the download.

### 8.5 Release gate

Before any release is published:

1. Install from the signed MSI on a **clean Windows VM** with Defender at default settings and
   SmartScreen enabled, downloaded over HTTPS the way a real user would.
2. Record whether SmartScreen interstitials appear and what Defender does.
3. If flagged, submit to Microsoft's false-positive channels (Defender sample submission and the
   SmartScreen/Edge report path) **before** announcing the release, not after moderators start
   reporting scary warnings.

A release that has not been through this on a clean machine has not been tested, because the
developer's own machine has trusted the binary since the first local build.

---

## 9. Updates

### 9.1 Central update host

Update manifests and payloads are served by a **central host operated by the Modbot project** — not
by the operator's self-hosted instance. Self-hosters do not build, sign or publish clients, so the
release feed is necessarily central.

The client therefore talks to **two** servers, and they are trusted for different things:

| | The operator's Modbot instance | The central update host |
|---|---|---|
| Purpose | Receives presence facts | Serves signed releases |
| Chosen by | The moderator, during pairing | The project, by default |
| Compromise means | This group's presence data is exposed | **Code execution on every Modbot client** |

### 9.2 That second column is a supply-chain surface, and is treated as one

A central host that can ship code to every Modbot install everywhere is the most dangerous component
in the entire system, and pretending otherwise would undercut everything §3 claims.

- **Signature verification before anything else.** The client verifies the payload's Authenticode
  signature *and that the publisher identity matches the pinned expected one*. A validly-signed
  binary from the wrong publisher is rejected exactly like an unsigned one. Serving a malicious
  update therefore requires compromising the signing identity, not merely the web host.
- **Updates are consent-gated and visible.** The client tells the user what it is about to install
  and asks. This is the same principle as §3.3 — a self-updating invisible background binary is
  precisely what §3 asks people to trust it not to be, so it must not become one.
- **The feed URL is configurable, and updates can be disabled entirely.** A tool that is genuinely
  self-hostable must not force a dependency on infrastructure the project happens to run. Groups
  with strict policies can mirror the feed or pin a version and never call out.
- Manifests over HTTPS; stable-and-beta channels; full payloads rather than binary deltas, because
  delta patching adds an attack surface and a failure mode for no meaningful benefit at this size.

### 9.2.1 Version compatibility

The client carries its own calendar version (foundation §2.7.1) and declares the **range of Modbot
API versions it supports** (§2.7.3). A moderator never chooses a build:

1. During pairing, the client reads the server's API version from its unauthenticated endpoint.
2. It fetches the static release manifest (central services §3.3) and picks the newest release whose
   `apiVersionMin`/`apiVersionMax` covers that server.
3. It installs that one.

Because self-hosted servers update on their operator's schedule, **the newest client is frequently
not the right client.** A group still running a six-month-old Modbot must get the newest build that
still speaks its API version, not the newest build that exists — so the updater's job is "newest
*compatible*", never "newest".

When a server moves to an API version no released client supports yet, or the client's own build is
too old to speak to an upgraded server, the client says so with **both numbers named** and what to
do. A version mismatch must never present as a parse error, a silent no-op, or ingest that quietly
stops — which would be indistinguishable from the log-parser failure of §2.2 and is exactly as
unrecoverable.

### 9.3 Why updates are close to mandatory anyway

§2.1 records that VRChat's log format is undocumented and may change without notice. When it does,
every client stops recognising events until it is updated. Moderators will not update manually, and
§2.2's loud-failure alarm tells you the data has stopped rather than fixing it.

So the tension is real and worth naming: **updates are a supply-chain risk that the design also
depends on.** §9.2 is what makes accepting that risk defensible, and the client version reported
alongside each device token (§4) is what lets an operator see how much of their coverage is running
a stale parser.

---

## 10. Non-goals

- Any form of game modification, injection, hooking or memory reading.
- Capturing chat, voice, screenshots, keystrokes, or the process list.
- Observing VRChat activity outside the managed group's instances.
- Acting on VRChat's API as the moderator's own account — all API traffic goes through the server's
  single account and `IVRChatGate` (foundation §2.3).
- Auto-moderation from the client. The client observes and reports; it never acts.
- macOS or Linux builds in M3. The overlay targets SteamVR on Windows, which is where VRChat's
  desktop and VR users are.

---

## 11. Open questions for implementation

These need answers before the plan is written, and at least the first needs hands on a real log file.

1. ~~Log format specifics.~~ **Largely answered** from a real 17k-line sample -- see
   `.agent/research/vrchat-log-events.md` for the full event catalogue, the phantom-burst problem,
   and parsing hazards. Log rotation and concurrent sessions remain unverified.
2. **Leave detection on crash.** If VRChat exits uncleanly, is there a leave line at all? If not,
   sessions need a server-side timeout heuristic — and that heuristic must be visible in the data
   (`occurred_before`, foundation §5.3) rather than inventing a precise departure time.
3. **Overlay rendering approach.** Native OpenVR overlay versus an embedded web view rendered to a
   texture. The latter reuses the React components from M2.5; the former is lighter and more
   reliable. This is the single biggest implementation fork in M3.
4. **Update mechanism.** Moderators will not manually update. Self-update is near-mandatory for a
   client whose log parser will break when VRChat changes format — but a self-updating background
   binary is exactly the thing §3 asks people to trust. Signed releases and a visible,
   consent-gated update prompt are the likely answer.
5. **Pairing code UX** for a moderator who is already in VR when they install.
6. **Signing identity and subject name**, chosen once (§8.2). Reputation accrues to it and resets if
   it changes, so this is effectively irreversible and should be decided before the first public
   release rather than after.
7. **Which VRChat tools already pass SmartScreen cleanly, and how.** Worth a short research pass —
   whether they hold EV certificates, have simply accrued reputation over years, or sidestep
   SmartScreen entirely by shipping through Steam. Steam distribution for the overlay is a real
   option that is worth pricing before committing to the certificate route.

8. ~~How the client determines which group owns an instance.~~ **Answered** — the owning group is
   carried inside the instance id itself (`~group(grp_…)`), so routing is local string parsing with
   no lookup and no leak. See `.agent/research/vrchat-log-format.md` §1.
9. ~~Do the player join/leave log lines carry the VRChat user id?~~ **Answered — yes**, alongside
   the display name. §3.1's privacy table stands, deduplication keys on a stable identity, and no
   server-side name-to-id resolution is needed. Display names are still captured opportunistically
   as fact *data* (useful for M4 §7 ban reports and historical name search) but never as identity.
   Research §2.1.
10. **Instance id and name handling.** The instance id is arbitrary user-controlled text and the
    name is a separate mutable field; both are hostile input on display surfaces (M6 §4.1.1), and
    identity is `worldId` + `instanceId`. Confirm parsing against the real log sample, including a
    group instance whose id was set to free text via VRCX.
11. **The verbose-logging flag itself** (2.3): its exact name, and a **sentinel line shape that
    appears if and only if it is enabled**. The sentinel is what 2.3.1's detection is built on, so
    it needs confirming against a log captured WITHOUT the flag -- the sample analysed so far was
    captured with it on, which shows what is present but not what is missing.
12. **Whether the instance API exposes occupants' avatar ids.** Avatar ids are absent from the log
    (research 4), which blocks M4 3.2's ban-by-avatar-id. Worth one focused check before M4 is
    planned, since it decides whether that feature ships or is withdrawn.
