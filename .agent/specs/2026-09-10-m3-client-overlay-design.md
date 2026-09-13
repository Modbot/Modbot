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

#### 2.3.0 The flags

```
--log-debug-levels="API;All;Always;AssetBundleDownloadManager;ContentCreator;Errors;NetworkData;NetworkProcessing;NetworkTransport;Warnings" --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging --enable-verbose-logging --log-console-out
```

Long enough that typing it is not realistic — the UI presents it on a **copy button**, and the
quoting inside `--log-debug-levels` must survive the round trip intact.

#### 2.3.1 Detect from the log, not from Steam's configuration

The obvious approach — read Steam's `localconfig.vdf` to see whether the flags are set — is the wrong
one on three counts:

1. **It answers the wrong question.** What matters is whether *the running VRChat* is producing
   verbose output, not whether a config file contains a string. A flag set but VRChat not yet
   restarted looks identical to a flag that works.
2. **It does not generalise.** VRChat also launches from a desktop shortcut or a non-Steam copy.
3. **It reads another application's configuration**, which is squarely the behaviour §8.1 is trying
   to avoid looking like.

Instead: **detect from the log itself.** The client watches for a sentinel — a line shape that
appears if and only if verbose logging is on — and concludes the flag is missing when VRChat is
demonstrably running and writing output but the sentinel never appears.

This works for every launcher, needs no file access outside the log directory, and verifies the thing
that actually matters. It is also self-verifying after the fix: the user restarts VRChat, the
sentinel appears, the client confirms it without being asked.

#### 2.3.2 Instruct, verify — do not silently reconfigure

When the flags are missing, the client shows instructions with the string on a copy button — Steam
launch options, or a desktop shortcut's target. Then it waits, watches, and confirms once VRChat is
restarted.

**Writing the flags into Steam's configuration automatically is available only as an explicit,
consented action**, never a default and never silent. If offered at all it must:

- state exactly which file it will modify and what it will add;
- **append, never replace.** Read the existing `LaunchOptions`, preserve every argument already
  there, and add only the flags that are absent. A moderator may have their own launch options —
  `-vrmode`, resolution overrides, an OSC port — and silently discarding them is both destructive and
  hard to diagnose, since the symptom appears in VRChat rather than in Modbot.
- be **idempotent**: running it twice must not duplicate flags, and re-running after VRChat updates
  must not accumulate them;
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

### 6.0 It renders natively, from local state

The overlay is a **native renderer drawing from the client's own local cache** — not a web view, and
not the Modbot web UI.

An embedded WebView2 loading the web UI was considered and rejected. It looks attractive (one design
system, one codebase) and fails on three counts:

1. **Which server?** The client is paired to several Modbots (§5.5). There is no single URL to load,
   and the right one changes as the moderator moves between instances.
2. **It would fail exactly when it matters most.** The overlay's highest-value moment is *"a flagged
   user just joined"* — which is also the moment latency and network trouble hurt. §5.3 already has
   the client buffering through disconnections; an overlay that goes blank in that same window is
   backwards.
3. **The shared-codebase saving is small.** The overlay is four or five glanceable screens, not the
   web app. It deliberately does **not** implement most of what the web UI does, so "build it twice"
   was never the real comparison.

A fourth, if standalone support is ever revisited: a Quest build could not run WebView2 anyway, so
the web-view route buys single-UI only for as long as the answer stays PC-only.

#### 6.0.1 Avalonia, for both the client window and the overlay

**Decided 2026-09-12.** `Modbot.Client`'s tray window and `Modbot.Overlay`'s in-headset surface are
both **Avalonia**.

The deciding argument is not performance — it is **how many UI stacks the product carries**.

| | Stacks |
|---|---|
| WebView2 client + native overlay | React (web) + WebView2 (client) + native (overlay) = **three** |
| **Avalonia client + Avalonia overlay** | React (web) + Avalonia (desktop and VR) = **two** |

Avalonia renders through SkiaSharp, so the *same* stack draws a normal desktop window and an
offscreen surface handed to `IVROverlay.SetOverlayTexture`. The client window and the overlay stop
being two problems and become one renderer with two hosts. That is the same shape as `IVRChatGate`:
the win is collapsing two things that would otherwise drift into one thing that cannot.

Supporting reasons, in order of weight:

- **Memory, because of where this runs.** The client is resident while VRChat is — which routinely
  uses 8–12 GB with a busy instance. Avalonia sits around 30–60 MB in one process; a WebView2 window
  is 150–250 MB across several Edge processes. Lazy creation would largely close that gap, but on a
  16 GB machine the headroom is worth not spending.
- **No runtime to troubleshoot** on a volunteer moderator's machine.
- **The client's UI is local-machine-shaped** — log parser status, pairing, "what I have sent", flag
  instructions. It renders almost no group data, so React's ecosystem buys little here.

WPF and MAUI were considered and rejected: neither has a supported path from rendered UI to a GPU
texture. WPF's only offscreen route is `RenderTargetBitmap`, a CPU readback — GPU to CPU to GPU
every frame, at 90 Hz. Avalonia is the exception precisely because its backend is swappable rather
than fixed.

**Still to validate:** Avalonia rendering offscreen into a D3D11 texture that OpenVR accepts. That is
one afternoon's prototype and it settles the whole approach — worth doing before M3 is planned, not
argued about further.

#### 6.0.2 Reuse tokens, not components

Design consistency comes from **sharing the design tokens** — the VR density values, colour ramp and
type scale in `Modbot.Web/src/index.css` — generated into a `DesignTokens` constants file the overlay
draws with. Same palette, same sizes, same rules; different renderer.

That is the right level of reuse here. Sharing *components* would have coupled a 90 Hz native
renderer to a DOM, and sharing *nothing* would have let the two surfaces drift apart visually.

#### 6.0.3 Local state, server-synced

The overlay reads what the client already holds: the instance roster from the log, and flag and
history data the client fetched and cached earlier. Nothing renders from a live request.

So the overlay works with stale data rather than no data when a server is unreachable — and it
**says** when data is stale, per §4.2.5's rule that freshness is shown rather than implied. A
moderator seeing "flagged — as of 20 minutes ago" can act on it; one seeing a spinner cannot.

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
| `InstanceJoined` | `Client` | A genuine arrival, observed while already present. Deduplicated across reporting clients. |
| `InstancePresenceObserved` | `Client` | Present when the local user arrived. **Arrival time unknown and earlier** — see §7.1 |
| `InstanceLeft` | `Client` | A genuine departure. Session duration derived from the pair. |
| `AvatarChanged` | `Client` | Avatar **display name** only, from the log. Resolved to `avtr_…` server-side — see §7.2 |

All carry `subject_platform = VRChat` (foundation §5.3), fall under the **Presence** retention class
(kept forever unless an operator configures a window, foundation §5.5), and are covered by
purge-user.

### 7.1 Phantom joins and leaves must not be recorded as real

VRChat emits `OnPlayerJoined` for **everyone already in the instance** when the local user arrives,
and `OnPlayerLeft` for **everyone still present** when they depart. Neither group joined or left.

Recorded naively this inflates join and leave counts by the instance population every time any
moderator enters or exits, and destroys time-spent — the metric M7's giveaways and regulars detection
depend on. It fails silently, like every other hazard in this subsystem.

Both bursts are cleanly delimited:

| Boundary | Rule |
|---|---|
| Arrival | Every `OnPlayerJoined` up to **and including the local user's own** is roster, not arrival |
| Departure | Every `OnPlayerLeft` **after `OnLeftRoom`** is phantom |
| Local identity | `Initialized PlayerAPI "<name>" is local` |

`OnLeftRoom` (the local user left) is distinct from `OnPlayerLeftRoom` (a remote player left). One
character apart, opposite meanings.

Roster observations become `InstancePresenceObserved` with an open-ended earlier bound, which is
exactly foundation §5.3's precision model. The existing supersede rule then resolves the
cross-moderator case for free: a moderator present from the start who saw the genuine arrival
supersedes a later moderator's roster snapshot of the same person.

Full evidence: `.agent/research/vrchat-log-events.md` §3.

### 7.2 Avatar identity is resolved on the server, never the client

**VRChat deliberately withholds avatar ids from clients** to frustrate avatar ripping. The log gives
an avatar's *display name* and nothing more — never `avtr_…`, and never a file id.

The file id comes from **the API, not the log**, and the resolution is a three-hop chain run entirely
on the server:

```
  GET /groups/{groupId}/instances        → live instances   (already scheduled, 1 per 8 s)
        │
        └─ GET /instances/{location}     → Instance.Users : LimitedUserInstance[]
                                             ├─ Id                            usr_…
                                             ├─ DisplayName
                                             └─ CurrentAvatarThumbnailImageUrl
                                                  │
                                                  │  https://api.vrchat.cloud/api/1/file/file_66fe…/1/file
                                                  │                                     └── file id ──┘
                                                  ▼
                                          avatar database (§7.3), keyed on file id  →  avtr_…
```

#### 7.2.1 `Instance.Users` is not available to us — the cheap path does not exist

The SDK model carries `Instance.Users` as `List<LimitedUserInstance>`, with avatar thumbnails, `Bio`
and `Platform` for every occupant in one call. **VRChat only populates it for VRChat staff or the
world's owner.** For an ordinary group-moderator account it comes back empty.

This is recorded because it is an attractive dead end: the field is right there in the model, it
would make avatar tracking nearly free, and anyone reading the SDK will find it and assume it works.
It does not.

So avatar resolution depends on **per-user profile data**, obtained by the user sync described in
foundation §4.2.5 — which is expensive, slow, and must be prioritised rather than swept uniformly.
Avatar identity for someone Modbot has not recently refreshed is simply **unknown**, and the UI says
so rather than guessing.

The practical consequence for M4 §3.2: "ban everyone wearing this avatar" operates on **users whose
profiles are currently fresh**, which in practice means the active population. That is the useful
population anyway — a crasher avatar matters while someone is wearing it in your instance — but the
limit must be stated in the UI rather than implied away.

#### 7.2.2 Freshness, and a free staleness check

The API returns whoever's avatar is current **at the moment of the call**, so a slow resolution
resolves the wrong avatar. Poll interval bounds accuracy, and avatar facts carry their observation
time rather than implying continuity.

The log provides a free correctness check. `Switching <user> to avatar <name>` gives the avatar's
**display name** immediately and at no API cost; the database returns a name too. **If they disagree,
the resolution is stale** — the user changed avatar between the log line and the API call — and the
resolution is discarded rather than recorded against the wrong avatar.

That is worth having precisely because the failure it catches is otherwise silent: a plausible
`avtr_…` attached to the wrong moment, which would then feed M4 §3.2's avatar bans.

#### 7.2.3 Why the split is right anyway

Even setting the mechanics aside, resolution belongs on the server:

- The client stays a passive log reader. Resolution needs outbound calls to VRChat *and* to a
  third-party service the moderator never agreed to talk to.
- Results are **cached once per deployment** rather than re-fetched by every moderator's client.
- The privacy decision about a third-party lookup belongs to the group operator, once, in settings —
  not implicitly to everyone who installs the client.

This split is not a workaround; it is the correct boundary anyway:

- The client stays a passive log reader. Resolution needs outbound calls to a service the moderator
  never agreed to talk to, and doing that from a volunteer's personal PC would violate §3.1's
  "it never left your machine" framing.
- Resolution results are **cached once per deployment** rather than re-queried by every moderator's
  client, which is both far fewer requests and far less exposure.
- The privacy decision about a third-party lookup belongs to the group operator, once, in settings —
  not implicitly to every moderator who installs the client.

### 7.3 Avatar database providers

Avatar resolution uses **VRCX-compatible search APIs** — community-run services, not VRChat's.

**The endpoint is configurable in settings**, shipping with a known list. Providers differ in
capability and quality, and the list records both, because a fresh operator has no way to know:

| Provider | Notes |
|---|---|
| `https://api.avtrdb.com/v3/avatar/search/vrcx` | **supports file-id query** — the capability Modbot needs |
| `https://vrcx.vrcdb.com/avatars/Avatar/VRCX` | search only |
| `https://paw-api.amelia.fun/vrcx_search` | search only |
| `https://avatarwbvrcxsearch.worldbalancer.com/vrcx_search` | search only |
| `https://vrcx.avtr.zip` | ⚠ proxies thumbnails badly |
| `https://api.avatarrecovery.com/Avatar/vrcx` | ⚠ requires cookies |
| `https://avatar.worldbalancer.com/vrcx_search.php` | ⚠ **no file id in response** — cannot serve §7.2 |

**File-id query support is a capability flag, not a detail.** Providers that only search by name
cannot answer "which avatar is this file id", which is the whole question. The settings UI must show
which providers can and cannot, rather than letting an operator select one that silently never
resolves anything.

Known-bad providers ship in the list **with their problems stated** rather than being omitted —
someone will otherwise rediscover them and wonder why they behave oddly.

#### 7.3.1 Rules for calling them

These are volunteer-run services doing the VRChat community a favour. The discipline from §4.3
applies, for the same reasons:

- **Cache aggressively and permanently.** A `file_… → avtr_…` mapping never changes, so it is
  resolved **once per deployment, ever**. Cache hits are the overwhelming majority after warmup.
- **Rate limit** through the gate's hierarchy as its own endpoint class, conservatively by default.
- **Failover in configured order**, skipping providers lacking the needed capability.
- **A miss is normal, not an error.** Coverage is partial; unknown avatars display as unknown rather
  than as a failure.
- **Never block moderation on a lookup.** Resolution is asynchronous and enriches facts after the
  event; a ban is never delayed waiting on a third-party service.

#### 7.3.2 Disclosure

Resolution sends avatar file ids to a third party, which is member-adjacent data leaving the
deployment. Therefore:

- It is **disabled by default** and enabled deliberately, with a plain statement of what is sent
  where at the moment of enabling.
- The endpoint is fully configurable, so an operator may point it at something they run themselves.
- What is sent is **the file id and nothing else** — no user id, no group, no instance, no
  deployment identifier. The provider learns that somebody asked about an avatar, not who wore it
  or where.

That last point is what keeps this compatible with foundation §5.5's posture. The lookup is about an
*avatar*, never about a person.

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
- **Any platform other than Windows PC VRChat.** Quest and other standalone headsets cannot expose
  logs or host a SteamVR overlay at all, so they are not a deferred target -- they are out of scope
  permanently. Meta/Oculus-store VRChat is likewise not supported. macOS and Linux are not targets.

  This is not a limitation to apologise for: presence coverage comes from *moderators* running the
  client, and a group needs only some of its staff on PC for coverage to work. Standalone users are
  still fully visible **in** the data; they just cannot be reporters of it.

---

## 11. Open questions for implementation

These need answers before the plan is written, and at least the first needs hands on a real log file.

1. ~~Log format specifics.~~ **Largely answered** from a real 17k-line sample -- see
   `.agent/research/vrchat-log-events.md` for the full event catalogue, the phantom-burst problem,
   and parsing hazards. Log rotation and concurrent sessions remain unverified.
2. **Leave detection on crash.** If VRChat exits uncleanly, is there a leave line at all? If not,
   sessions need a server-side timeout heuristic — and that heuristic must be visible in the data
   (`occurred_before`, foundation §5.3) rather than inventing a precise departure time.
3. ~~Overlay rendering approach.~~ **Decided: native** (§6.0). A WebView2 route was rejected --
   multi-server pairing gives no single URL to load, it would fail precisely when the network is
   degraded and the overlay matters most, and the overlay implements only a small subset of the web
   UI so the shared-codebase saving was never large. Renderer also decided: **Avalonia** for both the
   overlay and the client window (6.0.1), which takes the product from three UI stacks to two.
   Remaining: prototype Avalonia offscreen into a D3D11 texture OpenVR accepts.
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
11. ~~The verbose-logging flag.~~ **Answered** -- recorded verbatim in 2.3.0. Still needs a
    **sentinel line shape** confirmed against a log captured WITHOUT the flags, since the sample
    analysed so far was captured with them on: it shows what is present, not what is missing.
12. ~~Whether the instance API exposes occupants' avatar ids.~~ **Answered -- it does not, by
    design.** VRChat withholds avatar ids from clients to frustrate ripping. Resolution moves
    server-side via a VRCX-compatible third-party database (7.2, 7.3), which unblocks M4 3.2.
13. ~~Avatar file id in the log.~~ **Moot** — the file id comes from the API, not the log.
    `Instance.Users` returns `LimitedUserInstance` carrying `CurrentAvatarThumbnailImageUrl`, with the
    file id embedded in that URL (§7.2). The log supplies only the avatar display name, which §7.2.2
    uses as a staleness check.
15. **BLOCKING: is `Instance.Users` actually populated for a group instance the Modbot account is not
    physically in?** The SDK model carries the field, but VRChat may omit or truncate it for
    non-occupants — and the entire cost argument in §7.2.1 rests on it. Verify against a live group
    instance before planning M3 or M6. If it comes back empty, avatar tracking needs another source,
    because the per-user fallback is prohibitively expensive.
16. ~~Platform field on instance occupants.~~ **Present** — `LimitedUserInstance` carries `Platform`,
    `Bio`, `StatusDescription` and `UserIcon` as well as the avatar thumbnail. Subject to §15 being
    confirmed, this means the instance roster alone supplies PC-vs-Quest breakdown for M2.5 metrics,
    and **bios for M8 §4's AI profile review without any per-user calls.** Worth checking what else
    the payload carries before designing anything that iterates users.
14. **Response shapes of the avatar providers** in 7.3, which differ per provider and are undocumented.
    Needs one sample response from each before an adapter is written.
