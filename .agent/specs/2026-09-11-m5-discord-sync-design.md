# Modbot M5 — Discord Sync, Linking & Analytics

- **Date:** 2026-09-11
- **Status:** Draft, awaiting review
- **Covers:** M5 — account linking, role sync, ban sync, Discord as a fact source
- **Depends on:** M0 (facts, `INotifier`), M2 (audit log → Discord, already shipped), M4 (moderation actions)
- **Implements:** foundation §9.1

---

## 1. What M5 adds

The basic bot already ships in M0–M2.5: audit log → channels, and lookup commands. M5 turns the
Discord side from a *display surface* into a **connected, two-way, observed** part of the system:

1. **Account linking** — knowing that a Discord member and a VRChat member are the same person.
2. **Role sync** — keeping group roles and Discord roles consistent.
3. **Ban sync** — propagating removals across both platforms.
4. **Discord as a fact source** — an activity history for the Discord side, equivalent to VRChat's
   audit log (foundation §9.1).

Linking is first because the other three depend on it. Without it, Modbot has two unrelated
populations of people.

---

## 2. Account linking

### 2.1 Verification, not guessing

Matching by display name is unsafe: names collide, change, and are trivially impersonated. A false
link is severe — it attaches one person's moderation history, roles and bans to another.

**Modbot never auto-links on name similarity.** Verification is a proof of control:

1. The user runs `/link` in Discord (or starts from the web UI).
2. Modbot issues a short-lived random code.
3. The user places the code in their **VRChat profile bio or status**.
4. Modbot reads the profile through `IVRChatGate` and confirms the code.
5. The link is recorded; the user removes the code.

This proves control of the VRChat account, which is the property that matters. The reverse direction
is proved by the Discord interaction itself.

Per foundation §4.3.4, the profile-read endpoint needs its rate limit confirmed before this is built;
verification is user-triggered and bursty, which is a different load shape from background sync.

### 2.2 Suggested links are not links

Modbot may *suggest* a candidate match — identical names, an existing manual association — but a
suggestion is a prompt for a human or the user themselves to confirm, never an automatic association.
Suggestions are clearly marked as unverified everywhere they appear.

### 2.3 Auto-invite

Where enabled, a verified Discord member who is not yet a VRChat group member can be invited
automatically. Opt-in per deployment, rate-limited through the gate like any other write, and it is a
group *invite*, never an automatic join.

### 2.4 Unlinking

A user can unlink themselves at any time. Unlinking stops future sync but **does not delete history**
— facts already recorded under each platform identity remain, because rewriting history on unlink
would make the record trivially erasable by the person it documents. Purge-user (foundation §5.5)
remains the route for actual erasure, and it is deliberately an operator action rather than a
self-service one.

---

## 3. Role sync

### 3.1 Every mapping has exactly one authority

Bidirectional sync without a designated source of truth produces flapping: two systems overwriting
each other on every change, forever.

So each mapping is a triple — **VRChat role, Discord role, authority** — where authority is
`VRChat`, `Discord`, or `Manual`. The non-authoritative side is a mirror: changes made there are
reverted, and the revert is reported rather than performed silently, since a moderator who just
assigned a role deserves to know why it vanished.

`Manual` means Modbot reports the divergence and changes nothing. This is the right default for a
group that is not yet sure how it wants this to behave.

### 3.2 Partial mapping is normal

Most Discord roles have no VRChat equivalent and vice versa. Unmapped roles on both sides are
untouched, always. Modbot only manages roles it has been explicitly told to manage — an opt-in list,
never "everything not excluded."

### 3.3 Ordering and hierarchy

Discord role hierarchy constrains what the bot can assign: it cannot manage roles above its own. This
is detected at configuration time and reported as a setup problem, not discovered as a runtime
failure on the first sync.

---

## 4. Ban sync

### 4.1 Direction is opt-in, separately, in each direction

Discord ban → VRChat ban and VRChat ban → Discord ban are **different decisions** with different risk
profiles, and each is disabled by default.

A Discord ban is often issued for chat behaviour by a moderator with no VRChat authority; escalating
it automatically into removal from the VRChat group is a significant widening of a punishment. Some
groups want exactly that; none should get it without asking.

### 4.2 Loop prevention

Modbot bans in VRChat → the VRChat audit log records it → Modbot ingests it → Modbot bans in Discord →
Discord emits a ban event → Modbot ingests it → Modbot bans in VRChat. Without care, this is an
infinite loop that also produces duplicate facts forever.

Every synced action records its **origin**: the platform and the action that caused it. A synced
action never triggers a further sync, and an ingested event whose origin is Modbot's own prior sync
is recognised and dropped. This is checked at the fact layer, so it holds regardless of which code
path triggered the action.

### 4.3 Ban sync respects M4's rules

A synced ban is still a ban: it produces attempt and outcome facts (M4 §4.1), goes through the gate
at interactive priority, and carries a classification — inherited from the originating action where
one exists, `Other` where it does not.

Unbans sync too, in the same opt-in directions. A group that syncs bans but not unbans accumulates
people who are unbanned on one platform and permanently banned on the other, which is a trap worth
closing by default.

---

## 5. Discord as a fact source

Foundation §9.1. The bot gains an ingestion role, giving the Discord side the same treatment VRChat's
audit log gets.

| Signal | Path | Retention class |
|---|---|---|
| Member joined / left | fact | Moderation |
| Role granted / removed | fact | Moderation |
| Discord ban / kick / timeout | fact — feeds §5.8 accountability | Moderation |
| Voice channel join / leave | fact — sessions, exactly like instance presence | Presence |
| **Messages sent** | **daily total only** (foundation §5.2.1) | — |
| Member count, online count | daily total snapshot | — |

All facts carry `subject_platform = Discord`.

### 5.1 Message content is never stored

Foundation §5.2.1: message volume increments a counter and writes no fact. Modbot stores **no message
content, no message ids, and no per-message rows** — there is no social graph to leak, export by
accident, or be asked to hand over.

The bot therefore does not need the Message Content intent for analytics. If a future feature needs
it, that is a separate decision requiring its own justification, not an incremental permission grab.

### 5.2 Voice presence is presence

Treated identically to instance presence: same session model, same time-spent daily totals, same
retention class, same purge-user coverage. A community that runs events in Discord voice rather than
in-world gets the same regulars detection and the same giveaway eligibility (M7) as one running
instances.

### 5.3 The linked dossier

Once linked, a dossier answers *"this person"* across both platforms rather than showing two
unrelated records. This is what makes a link worth having: a moderator seeing VRChat kicks alongside
Discord timeouts is seeing one pattern instead of two halves of one.

---

## 6. Analytics surfaced

Server activity charted alongside VRChat metrics, on the same dashboard and the same time axis:
member count over time, joins and leaves, message volume, voice minutes, moderation action volume,
per-moderator activity across both platforms.

The cross-platform view is the point. "Our VRChat group grew but Discord activity fell" is a question
no group can currently answer, and it is answerable the moment both sides are facts in one log.

---

## 7. Permissions and safety

- Bot permissions requested are the **minimum** for configured features; role sync and ban sync each
  require their own, requested only when enabled.
- Missing permissions are reported at configuration time, with the specific missing permission named.
- A dry-run mode for role and ban sync shows what *would* change on first enable. Turning on sync
  against a 5,000-member server without seeing the blast radius first is how a group loses a weekend.

---

## 8. Non-goals

- Storing message content, message ids, or per-message rows.
- Moderating Discord chat. Modbot observes and syncs; it is not an automod.
- Multi-guild support. One deployment, one group, one guild (foundation §2.4).
- Discord-side appeals or ticket UI beyond the accountability tickets of M4.

---

## 9. Open questions

1. **Rate limit for the VRChat profile-read** used in verification (foundation §4.3.4). Different
   load shape from sync — user-triggered and bursty.
2. **Whether VRChat profile status is API-readable** or only the bio, which decides step 3 of §2.1.
3. **Gateway intents required** for member and voice events, and whether Server Members Intent
   approval is needed at the server's size.
4. **Voice session boundaries** — how to treat channel-to-channel moves, and disconnects without a
   clean leave event. The same unclean-exit problem as M3 §9.2, and it should reuse whatever
   heuristic that lands on rather than inventing a second one.
