# Modbot M4 — Moderation Actions

- **Date:** 2026-09-11
- **Status:** Kick, ban and unban implemented 2026-09-16 (§12). Everything else still a draft.
- **Covers:** M4 — performing moderation through Modbot, classification capture, ban reports, accountability tickets
- **Depends on:** M0 (`IVRChatGate`, fact log, `INotifier`), M1 (member/ban cache), M2 (audit log), M3 (avatar facts, overlay)
- **Implements:** foundation §5.8 capture side

---

## 1. What changes at M4

Until now Modbot has only *observed*. M4 is the first milestone where Modbot **writes to VRChat**, and
that changes three things structurally:

1. Interactive work now competes with background sync for a scarce request budget.
2. Actions can fail — and a moderation tool that silently fails to ban someone is worse than one that
   cannot ban at all.
3. Modbot becomes the point of capture for classification (foundation §5.8.2), which is what makes
   accountability and repeat-offender detection legible rather than guesswork.

---

## 2. Two kinds of action, and the distinction matters

| | VRChat-side | Modbot-side only |
|---|---|---|
| Examples | Ban, unban, kick from instance, role grant/revoke | **Warn**, **note**, **watch** |
| Effect | Changes state in VRChat | Changes state only in Modbot |
| Can fail | Yes — network, rate limit, permissions, WAF | No |
| Reversible | Via VRChat | Freely |

**Warns, notes and watches are Modbot concepts.** VRChat has no warning system, so Modbot provides
one: a recorded, attributable, optionally-notified event that builds the history foundation §5.8.4
surfaces. A warn optionally reaches the user through Discord if their account is linked (M5) or via
the overlay if a moderator is present; if neither, it is still recorded, because its primary purpose
is the record.

This distinction must be visible in the UI. A moderator needs to know whether they just changed
something in VRChat or only in Modbot — conflating them produces staff who believe a user was
punished when nothing happened.

---

## 3. Resolving a target

Foundation goal: ban by display name, user id, **or avatar id**, without hunting through VRChat's UI.

| Input | Resolution |
|---|---|
| User id (`usr_…`) | Direct |
| Display name | `pg_trgm` fuzzy match against the cached member table (foundation §6.4), ranked, **always disambiguated by a human** |
| Avatar id (`avtr_…`) | Everyone observed wearing it, from `AvatarChanged` facts (M3) |

### 3.1 Display names are not identifiers

VRChat display names are mutable and non-unique enough to be dangerous. Modbot therefore **never
performs an action on a name match alone** — it resolves to candidates and a human picks. The
candidate list shows user id, join date, avatar, and prior moderation history so the choice is
informed.

Banning the wrong person because two users had similar names is the single most damaging mistake this
feature can make, and it is entirely preventable by refusing to guess.

### 3.2 Avatar-id targeting depends on M3

"Ban everyone wearing this crasher avatar" is only possible because the client reports
`AvatarChanged` facts. This is a concrete payoff from moving the client ahead of this milestone —
at the original ordering, avatar-id banning would have had no data to work from.

Because this is inherently a **bulk** action, it gets bulk safeguards (§5).

---

## 4. Executing through the gate

All writes go through `IVRChatGate` at **interactive priority**, preempting background sync
(foundation §4.1).

### 4.1 Attempt and outcome are both facts

An action produces a fact when it is *attempted* and a fact when it *resolves*. They are separate
because the gap between them is real and sometimes long: the action may be queued behind a rate
limit, may fail, or may partially succeed in a bulk operation.

Never show a moderator a success that has not happened. The UI reflects queued, succeeded, or failed
— optimistic confirmation is forbidden here, because "I thought I banned them" is a safety problem,
not a UX blemish.

### 4.2 When `moderation.write` is cold

Foundation §4.3.1: interactive actions obey their own bucket's cold stop. If it is stopped, the
action **fails fast with an explanation** rather than queueing into a penalty that retrying would
extend. The message says what happened, that it is a VRChat rate limit rather than a Modbot fault,
and roughly when it will clear.

### 4.3 One confirmation, one action

Every action carries a key the browser generates when the confirmation opens. A double-submitted
ban, a retried request after a timeout, or an impatient second click results in one ban and one
fact — not two.

**Settled 2026-09-16.** The key is claimed as a row in `moderation_action`, on a unique index,
*before* anything is sent to VRChat; a second request carrying the same key finds that row and is
answered from it. The disabled button in the browser is a courtesy on top; the row is the
guarantee, because a browser retry the page never saw would get past the button and not past the
index. The same row is also §4.1's attempt-and-outcome pair: it is written when the action starts
and finished when VRChat answers.

---

## 5. Bulk actions

Avatar-id targeting and multi-select make bulk moderation possible, which makes bulk mistakes
possible at the same scale.

- **Preview before execute.** The exact list of affected users, with counts, and the ability to
  deselect individuals.
- **Explicit confirmation above a threshold**, requiring the count to be typed rather than clicked.
- **Serialised through the gate** at the configured rate; a bulk ban of 200 users is a long-running
  job with visible progress, not a burst.
- **Cancellable mid-run**, with a clear record of which users were actioned before cancellation.
- **A single reversal handle.** Bulk actions are grouped, so "undo that" means undoing the group,
  not finding 200 individual bans by hand.
- Group owners can cap bulk size, or disable bulk entirely, per role.

---

## 6. Classification capture

Foundation §5.8.1–§5.8.2 govern this, and the friction rules are not negotiable in implementation:

| Action | Classification |
|---|---|
| Kick, warn, mute | **Optional**, one tap |
| Ban | **Required**, part of the report |

- The enum is **group-editable** (defaults: Crasher, Harassment, NSFW, Spam, Advertising, Underage,
  Ban Evasion, Other) and rendered as **buttons**, never a dropdown-and-text-box.
- `RequireModerationClassification` in `Settings` flips kick/warn/mute to required for groups that
  want it. Default off.
- Free-text notes are an *additional* optional field, never the primary input.
- The same button row appears in the web UI, the Discord bot, and the overlay — the overlay is the
  case that makes buttons mandatory rather than merely preferable (M3 §6.3).

---

## 7. Ban reports

A ban is the only action with real friction, because it is the only one that is costly to get wrong
and rare enough to afford the cost.

Before submission, Modbot **pre-fills what it already knows**: prior kicks, warns and bans with their
classifications and issuing moderators; join date; time in instances; linked Discord account; prior
display names. The moderator confirms a case rather than composing one from memory.

Required: classification, and a written justification. Optional: evidence references (audit log
entries, instance sessions, message links).

The report is a fact, permanently retained under the **Moderation** retention class (foundation
§5.5) — it outlives the raw presence data that informed it.

---

## 8. Accountability

### 8.1 Context at the moment of action

Foundation §5.8.4: when a moderator opens an action on a user, Modbot shows prior actions across all
moderators and instances — *"fourth kick in 30 days, by three different moderators"* — before the
action is taken. This is the moment the information is worth having and costs nothing to show.

It also runs the other way: if **no** other moderator has ever actioned this user and the current
moderator has repeatedly, that is shown too, to the moderator themselves. A quiet nudge at the point
of action prevents far more than a ticket after the fact.

### 8.2 Tickets

Foundation §5.8.5 governs detection. M4 adds the workflow:

- A flagged pattern opens a ticket **visible to group owners**, and asks the moderator to explain.
  It is a prompt, never a sanction, and Modbot never auto-punishes a moderator.
- **Auto-resolution**: where consistent classifications corroborated by other moderators fully
  explain the pattern, the ticket closes without ever being shown. This is the mechanism that makes
  the optional classification worth a moderator's second.
- Resolutions are recorded as facts — "reviewed, found legitimate" is itself history.
- Notifications route at `Warning` severity (foundation §4.5).

### 8.3 Permissions

New permission flags: `Kick`, `Ban`, `Unban`, `Warn`, `BulkAction`, `ReviewTickets`, `EditClassifications`.
`ReviewTickets` is deliberately separate — the people being reviewed should not be the people closing
the reviews.

---

## 9. Reversal

Unban, revoke a warning, and withdraw a ticket. Reversals are **new facts, never deletions** — the
original action and its reversal both stand, because "this ban was overturned" is exactly the kind of
history that matters later.

A reversal carries its own optional classification (mistake, appeal upheld, sentence served) and
inherits the same accountability treatment.

---

## 10. Non-goals

- Automated moderation. Modbot does not ban anyone by itself at any point in M4. Detection surfaces
  things for humans; humans act. (Automated flagging arrives at M8 and is still advisory.)
- Appeals as a user-facing portal. Reversal exists; a public appeals workflow does not.
- Cross-group action. Federation is M8.
- Editing or deleting facts. The log is append-only.

---

## 11. Open questions

1. ~~**Which actions VRChat's group API actually exposes**~~ — **settled for three of them
   (2026-09-16).** Group kick, ban and unban exist and are used: `KickGroupMember`,
   `BanGroupMember`, `UnbanGroupMember`. Role grant and revoke, and instance kick, are still open.
   The maintainer set the rate limit for the three at **one request per two seconds, shared, on a
   lane of their own and marked not measured** — a deliberately low guess, not a finding, to be
   replaced when somebody asks VRChat the real number (§12).
2. **Whether instance kick requires presence in the instance**, which would make it an overlay-first
   feature rather than a web-first one.
3. **Warn delivery when no Discord link and no moderator present** — queue until next seen, or record
   silently? Leaning record-silently, since the record is the primary purpose.
4. **Ticket thresholds** and their defaults, which foundation §5.8.5 requires to be conservative and
   asymmetric. Needs real data from M2.5 detection running before numbers are chosen.

---

## 12. What was built on 2026-09-16

Kick, ban and unban, one person at a time, from the web app. Not warn, not bulk, not roles.

### 12.1 The endpoints

`POST /api/moderation/kick`, `/ban` and `/unban`, gated on `Kick`, `Ban` and `Unban` — the flags
§8.3 reserved when the bitfield was written, now real. Each takes the person's VRChat id, the
confirmation's key, the reasons picked from the group's ban reason list, and an optional note. The
id travels in the body, never the path: a route constraint on it would be a format check, which
foundation §3.1.1 forbids.

### 12.2 Through the gate, on a budget of its own

A new endpoint class, `groups.moderate`, on its own lane, resource-scoped to the group, counted
against the global backstop, at **one request per two seconds**. **Not measured** — see §11.1. All
three actions go through `IVRChatGate` at interactive priority, using the `…WithHttpInfoAsync`
overloads, and a 429 is a cold stop that is never retried: the action failed, and the moderator is
told so.

### 12.3 What is recorded

`modbot.action.kick`, `.ban` and `.unban` on success, and `modbot.action.failed` on a refusal.
Subject is the person; actor is the Modbot account of the moderator who pressed the button, which is
the only place that attribution exists (§5.9.1 — VRChat records everything Modbot does as Modbot).
Payload carries the action, the reasons, the note and the key.

A failure is its own type rather than a flag on the others, so no query for "who was banned" can
count an attempt that did not happen. The audit log shows all four as moderation history about the
person, and the Discord event routes list them under Moderation.

A successful ban writes its case file straight away, citing the fact id of the ban it is the
write-up of rather than waiting for the audit log to publish the ban and then matching them up. This
is the mechanism §9 of the ban case files design said would exist "when Modbot performs bans": the
report is made *with* the ban rather than chased up afterwards.

### 12.4 What Modbot stores

A kick or a ban marks the `group_member` row as left; a ban writes or revives the `group_ban` row;
an unban marks it lifted. Deliberately the same marks the sweeps themselves use, and nothing more —
a sweep that lists the person again clears the mark by its ordinary rules. If VRChat did not really
do it, the tables go back to the truth without anybody intervening.

### 12.5 Safety

- **Never the service account.** Kicking or banning the account Modbot signs in as would take away
  the access every sync depends on, from inside the thing doing the kicking. Refused before
  anything is sent.
- **Never an empty id.** A ban with no person would reach VRChat as a request against the group.
- **A ban always needs a reason** (§6); a kick or an unban needs one only where
  `RequireModerationClassification` is on. That setting is read but has no control in the web app
  yet, so today it is off everywhere.

### 12.6 Still open

Warn, bulk actions, role changes, instance kick, reversal classifications (§9), and the
accountability context at the moment of action (§8.1). The reason list is the ban list for now,
which is open question 2 of the ban case files design.
