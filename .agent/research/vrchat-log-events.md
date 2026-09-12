# VRChat log — event catalogue

- **Source:** `output_log_2026-09-03_20-26-45.txt` — 17,123 lines, VRChat on Steam (AppID 438100), server env `Release, bf0942f7`
- **Analysed:** 2026-09-11
- **Companion:** `vrchat-log-format.md` (instance id grammar, routing)

Everything below is **confirmed from a real log**, not inferred. Line shapes marked *unverified* were
not present in this sample.

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
OnPlayerLeft hevy1015 (usr_…)
Unloading scenes
```

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

This maps directly onto foundation §5.3's precision model. And the deduplication rules already
handle the cross-moderator case correctly: if moderator A was present from the start and saw B
arrive precisely, A's exact `InstanceJoined` **supersedes** C's later `InstancePresenceObserved` for
the same person — same event, better source.

That is the payoff for having built precision into the schema before knowing this problem existed.

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

- **Never split on whitespace.** Anchor on the trailing `(usr_<uuid>)` at end of line and take
  everything between the event name and it as the display name.
- **Match the *last* `(usr_` occurrence**, because a display name could itself contain text shaped
  like `(usr_something)`. A greedy first-match parser is spoofable by anyone who can set their own
  display name.
- **Do not validate the id's shape.** Legacy VRChat ids follow no format (foundation section 3.1.1),
  so the id is "everything between the final `(usr_` and the closing `)` at end of line" -- extracted
  by delimiter, never matched against a UUID pattern.
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
| Avatars (weak) | `Switching <user> to avatar <name>` |
| Liveness (§2.2 alarm) | any `[Behaviour]` line |

**Ignored entirely:** `[IK Debug Log]`, `[VRCTracking*]`, `[OSC]`, `[API]`, `[String Download]`,
`[Image Download]`, `[EOSManager]`, `[Steam]`, and all of `[AssetBundleDownloadManager]` — about
96% of the file.

---

## 7. Still unverified

- **Unclean exit.** This sample ends with a clean `Client invoked disconnect.` Whether a crash
  produces any leave marker at all is still open (M3 open question 2). Without one, the departure
  burst rule of §3.4 has no anchor and the session needs a server-side timeout.
- Log rotation and file naming; behaviour across multiple concurrent VRChat sessions.
- `~hidden(usr_…)` and `~canRequestInvite` qualifiers (not present here; `~private`, `~friends`,
  `~group` all confirmed).
- Whether `groupAccessType` takes values beyond `public` and `members`.
- Instance *name* (the mid-2026 API field) does not appear in logs at all — API-only, as expected.
