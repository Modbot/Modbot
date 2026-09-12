# VRChat log format — research notes

- **Started:** 2026-09-11
- **Purpose:** the empirical basis for M3's log parser (`.agent/specs/2026-09-10-m3-client-overlay-design.md` §2.1)
- **Status:** partial. Confirmed items are marked; everything else needs verification against real logs.

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
  <worldId> ":" <instanceName> ( "~" <qualifier> [ "(" <value> ")" ] )*

  worldId         wrld_<uuid>
  instanceName    39911                    arbitrary token; numeric in this sample
  qualifiers      group(grp_<uuid>)        ← THE OWNING GROUP
                  groupAccessType(members|plus|public)
                  region(use|usw|eu|jp|…)
```

### 1.3 Why this matters more than it looks

**The owning group is carried in the instance id itself.** The client can determine which group an
instance belongs to by **string parsing alone** — no API call, no server round-trip, no question
asked of anybody.

That is what makes M3 §5.5.1's cross-group boundary implementable as specified. Routing decides
locally which paired server (if any) should receive an event, so a moderator staffing two communities
leaks nothing about either to the other. Had the group required a lookup, the client would have had
to ask *some* server which group an instance belonged to — and asking the wrong one is itself the
leak the boundary exists to prevent.

It also makes §3.1's filter exact rather than approximate: **an instance id with no `~group(…)`
qualifier is not a group instance**, and is dropped before transmission. Public, friends and private
instances — a moderator's personal VRChat use — are excluded by structure, not by heuristic.

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

### 1.5 Unverified — needs checking against real logs

Expected from community knowledge, **not** confirmed by an observed sample:

| Qualifier | Expected meaning |
|---|---|
| `~private(usr_…)` | invite-only, owner |
| `~friends(usr_…)` | friends-only |
| `~hidden(usr_…)` | friends-of-guests |
| `~canRequestInvite` | invite-plus modifier |
| `~nonce(…)` | instance secret |
| *(no qualifier)* | public |

`groupAccessType(public)` is the case to watch: a **group-public** instance where non-members may
join. It still carries `~group(…)`, so it routes correctly — but the roster will contain non-members,
which M1's member cache must handle rather than assume.

---

## 2. Log events — **open, blocking for M3**

`WorldChange` gives the local user's own instance transitions. Presence tracking needs **other
players' joins and leaves**, which come from different lines.

### 2.1 The question that has to be answered first

> **Do the player join/leave lines contain the VRChat user id (`usr_…`), or only the display name?**

This is not a detail. M3 §3.1 commits to transmitting *"the VRChat user id, the instance id, and a
timestamp"*. If the log carries only display names, that commitment cannot be met as written, and the
consequences are serious:

- Display names are **mutable and not unique** (M4 §3.1 already refuses to act on a name match alone).
- The client would have to transmit display names, which is *more* personal data than user ids, not
  less — working against §3.1's minimisation argument.
- Resolution from name to id would have to happen **server-side** against M1's member cache, and
  would be ambiguous exactly when it matters: a name collision, a recent rename, or a non-member in a
  group-public instance who is not in the cache at all.
- Deduplication (§5.1) keys on subject identity. Keying on a mutable string is a correctness problem,
  not merely an inconvenience.

Recent VRChat versions are believed to have added user ids to these lines. **That must be confirmed
against a current log before the M3 plan is written**, because the answer changes the ingest contract,
the dedup key, and the privacy table in §3.1.

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
