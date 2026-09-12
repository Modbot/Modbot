# Modbot M6 — Instance Launching & Monitoring

- **Date:** 2026-09-11
- **Status:** Draft, awaiting review
- **Covers:** M6 — launching group instances, watching the instance list, Discord announcements and live sync
- **Depends on:** M0 (`IVRChatGate`, `INotifier`), M3 (presence facts), M5 (Discord linking)

---

## 1. What M6 adds

Launching a group instance works today — manually, in VRChat's UI, followed by someone pasting a
link into Discord and someone else editing it when the instance dies. M6 automates the surrounding
work, which is where the friction actually lives.

Three capabilities:

1. **Launch** instances with chosen settings, and auto-invite staff.
2. **Watch** the group's instances and record their lifecycle as facts.
3. **Announce** openings and closures in Discord, and keep a live message in sync with activity.

This is squarely **convenience over capability** (foundation §14.1), which is why it sits here rather
than earlier. Nothing is lost by its absence except staff time.

---

## 2. Launching

### 2.1 What the operator chooses

Instance type and region, the world, the group role gating where applicable, and whether staff are
auto-invited. Saved as **presets**, because a group running a weekly event launches the same shape of
instance every week and re-entering it is pure tax.

### 2.2 Auto-inviting staff

On launch, Modbot invites the configured staff list. Invites are writes: they pass through
`IVRChatGate` at interactive priority, serialised at the configured rate, with visible per-person
progress. Failures are individually reported — "13 of 15 invited, 2 failed" — never collapsed into a
single success or a single failure.

Per foundation §4.3.4, **the invite endpoint's rate limit must be confirmed before this is built.**
Inviting fifteen people is fifteen writes in quick succession, which is the burstiest thing Modbot
will ever do, and it is exactly the shape most likely to trip an opaque limit.

### 2.3 No hardcoded capacity

Foundation §3.1. Instance capacity displayed at launch and in monitoring is read from the API. A group
with a raised exemption must never see "80" anywhere.

---

## 3. Monitoring

A polling job under the standard budget-derived scheduler (foundation §4.2), emitting facts:

| Fact | Notes |
|---|---|
| `InstanceOpened` | Includes world, type, region, and who opened it where available |
| `InstanceClosed` | Duration derived from the pair |
| `InstancePopulationSampled` | Periodic gauge — see §3.1 |

### 3.1 Population samples are counted, not evented

A population sample every minute per instance is high-cardinality and individually worthless — nobody
asks "how many people were in the instance at 14:32". Foundation §5.2.1 applies: these increment a
rollup and write no fact.

Actual join and leave events already come from the client (M3) at far better fidelity than polling
could achieve. Polling exists to catch instances **no moderator is in**, which is precisely the gap
client-sourced presence cannot cover.

### 3.2 The two sources are complementary, and must not double-count

Where a moderator's client is present, client facts are authoritative — they are per-user and
precisely timed. Where none is present, polled samples are all there is, and they are coarse.

The dedup and precision rules of foundation §5.3 carry this: polled observations set `occurred_before`
to reflect the polling interval's uncertainty, and never overwrite a precisely-timed client fact.
Getting this wrong inflates time-spent metrics, which is the same silent-corruption failure mode as
M3 §5.1 and gets the same test treatment.

---

## 4. Discord integration

### 4.1 Announcements

An instance opening posts to a configured channel: world, type, capacity, join link. Closure updates
or removes it, per configuration.

#### 4.1.1 Instance ids and names are hostile input

**A group can set an instance id to arbitrary text** — usually a VRChat-assigned number, but commonly
a readable string set through the API via VRCX. VRChat's newer instance *naming* feature adds a
second free-text field. Both are attacker-influenced and both end up rendered in a Discord message.

An instance id containing `@everyone` would ping the entire server, every time that instance opens
and on every live-message edit (§4.2). Markdown can spoof formatting; bidi-override characters can
reorder displayed text; a very long value can blow out the embed.

So, everywhere either value is displayed:

- **Discord** — send with `allowed_mentions` suppressing everything, escape markdown, cap length.
  Mention suppression is the load-bearing one: escaping alone does not stop `@everyone`.
- **Web UI** — rendered as text, never interpreted; length-capped; bidi-override characters stripped.
- **Overlay** — length-capped so one long value cannot push a card off-screen.

This is the only place in Modbot where text controlled by a non-member reaches a broadcast surface
automatically, which is why it gets called out rather than left to general good practice.

#### 4.1.2 Id is identity, name is display

Modbot keys instances on `worldId` + `instanceId` — instance ids are unique within a world, not
globally, so neither alone is sufficient.

The **name** (from the instance JSON) is display only: mutable, frequently absent, not unique.
Display preference is name → instance id → world name, and **nothing is ever keyed on the name.**

Facts record `world_id`, `instance_id` and the non-secret qualifiers. The raw location string is
never stored, because for non-group instances it carries `~nonce(…)` — the instance secret. Full
grammar in `.agent/research/vrchat-log-format.md`.

### 4.2 The live message

One message per instance, edited in place as population changes, so members can see activity without
joining to find out.

**Edit rate is the design problem.** A naive implementation edits on every population change and is
rate-limited by Discord within minutes. So: a minimum edit interval, coalesced updates, and
suppression of edits that do not change what a reader sees. This is the same attention-and-budget
reasoning as foundation §4.5.1 — the constraint is different, the discipline is identical.

### 4.3 Stale message recovery

Instances end untidily: Modbot restarts, the API stops reporting an instance, a message is deleted by
hand. On startup, Modbot reconciles its tracked messages against reality and closes out anything
orphaned. A permanently stale "42 people online!" message for an instance that closed last Tuesday is
worse than no message at all, because it teaches members not to trust the channel.

---

## 5. Notifications

Through `INotifier` (foundation §4.5):

| Event | Severity |
|---|---|
| Launch failed | Warning |
| Instance closed unexpectedly while populated | Warning |
| Instance opened / closed normally | Info — digest only |

---

## 6. Non-goals

- Scheduled or recurring automatic launches. Presets reduce the work; Modbot does not open instances
  unattended. An instance opening with no staff present is a moderation problem, not a feature.
- Capacity management or queueing.
- Managing instances belonging to other groups.
- Anything requiring a client-side hook to create instances. Launching uses the API only.

---

## 7. Open questions

1. **Which instance types and settings the group API exposes**, and whether custom instance ids are
   settable via API or only via the client.
2. **Invite endpoint rate limit** (§2.2). The burstiest write path in the product.
3. **How closure is detected** — an explicit signal, or absence from the list? If absence, the
   `occurred_before` window is the full polling interval and must be recorded as such.
4. **Whether the client (M3) can report instance lifecycle more cheaply than polling** when a
   moderator is present. If so, polling becomes the fallback rather than the primary, which would
   materially reduce the request budget this milestone consumes.
