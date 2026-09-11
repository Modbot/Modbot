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

The cost is real and must be stated plainly: **the log format is undocumented and can change without
notice.** Parsing is therefore isolated behind one interface with recorded fixture logs, so a format
break is a contained failure with clear diagnostics — not a silent data stoppage.

### 2.2 Failing loudly

A log parser that silently stops matching is the worst outcome available: presence history quietly
stops accruing, nobody notices for weeks, and the gap is unrecoverable.

So the client tracks **matched-line rate** and raises a `Critical` notification (foundation §4.5)
when VRChat is demonstrably running and producing log output but Modbot has recognised nothing for
a threshold period. "I can see the log growing and I no longer understand it" is a fault worth
waking someone for.

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

## 8. Non-goals

- Any form of game modification, injection, hooking or memory reading.
- Capturing chat, voice, screenshots, keystrokes, or the process list.
- Observing VRChat activity outside the managed group's instances.
- Acting on VRChat's API as the moderator's own account — all API traffic goes through the server's
  single account and `IVRChatGate` (foundation §2.3).
- Auto-moderation from the client. The client observes and reports; it never acts.
- macOS or Linux builds in M3. The overlay targets SteamVR on Windows, which is where VRChat's
  desktop and VR users are.

---

## 9. Open questions for implementation

These need answers before the plan is written, and at least the first needs hands on a real log file.

1. **Log format specifics.** Exact line shapes for join, leave and avatar change; how instance ids
   and privacy types appear; behaviour on log rotation and on multiple VRChat sessions. Needs a
   research pass against real logs, with fixtures recorded into `tests/`.
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
