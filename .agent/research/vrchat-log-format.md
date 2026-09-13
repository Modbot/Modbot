# VRChat log format — research notes

- **Started:** 2026-09-11
- **Purpose:** the empirical basis for M3's log parser (`.agent/specs/2026-09-10-m3-client-overlay-design.md` §2.1)
- **Status:** partial. Confirmed items are marked; everything else needs verification against real logs.
- **Fixture:** `fixtures/behaviour-only-2026-09-03.log` — every "confirmed" below is grep-reproducible from it.
- **Revised:** 2026-09-12 — qualifier statuses corrected (§1.5), valueless qualifiers added to the grammar (§1.2).

VRChat's log format is undocumented and can change without notice (M3 §2.1). This file is the record
of what has actually been observed, so a format break can be diagnosed against a known baseline
rather than from memory. **Every confirmed line shape should gain a fixture in
`tests/Modbot.Client.Tests/Fixtures/`.**

---

## 1. Instance identifiers

### 1.1 Confirmed example

Supplied 2026-09-11, observed on a `WorldChange` event:

```
wrld_4b341546-65ff-4607-9d38-5b7f8f405132:39911~group(grp_2d8cee98-2481-451b-9bb1-e0351f7190a7)~groupAccessType(members)~region(use)
```

### 1.2 Grammar

```
  <worldId> ":" <instanceId> ( "~" <qualifier> [ "(" <value> ")" ] )*

  worldId       wrld_...                 the world itself -- the Unity package
  instanceId    39911                    identifies ONE instance of that world
  qualifiers    group(grp_...)           the owning group
                groupAccessType(members|plus|public)
                region(use|usw|eu|jp|...)
                ageGate                  VALUELESS -- no parentheses at all
                private(usr_...)         invite-only, owner
                friends(usr_...)         friends-only, owner
```

**The value is genuinely optional** -- the `[ ... ]` above is load-bearing. `~ageGate` carries no
parentheses, and a parser that requires them drops the rest of the location string with it. Observed
in the fixture's group location:

```
wrld_4cf554b4-…:85019~group(grp_c7ba8659-…)~groupAccessType(public)~ageGate~region(use)
                                                                    ^^^^^^^^
```

A world has many instances. The world id identifies the content; the instance id identifies one
running session of it.

### 1.3 The instance id is arbitrary, user-controlled text

`39911` is an **instance id**, not a name. It is usually a random number assigned by VRChat, but it
**can be any text**, and groups routinely set it to something readable through the API -- commonly
via VRCX.

Three consequences follow, and the second is a security finding.

#### 1.3.1 Identity is `worldId` + `instanceId`, and neither alone

Instance ids are unique within a world, not globally. Anything keyed on an instance must carry both.

**Ids are opaque.** The `<uuid>` shapes above describe modern ids only -- VRChat changed its format
years ago and legacy ids follow no structure. Parsing matches the **delimiters** (`group(` ... `)`,
`:` between world and instance) and never an expected id shape. See foundation section 3.1.1.

#### 1.3.2 It is untrusted input, and it reaches Discord

Because a group can set the instance id to arbitrary text, it may contain Discord mentions
(`@everyone`), markdown, HTML, zero-width or right-to-left override characters, or be extremely long.

**M6 §4.1 announces new instances into a Discord channel, and M6 §4.2 keeps a live message updated
with the instance id in it.** Rendering that text unescaped is a mention-injection vector: anyone who
can open a group instance could ping an entire server, or spoof message formatting.

The instance id must therefore be treated as hostile input everywhere it is displayed:

- **Discord** -- suppress mentions via `allowed_mentions`, escape markdown, cap length.
- **Web UI** -- escaped as text, never interpreted; capped length; bidi-override characters stripped.
- **SteamVR overlay** -- length-capped so a long id cannot push the rest of the card off-screen.

This applies to instance *names* (§1.3.3) equally, and for the same reason.

#### 1.3.3 There are now two separate things: id and name

VRChat shipped **instance naming** around July 2026. The instance JSON returned by the API carries a
distinct, human-facing **name** field, separate from the instance id.

Both conventions are in active use -- groups that adopted the naming feature, and groups still
encoding a name into the instance id itself via VRCX. Modbot must handle both.

| | Instance id | Instance name |
|---|---|---|
| Source | the location string, so **present in the logs** | instance JSON from the API |
| Stability | fixed for the life of the instance | mutable display text |
| Role in Modbot | **identity** -- what facts are keyed on | **display only** |

**Display preference:** instance name, falling back to the instance id, falling back to the world
name. **Never key anything on the name.** It is mutable, may be absent, and is not unique.

#### 1.3.4 Never store instance secrets

Non-group instances are widely reported to carry a `~nonce(...)` qualifier, the instance secret —
though none appears in the only sample observed so far (§1.5.1). Modbot must **never persist it**, and non-group instances are dropped before transmission anyway (§1.4).

Facts should record `world_id`, `instance_id`, and the non-secret qualifiers (group, access type,
region) -- never the raw location string, which would carry a nonce along with everything else.

### 1.4 Routing rule

```
1. Parse the instance id from the WorldChange line.
2. Extract group(grp_…). Absent → not a group instance → DROP. Never transmitted.
3. Match the group id against each paired server's declared managed group.
4. No match → DROP. One match → route there. (Multiple matches are possible only if two
   paired servers manage the same group; route to both.)
```

The managed group id is declared by each server at pairing (M3 §4), so step 3 is a local lookup
against a list the client already holds.

### 1.5 Qualifier status — **updated 2026-09-12 against the fixture**

Confirmed means: present in `../research/fixtures/behaviour-only-2026-09-03.log`, counted.

| Qualifier | Meaning | Status | Count |
|---|---|---|---|
| `~region(…)` | server region | **confirmed** | 11 |
| `~group(grp_…)` | owning group | **confirmed** | 4 |
| `~groupAccessType(…)` | `public` / `members` observed | **confirmed** | 4 |
| `~friends(usr_…)` | friends-only, owner | **confirmed** | 4 |
| `~ageGate` | age-verified instance; **takes no value** | **confirmed** | 4 |
| `~private(usr_…)` | invite-only, owner | **confirmed** | 3 |
| `~hidden(usr_…)` | friends-of-guests | unverified | 0 |
| `~canRequestInvite` | invite-plus modifier | unverified | 0 |
| `~nonce(…)` | instance secret | **unverified — see below** | 0 |

`~private` and `~friends` were previously listed here as unverified while the companion document
already recorded them as confirmed. The two documents disagreed; this table is now the one to
trust, and it is reproducible from the fixture.

#### 1.5.1 `~nonce` did not appear, and that is worth knowing

The fixture contains **two non-group instances** — one `~friends`, one `~private` — and **neither
carries a nonce**:

```
wrld_266523e8-…:39047~friends(usr_527e5167-…)~region(use)
wrld_4432ea9b-…:69955~private(usr_f2049d71-…)~region(use)
```

This does not disprove `~nonce`; it is widely reported and probably appears when a location arrives
by invite rather than by the owner's own navigation. But §1.3.4 is written as though every non-group
location carries one, and in the only sample there is, none does.

**Nothing about the handling changes.** `~nonce` must still never be persisted, and non-group
instances are dropped before transmission anyway (§1.4) — so the secret never reaches a Modbot
server whether it is in the string or not. A rule that is load-bearing only in a case never
observed should be tested against a synthetic fixture, since a real one may be a while coming.
| *(no qualifier)* | public |

`groupAccessType(public)` is the case to watch: a **group-public** instance where non-members may
join. It still carries `~group(…)`, so it routes correctly — but the roster will contain non-members,
which M1's member cache must handle rather than assume.

---

## 2. Log events

`WorldChange` gives the local user's own instance transitions. Presence tracking needs **other
players' joins and leaves**, which come from separate lines -- reported as user-join and user-leave
events.

### 2.1 User ids are present -- RESOLVED

> **Confirmed 2026-09-11: the player join/leave lines carry both the display name and the VRChat
> user id.**

This unblocks M3. The commitments that depended on it all stand as written:

- §3.1's privacy table is accurate -- the client transmits the **user id**, not the display name.
  Ids are less identifying than names, so the minimisation argument holds rather than inverting.
- Deduplication (§5.1) keys on a **stable** identity. Had it keyed on display names, a rename
  mid-session would have split one person into two, and a name collision would have merged two into
  one -- silently, in exactly the metric giveaways and regulars detection depend on.
- No server-side name-to-id resolution is needed, which removes the ambiguity that would have bitten
  hardest for non-members in a `groupAccessType(public)` instance who are absent from M1's cache.

**Display names should still be captured opportunistically**, because the pairing of id to name at a
point in time is useful history -- M4 §7 pre-fills ban reports with prior display names, and a
moderator searching for a name someone used six months ago should find them. Recorded as fact data
alongside the id, never as identity.

Exact line shapes pending the real log sample.

### 2.2 Other events still to characterise

- Player join / leave line shapes, and whether ids are present (§2.1).
- Avatar change lines, and whether they carry `avtr_…` plus the wearing user.
- Behaviour on **unclean exit** — is there a leave line when VRChat crashes? (M3 open question 2.)
- Log rotation and file naming; multiple concurrent VRChat sessions.
- Whether the local user's own join appears as both a `WorldChange` and a player-join line.

---

## 3. How to collect the rest

1. Join a group instance, have another account join and leave, change avatar, then exit cleanly.
2. Repeat, killing VRChat uncleanly at the end.
3. Copy the log, **redact display names and user ids that are not yours**, and commit it as a fixture.
4. Record confirmed line shapes here with the date observed.

Fixtures are what make a format break a contained failure with clear diagnostics rather than a silent
data stoppage (M3 §2.2).
