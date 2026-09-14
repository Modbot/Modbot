# VRChat instances and worlds — what the API actually answers

- **Probed:** 2026-09-13, against the live API with a group-moderator account.
- **Group:** `grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd`
- **Raw bodies:** `explore/responses/GetInstance-20260913-192443.json` (invented),
  `GetInstance-20260913-192519.json` (real), `GetGroupInstances-20260913-192509.json`
- **Reproduce:** `dotnet run --project explore -- instance <location>` / `-- instances`

Everything below is **confirmed from a live response**, not inferred from the SDK's types.

---

## 1. `GET /instances/{location}` answers 200 for an instance that has never existed

This is the finding that matters, and it is a trap, because the obvious way to ask "is this room
still alive" is to fetch it.

The same request was made twice: once for a room the group genuinely had open, and once for the
same world with the instance number `909991220`, which nobody has ever used.

| | real — `68681` | invented — `909991220` |
|---|---|---|
| **HTTP status** | 200 | **200** |
| `active` | `true` | **`false`** |
| `n_users` | 3 | 0 |
| `userCount` | 2 | 0 |
| `platforms` | `standalonewindows: 3` | all zero |
| `gameServerVersion` | 1626 | **absent** |
| `queueSize` | 0 | absent |
| `hasCapacityForYou` | `true` | absent |
| `shortName` | `null` | **`"9wsjhasa"`** |
| `secureName` | `"nvjc7ucu"` | `"e7g4hc5a"` |
| `capacity` | 32 | 32 |
| `ownerId` | the group | the group |
| `permanent` | `true` | `true` |
| `world` | the full world object | the full world object |

The invented instance comes back with an owner, a capacity, a region, a type of `group`, the whole
world object, and a freshly minted `shortName` — everything a real one has except a game server.
There is no 404, no error field, and nothing in the shape of the response to suggest the room is
imaginary.

**So a 200 from this endpoint means nothing.** The fields that actually differ are `active` and
the presence of `gameServerVersion`; a caller that checks the status code is checking nothing.

### 1.1 What this rules out

Modbot cannot use `/instances/{location}` to decide whether a room is still open. `active: false`
is returned both by a room that never existed and — presumably, though this has not been caught in
the act — by a real room that has emptied. The two are not distinguishable here.

**The group's own instance list is therefore the only trustworthy answer** to "which rooms exist
right now", and that is why `GroupInstanceSync` treats absence from the list as the end of a room
and never asks this endpoint about liveness.

---

## 2. `GET /groups/{groupId}/instances` is small, exact, and carries the world

The live response, with one room open:

```json
[
  {
    "instanceId": "68681~group(grp_0a17…)~groupAccessType(plus)~region(us)",
    "location":   "wrld_4432ea9b-…:68681~group(grp_0a17…)~groupAccessType(plus)~region(us)",
    "memberCount": 2,
    "world": { "id": "wrld_4432ea9b-…", "name": "VRChat Home", "authorName": …, … }
  }
]
```

Four fields, and the fourth is the **entire world object** — name, author, capacity, image, tags,
the lot. Two consequences:

1. **A group's own worlds are named for free.** The world page never has to be read for a world the
   group has had an instance in. `WorldSync` exists only for worlds met some other way — a
   moderator wandering into a public world, or a world that only ever appeared in an audit entry.
2. **`memberCount` is the group-member count, not the head count.** See §3.

### 2.1 Open question — does an empty room stay in the list?

**Not established.** No empty group instance existed during the probe, so it is not known whether
a room with nobody in it stays listed or drops off and comes back.

This matters, because "dropped off the list" is what Modbot treats as the end of a room. If a real
room can leave the list while empty and return under the same number, closing on absence would cut
one evening into two.

Modbot is built to survive either answer: a location whose last room was closed by the list, and
which is seen again within `InstanceIdentity.ReopensWithin`, resumes that same row rather than
opening a new one. If the room really had ended, a number reissued within minutes is rare and the
cost is one merged pair of short sessions; if the room was merely empty, the row is correctly
continuous. **Worth re-probing** the first time the group has a genuinely empty instance.

---

## 3. Three different numbers describe "how many people are here"

From the two responses for the same room, at the same moment:

| source | field | value |
|---|---|---|
| `/groups/{id}/instances` | `memberCount` | **2** |
| `/instances/{location}` | `userCount` | **2** |
| `/instances/{location}` | `n_users` | **3** |

`n_users` and `userCount` disagree by one in the same response body. The likeliest reading is that
`n_users` counts everybody present and `userCount` counts something narrower — group members, or
people not in a queue — but this is **one observation, and the difference is not explained.**

Modbot records `memberCount` from the list, because that is the number it polls every ten seconds
and the only one available for a room nobody is standing in. Any screen showing it should say what
it counts rather than calling it "users".

---

## 4. `Instance.Users` is empty, as the M3 spec already recorded

Confirmed again here: `userIcons` is `[]` and the `users` array is absent for a group instance the
account is not physically in. M3 §7.2.1 records why — VRChat populates it only for staff and the
world's owner — and this probe is consistent with that. The cheap path to per-user avatar data
still does not exist.

---

## 5. Still unverified

1. **Whether an empty group instance stays in the list** (§2.1) — the one that would change a
   design decision.
2. **What `n_users` counts** that `userCount` does not (§3).
3. **Whether `active` is ever `true` for a room with nobody in it**, which would make it a usable
   liveness test after all.
4. **The real rate limit on `/instances` and `/worlds`.** Both are set to 1 req/s on the
   maintainer's measurement (2026-09-13), which is a finding rather than a spec §4.3.4 guess, but
   it has not been pushed to the point of a 429.
