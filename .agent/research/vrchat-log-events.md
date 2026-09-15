# VRChat log — event catalogue

- **Source:** `output_log_2026-09-03_20-26-45.txt` — 17,123 lines, VRChat on Steam (AppID 438100), server env `Release, bf0942f7`
- **Analysed:** 2026-09-11; **re-verified against the fixture 2026-09-12**
- **Fixture:** `fixtures/behaviour-only-2026-09-03.log` (the 754 `[Behaviour]` lines)
- **Companion:** `vrchat-log-format.md` (instance id grammar, routing)

Everything below is **confirmed from a real log**, not inferred. Line shapes marked *unverified* were
not present in this sample.

> **Revised 2026-09-12.** The first pass got §7 backwards and undercounted the noise in §4. Both are
> corrected below, and every claim in this file has now been re-checked by grepping the fixture
> rather than by reading the earlier notes. Where a count appears, the command that produced it
> should reproduce it.

---

## 1. Line format

```
2026.09.03 20:27:14 Debug      -  [Behaviour] OnPlayerJoined bin¹ (usr_f2049d71-…)
└──── timestamp ───┘ └ level ┘    └─ tag ─┘ └──────────── message ──────────────┘
```

- Timestamp: `yyyy.MM.dd HH:mm:ss`, **local time, no timezone or offset recorded.**
- Level: `Debug` / `Warning` / `Error`, space-padded.
- Separator: `-` surrounded by spaces.
- Tag: `[Behaviour]`, `[API]`, `[String Download]`, etc.

> **Local time with no offset is a real problem.** Two moderators in different timezones produce
> identical-looking timestamps for different instants, and a DST transition shifts them by an hour
> mid-session. This is exactly what foundation §4.4's `IModbotClock` exists for: the client converts
> using its own offset and reports in server time, and the server records `observed_at` independently
> as the ordering authority.

### 1.0 The file has a heartbeat, and it is not `[Behaviour]`

**Measured 2026-09-13 against the full 16,545-line log, not the `[Behaviour]` subset.**

| Largest gap between consecutive lines | |
|---|---|
| **Any tag** | **11 seconds** |
| `[Behaviour]` only | **2,683 seconds — 44.7 minutes** |

A factor of 244, and it is the difference between a liveness check that works and one that cannot.

`[IK Debug Log]` emits a frame-rate line roughly every ten seconds for as long as VRChat is running,
which is why it is 14,491 of the lines in §1.1's table and why Modbot otherwise ignores every one of
them. The consequence is worth stating precisely:

- **Silence across all tags means VRChat has stopped.** The file only stops growing when the process
  does. This is the liveness signal.
- **`[Behaviour]` silence means nothing at all.** §7's "45-minute gap" is a gap in `[Behaviour]`
  lines while the moderator was demonstrably still in an instance — the file was being written to
  continuously throughout it, eight or more lines a minute.

§6 previously listed *"any `[Behaviour]` line"* as the liveness signal. That is wrong, and wrong in a
way that fails quietly in both directions: a moderator sitting in a quiet instance would be reported
as a log Modbot no longer understands, and a client that exits uncleanly would never be detected at
all, because the thing being watched had already been silent for three quarters of an hour.

**Reproduce it:** parse the leading `yyyy.MM.dd HH:mm:ss` off every line of a full log, sort, and
take the largest delta between consecutive entries — first across all lines, then across
`[Behaviour]` lines only.

### 1.1 Tag volume in this sample

| Tag | Lines | Modbot interest |
|---|---|---|
| `[IK Debug Log]` | 14,491 | none |
| `[VRCTrackingManager]` | 13,836 | none |
| **`[Behaviour]`** | **754** | **everything Modbot needs** |
| `[API]` | 14 | none (own client's HTTP) |
| everything else | ~1,200 | none |

**Modbot parses `[Behaviour]` lines only.** That is ~4% of the file, and it is a useful thing to be
able to say plainly in the client's privacy documentation (foundation §3.2).

---

## 2. Event catalogue

### 2.1 Session and startup

| Line | Meaning |
|---|---|
| `Using server environment: Release, <build>` | VRChat build id |
| `Launching with args: …` | process args |
| `- avatar: avtr_…` | own avatar at startup (header block) |

### 2.2 Instance transition — local user

In observed order:

| # | Line | Notes |
|---|---|---|
| 1 | `Destination requested: <worldId>` or `<location>` | intent |
| 2 | `Destination fetching: <location>` | |
| 3 | `Destination set: <location>` | **full location string — the routing input** |
| 4 | `OnPlayerLeftRoom` / `OnLeftRoom` | see §3 |
| 5 | `Unloading scenes` | |
| 6 | `Switching to network region us (current state: …)` | region change |
| 7 | `Entering Room: <world display name>` | **world name, not id** |
| 8 | `Joining <location>` | **full location string again** |
| 9 | `Joining or Creating Room: <world display name>` | |
| 10 | `Successfully joined room` | |
| 11 | `Room instantiate took <n>s` | |
| 12 | `Finished entering world.` | |

### 2.3 Player presence

| Line | Carries | Notes |
|---|---|---|
| **`OnPlayerJoined <displayName> (usr_…)`** | name **and id** | the primary presence event |
| **`OnPlayerLeft <displayName> (usr_…)`** | name **and id** | |
| `OnPlayerJoinComplete <displayName>` | name only | no id — not usable alone |
| `OnPlayerEnteredRoom` | nothing | precedes a genuine single arrival |
| `OnPlayerLeftRoom` | nothing | precedes a genuine single departure |
| `OnLeftRoom` | nothing | **the local user left — §3** |
| `Initialized PlayerAPI "<name>" is local` | name | **identifies the local user** |
| `Initialized PlayerAPI "<name>" is remote` | name | everyone else |

### 2.4 Avatars

| Line | Carries |
|---|---|
| `Switching <displayName> to avatar <avatarName>` | **avatar display name only — no `avtr_` id** |
| `Avatar is Ready` | nothing useful |
| `[AssetBundleDownloadManager] Download for avatar (Worn:0 Dist:30.75) (48.2 MB) …` | size and distance, **no id, no user** |

### 2.5 Disconnect

| Line | Meaning |
|---|---|
| `Client invoked disconnect.` | deliberate |
| `OnDisconnected: DisconnectByClientLogic` | reason string |
| `showing disconnect reason` | UI |

> **These lines appear at *startup*, not at exit.** In the fixture both sit at 20:26:56, before the
> first world was ever loaded — VRChat tearing down a connection it had just made. The session ends
> 64 minutes later with no disconnect line of any kind. Do not read `Client invoked disconnect.` as
> "the user quit"; see §7.

### 2.6 Object teardown — undocumented until 2026-09-12

| Line | Count in fixture | Notes |
|---|---|---|
| `Destroying <displayName>` | 21 | **name only, no id.** Emitted as a player object is torn down |
| `Initialized player <displayName>` | 18 | name only; distinct from `Initialized PlayerAPI "<name>"` |
| `Loading avatar for <displayName>` | 18 | name only; precedes the `Switching` line |

`Destroying` matters out of proportion to how dull it looks: **at an unclean exit it is the only
departure signal in the log.** The fixture's final two lines are `Destroying -winter~` and
`Destroying bin¹`, with no `OnPlayerLeft` and no `OnLeftRoom` anywhere near them.

It is not a substitute for `OnPlayerLeft`, because it carries no `usr_` id — and §5.1 forbids keying
anything on a display name. It is a *liveness* signal, not a presence one.

---

## 3. The phantom burst problem — **the most important finding**

**VRChat emits join and leave events for people who did not join or leave.** Both instance
boundaries produce a burst of events for everyone *already present* or *still present*.

### 3.1 On arrival — everyone already there "joins"

```
Successfully joined room
Switching bin¹ to avatar …          ← 8 avatar lines, one per occupant
Switching ΛƧƬΛ to avatar …
  …
OnPlayerJoinComplete ΛƧƬΛ           ← 8 complete lines
  …
OnPlayerJoinComplete bin¹
Finished entering world.
OnPlayerJoined ΛƧƬΛ   (usr_…)       ← 8 JOIN events for people already in the room
OnPlayerJoined -winter~ (usr_…)        for potentially hours
  …
OnPlayerJoined bin¹   (usr_…)       ← LOCAL USER LAST — this terminates the burst
Initialized PlayerAPI "ΛƧƬΛ" is remote
Initialized PlayerAPI "bin¹" is local
```

### 3.2 On departure — everyone still there "leaves"

```
Destination set: <new location>
OnPlayerLeftRoom
OnPlayerLeft -winter~ (usr_…)       ← a genuine departure, seconds earlier
OnLeftRoom                          ← LOCAL USER LEAVES — burst marker
OnPlayerLeft ΛƧƬΛ  (usr_…)          ← 11 LEAVE events for people who are still there
OnPlayerLeft BlackIndium (usr_…)
  …
OnPlayerLeft hevy1015 (usr_…)        ← never had an OnPlayerJoined at all
Unloading scenes
```

#### 3.2.1 A departure burst can name someone who never logged an arrival

`hevy1015` appears in the fixture exactly four times — `Loading avatar for` and
`Initialized player` at 20:45:27, then `OnPlayerLeft` and `Destroying` at 20:45:29. There is **no
`OnPlayerJoined hevy1015` anywhere in the file.** They entered roughly two seconds before the local
user left, and the arrival never made it to the log.

**Anything that pairs a leave with a preceding join breaks here**, and breaks silently: a parser
holding open sessions keyed on join will either drop the leave or, worse, attribute it to whoever
it does find. The session model has to tolerate a departure for somebody it never saw arrive.

### 3.3 Why this cannot be ignored

Treating phantom events as real would:

- **Inflate join and leave counts** by the instance population, every time any moderator enters or
  exits. With 4–6 moderators cycling through a busy instance, the noise dwarfs the signal.
- **Destroy time-spent**, which is the metric M7 giveaways and regulars detection are built on. A
  person present for three hours would be recorded as having arrived when the moderator did.
- Do it **silently**. Nothing errors. The numbers are simply wrong — the recurring failure mode of
  this whole subsystem.

### 3.4 The markers that make it solvable

Both bursts are cleanly delimited:

| Boundary | Rule |
|---|---|
| **Arrival** | Every `OnPlayerJoined` from `Joining <location>` up to **and including the local user's own** is the initial roster, not an arrival. Subsequent ones are genuine. |
| **Departure** | Every `OnPlayerLeft` **after `OnLeftRoom`** is a phantom. Ones before it are genuine. |
| **Local identity** | `Initialized PlayerAPI "<name>" is local` gives the local display name; matching it in the join burst yields the local `usr_…`. Established once and remembered. |

Note `OnLeftRoom` (local user left) is **distinct from** `OnPlayerLeftRoom` (a remote player left).
One character of difference, opposite meanings. Mixing them up inverts the entire rule.

### 3.5 What Modbot should emit

Two different fact types, because they carry different certainty:

| Situation | Fact | Precision |
|---|---|---|
| Observed someone arrive while already present | `InstanceJoined` | exact `occurred_at` |
| Someone was already present on arrival | `InstancePresenceObserved` | present *at* this time; **arrival time unknown and earlier** |
| Observed someone leave while still present | `InstanceLeft` | exact |
| Still present when the local user left | *(nothing — the session simply stops being observed)* | |

This maps directly onto foundation §5.3's precision model.

> **Corrected 2026-09-14.** This section used to say that moderator A's exact `InstanceJoined`
> **supersedes** a later moderator C's `InstancePresenceObserved` for the same person. It does not,
> and nothing was ever built to make it. `FactWriter`'s duplicate check matches on the fact type as
> well as the person, room and ±5 s window, so an `InstanceJoined` and an `InstancePresenceObserved`
> are never duplicates of each other and **both are stored**.
>
> That is harmless, and it was decided to leave it that way rather than build the replacement:
>
> - **Rosters** read both types as "present", so a second fact about somebody already present
>   changes nothing.
> - **Time sums** treat both as the start of a presence, and a later "already here" inside a stay
>   that began with an exact arrival adds no time.
> - **The Live page** shows "arrived <time>" or "here before <time>" from the *first* fact of a
>   person's current stay, so A's exact arrival is what a moderator sees even when C's snapshot
>   is also on record.

---

## 4. Avatar ids are absent from the log **by design**

```
[Behaviour] Switching CODYYYYYYYYYYYY to avatar Pengus
                                                ^^^^^^ display name, not avtr_…
```

The only `avtr_` ids in this 17k-line sample are the local user's own in the header block, and
`[API]` **404s** for avatars that failed to load. Nothing associates a remote user with an avatar id.

**This is deliberate on VRChat's part.** Avatar ids are withheld from clients to frustrate avatar
ripping. It is not an oversight and will not be fixed; no amount of log parsing will produce one.

### 4.0 `Switching … to avatar` is mostly noise — **82% of it, in the fixture**

Neither this document nor its companion said so before 2026-09-12, and the §3.1 excerpt above
quietly shows it: the "8 avatar lines, one per occupant" inside the arrival burst are **not
changes**. They are what those people were already wearing.

VRChat also re-emits the *same* line two or three times for a single real change. Classifying each
of the fixture's 33 `Switching` lines against the wearer's previously-known avatar:

| | Count |
|---|---|
| Re-assertion of the avatar already worn | **15** |
| First sight — the roster's current avatar, arrival-burst or otherwise | **12** |
| **Genuine changes** | **6** |

Six real events in thirty-three lines. Recording them naively inflates avatar-change counts exactly
the way §3 inflates join counts, for the same reason, and just as silently.

**The rule:** a `Switching X to avatar Y` is a change only when `Y` differs from the last avatar
known for `X`. First sight is initial state — the avatar analogue of `InstancePresenceObserved` —
and a repeat is nothing at all. Per-wearer state, not consecutive-line deduplication: collapsing
only adjacent duplicates leaves 23 of the 33, still nearly four times the real count.

### 4.1 What the client *can* see

An avatar **display name**, and a **thumbnail file id** (`file_…`). The file id cannot be exchanged
for avatar information through VRChat's own API.

### 4.2 Resolution is server-side, through a third-party database

The client reports file id and display name; the **Modbot server** resolves identity through a
VRCX-compatible community avatar database. See M3 §7.2–§7.3 for the provider list, capability flags,
caching, rate limiting and disclosure rules.

This unblocks M4 §3.2's ban-by-avatar-id, which the earlier reading of this section had assessed as
blocked. The earlier conclusion was right about the log and wrong about the ceiling.

### 4.3 Still to confirm

The `Switching <user> to avatar <name>` line carries **no file id**. Whether a per-wearer `file_…`
appears elsewhere — a different line, or only under the full flag set of M3 §2.3.0 — is **open and
blocking for M3** (open question 13). Without a per-wearer file id the server has only an avatar
display name to resolve from, which is substantially weaker: author-set, non-unique, and freely
renamed.

## 5. Parsing hazards

### 5.1 Display names are hostile input

Observed in this one sample: `~ RedZu ~`, `ΛƧƬΛ`, `bin¹`, `-winter~`, `Hawk Echos`, `-Traceless-`.

Names contain **spaces, tildes, hyphens, superscripts, and non-Latin scripts**. So:

- **Never split on whitespace.** Anchor on the parenthesised id at end of line and take everything
  between the event name and it as the display name.
- **Match the *last* `(`**, because a display name could itself contain parentheses -- and one of
  the names in this very sample, `~ RedZu ~`, shows how little display names respect delimiters. A
  greedy first-match parser is spoofable by anyone who can set their own display name.
- **Do not validate the id's shape, and that includes the `usr_` prefix.** Legacy VRChat ids follow
  no format at all (foundation §3.1.1), so anchoring on the literal `(usr_` is itself a shape
  assumption -- it would silently drop a legacy account with no prefix, which is exactly the
  long-standing member a moderation tool least wants to lose.

  > **Corrected 2026-09-12.** This section previously said to anchor on `(usr_` while, two bullets
  > later, forbidding shape validation. Both cannot hold. The rule is: the id is everything between
  > the **last `(`** and a closing `)` that ends the line -- extracted purely by delimiter. That is
  > strictly more general than the `usr_` anchor and equally spoof-resistant, since both take the
  > last occurrence.
- Avatar names are worse — observed with full-width quotes and braces: `＂ Evur ＂ By Kaiylast ｛FT｝`.

### 5.2 No timezone

§1. Handled by `IModbotClock`, not by the parser.

### 5.3 Multi-line records exist

The `[SteamManager]` startup line wraps across lines. Parsing must tolerate lines that do not begin
with a timestamp rather than treating them as corrupt.

---

## 6. What Modbot consumes

| Purpose | Lines |
|---|---|
| Instance identity and routing | `Destination set:` / `Joining <location>` |
| Presence | `OnPlayerJoined` / `OnPlayerLeft` |
| Burst delimiting | `OnLeftRoom`, `Initialized PlayerAPI … is local` |
| Avatars (weak) | `Switching <user> to avatar <name>` — **filtered per §4.0; 82% is noise** |
| Unclean-exit detection | `Destroying <name>` (§2.6) — carries no id |
| **Liveness** | **whether the file is still growing at all.** Not `[Behaviour]` lines, which this table wrongly named until 2026-09-13 — see §1.0 |

**Parsed:** `[Behaviour]` only — about 4% of the file.

**Never parsed:** `[IK Debug Log]`, `[VRCTracking*]`, `[OSC]`, `[API]`, `[String Download]`,
`[Image Download]`, `[EOSManager]`, `[Steam]`, and all of `[AssetBundleDownloadManager]` — the
other 96%.

Note the distinction §1.0 turns on: **their contents are never read, but the fact that they keep
arriving is.** `[IK Debug Log]` alone is 14,491 of the lines here and Modbot cares about none of
them individually — yet it is what keeps the file growing while a moderator sits in a quiet
instance, and so it is what separates "VRChat is running" from "VRChat has exited". Ignoring what a
line says is not the same as ignoring that it exists.

---

## 7. Unclean exit — **confirmed, and it is the case this sample ends in**

> **Corrected 2026-09-12. The previous version of this section was wrong.** It said "this sample
> ends with a clean `Client invoked disconnect.`" and filed unclean exit as unverified. The opposite
> is true, and the evidence was already in the fixture.

The only `Client invoked disconnect.` in the file is at **line 58, 20:26:56** — during startup,
before the first world was ever loaded (§2.5). The session then runs for just over an hour and the
log simply **stops**:

```
2026.09.03 20:45:50  Initialize ThreePoint Avatar VRCPlayer[Remote] 1 False 8
2026.09.03 21:30:33  Destroying -winter~
2026.09.03 21:30:33  Destroying bin¹          ← last line in the file
```

A 45-minute gap, then two teardown lines, then nothing. No `OnLeftRoom`, no `OnPlayerLeft`, no
disconnect line. The last `OnLeftRoom` is at 20:45:29, four world-changes earlier.

> **That gap is `[Behaviour]` silence, not file silence (§1.0).** The log was being written to
> throughout it — at least eight lines a minute, with no all-tag gap anywhere in the session longer
> than eleven seconds. Reading this section as "the log went quiet" is the mistake it most invites,
> and it is what makes "has this session ended?" answerable at all: the file stops growing when
> VRChat stops, and at no other time.

**Three consequences, none of them optional:**

1. **§3.4's departure rule has no anchor here.** It keys on `OnLeftRoom`, which never comes. Every
   person still present at 21:30:33 has an open session that the log never closes.
2. **Server-side session timeout is required, not speculative.** M3 open question 2 is answered: the
   server must close sessions that stop being reported, because the client cannot always know they
   ended. A tool that waited for a clean marker would carry those sessions forever and count
   `-winter~` as present for months.
3. **`Destroying` is the only hint**, and a weak one — no id (§2.6), and it also fires during normal
   instance changes, so its presence does not by itself mean the session ended.

This is the ordinary case, not an edge case. Users close VRChat, VRChat crashes, machines sleep. A
clean shutdown is the exception the parser should be surprised by.

---

## 8. Still unverified

- Log rotation and file naming; behaviour across multiple concurrent VRChat sessions.
- `~hidden(usr_…)`, `~canRequestInvite` and `~nonce(…)` — **none appear in this sample**, including
  in its two non-group instances, which is itself a finding (see `vrchat-log-format.md` §1.5).
- Whether `groupAccessType` takes values beyond `public` and `members`.
- Whether a per-wearer `file_…` id appears anywhere (§4.3) — still open and still blocking for M3.
- Instance *name* (the mid-2026 API field) does not appear in logs at all — API-only, as expected.
- **What a genuine clean exit looks like.** Now that §7 has established this sample is not one, no
  observed sample shows a clean shutdown at all. The clean path is the unverified one.
