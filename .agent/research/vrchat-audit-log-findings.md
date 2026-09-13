# VRChat group audit log — findings from live data

- **Source:** 638 facts written by Modbot's audit-log producer against a real ~4,700-member group,
  pulled from the live database on 2026-09-13 into `.local/live/` (gitignored — never commit it).
- **Companion:** `vrchat-sdk-findings.md` (SDK landmines), foundation spec §5.9 (the merged log).

Everything below is **observed**, not inferred. Display names are redacted; ids and counts are real.

---

## 1. What VRChat actually calls things

| Event type as VRChat sends it | Count | Modbot fact type |
|---|---|---|
| `group.invite.create` | 237 | `vrchat.group.invite.create` |
| `group.member.join` | 175 | `vrchat.group.member.join` |
| `group.member.leave` | 63 | `vrchat.group.member.leave` |
| **`group.user.ban`** | **35** | `vrchat.group.member.ban` |
| `group.member.role.unassign` | 1 | `vrchat.group.role.unassign` |
| `group.request.create` | 1 | was `modbot.unrecognised` — see §4 |

**The real ban event is `group.user.ban`.** The spelling in the OpenAPI example and in every
community reference is `group.member.user.ban`, and in 35 real bans it appeared zero times. Modbot's
mapping table had `group.member.user.ban` as the "primary" spelling and `group.user.ban` as a
"defensive alias" — which is to say the table had the truth on the wrong side, and only worked
because the alias existed. Both are mapped; neither is treated as more real than the other any more.

VRChat's own vocabulary has evidently moved at least once. Treat every event-type string as an
observation with a date, not a constant.

## 2. `data` is mostly empty, and `description` is a sentence

The OpenAPI schema says only *"the format of this data is dependent on the event type"*, with one
example (`group.update`: `{ description: {old, new}, joinState: {old, new} }`). Observed:

| Event | `data` |
|---|---|
| `group.member.join`, `group.member.leave`, `group.invite.create`, `group.user.ban` | `{}` — nothing |
| `group.member.role.unassign` | `{ "roleId": "grol_…", "roleName": "Moderator - Guard" }` |
| `group.request.create` | `{}` |

`description` is a human sentence in VRChat's own template, e.g.

```
User <name> was preemptively banned by <actor>.
User <name> has been invited to the group by <actor>.
User <name> has been added to the group by <actor>.
User <name> has left the group.
<actor> unassigned the role "<role>" from <name>
User <name> requested to join the group.
```

**There is no ban reason anywhere** — not in `data`, not in `description`. "Preemptively banned"
is VRChat's phrasing for every ban, not a classification. Anything Modbot wants to know about *why*
a ban happened has to come from Modbot's own ban report (§5.8.3), never from the audit log.

Consequence for parsing: lift `roleId`/`roleName` for role events and the `{old,new}` diff map for
`group.update` / `group.role.update`; keep `data` verbatim for everything (already done as
`auditData`); **invent nothing** for the types with no observed payload. A field guessed today is a
role id in a user-id column tomorrow.

## 3. `auditLogTypes` only tells you about the last two weeks

`GET /groups/{id}/auditLogTypes` returns the event types the group has *recently* had — reported by
the maintainer as roughly a two-week window — not the set VRChat can emit. A vocabulary check built
on it (spec §4.3.4.2, now withdrawn) would tell any group that had not banned anyone for a fortnight
that its ban mapping was broken. Combined with §1 it would also have reported the *real* ban
spelling as an unused alias. Withdrawn 2026-09-13; the endpoint is no longer called.

## 4. Record-now-understand-later works in production

One `group.request.create` entry arrived before Modbot had a mapping for it. Under the old enum it
would have been counted and dropped, and — because VRChat's audit log ages out — gone for good. It
was instead written as `modbot.unrecognised` with `type_raw = group.request.create` and its payload
intact, and it maps to a real type now with nothing lost. That is the whole argument for string fact
types (§5.3.1), confirmed on the first day.

## 5. Things that are *not* audit entries but look like them in the table

126 `vrchat.group.update` rows carry no `eventType` and `auditData: null`. Those are the
**group-info producer's** own change records, not audit-log entries. `group.update` is also a real
audit event type (the OpenAPI example), so both producers can write the same fact type; the audit
one carries `auditEntryId` and the producer one does not, which is how they are told apart and why
dedup must key on that rather than on type alone.

## 6. Still unknown

No live sample yet for: `group.calendarEvent.*`, `group.instance.*` (announcement, close, create,
kick, update, warn), `group.post.*`, `group.request.block` / `.reject`, `group.role.update`,
`group.member.user.unban` / `group.user.unban`. Their `data` shapes are unobserved. They are mapped
to fact types and stored verbatim; the moment one arrives, its shape is in `auditData` and this file
should be updated from it — not from a guess.

For `group.instance.*`, `targetId` is documented as "typically a UserID, GroupID, GroupRoleID, or
Location". It is carried through untouched and never parsed (§3.1.1).

## 7. The audit log's `offset` is hard-capped at 7,500

Reported by the maintainer, 2026-09-13, **for the audit log**. Whether members, bans, invites and
join requests share the cap is not documented and is being measured (`explore offset-probe`); do not
assume it either way until the numbers are in §7.1. `offset=7501` on the audit log returns HTTP 400
with this body — note the **fullwidth Unicode punctuation** (`＝`, `․`, `‚`, `＠`), which makes
string-matching it fragile:

```
{"error":{"message":"offset＝7501 is above the limit․ if you believe this is too low‚ please contact support＠vrchat․com with details․","status_code":400}}
```

- It is a **400, not a 429**. It is not a rate limit, it does not extend on retry, and it must not
  trigger a cold stop. The correct handling is arithmetic: never send an offset above 7,500, and
  treat reaching it as "the history horizon" — a fact about VRChat, not a failure.
- A naive "non-2xx → failed pass → retry next tick" loops at offset 7,501 forever. If a 400 arrives
  on a paginated read, it is terminal for that pass.
- **Do not extend this to other endpoints without measuring.** If members or bans turn out to share
  it, a group with more than 7,500 of either cannot be enumerated by offset and M1's sweeps need
  filters or sort windows instead. If they do not, the obvious design is fine. §7.1 will say which.

### 7.1 Measured: which other endpoints share the cap

`explore offset-probe`, 2026-09-13, against the same group, `n=1`, one request per 3.5 s, 21
requests, no 429. Each endpoint was asked for offset 7,500 and 7,501 first.

| Endpoint | 7,500 | 7,501 | Reading |
|---|---|---|---|
| group members | 200, empty | 200, empty | **No 400.** The data ends at offset **4,739** (binary-searched: 4,738 returns an item, 4,739 does not), so a cap beyond the data cannot be observed from this group — but the audit log's cap fires *past the end of the data*, and this one did not. Members do not behave like the audit log. |
| group bans | 403 | 403 | Not measured: the probing account lacks the group permission. |
| group invites | 403 | 403 | Not measured: same. |
| group join requests | 403 | 403 | Not measured: same. |

The 403 body is `{"error":{"message":"You don't have permission․","status_code":403}}` — again with a
fullwidth full stop. The account in `explore/.env` is the deployment's bot account; those three
endpoints need group role permissions it has not been granted. Re-run once it has them.

**`/members` is a complete enumeration — checked.** The list ended at 4,739 items; the group's own
`memberCount` from the group-info facts was 4,741 at the start of the day and 4,740 at the last
change, and two people joined or left while the probe ran. So `/members` returns everybody, and a
sweep built on it does not under-count. The group is therefore too small to observe a 7,500 cap on
members even if one exists; the finding that survives is narrower: **members did not 400 past the
end of their data, and the audit log does** — the two endpoints behave differently.

## 8. VRChat keeps roughly 30 days of audit log

The live catch-up finished at offset **1,233** with `complete = true`. The oldest entry it found is
dated **2026-08-13**, 31 days before the pull. This group's audit log simply does not go back
further — VRChat's own retention ends it, not the 7,500 cap. Everything Modbot has not recorded by
the time an entry ages out is unrecoverable, which is the operational meaning of §5.1.

## 9. 721 entries were read and dropped before record-not-drop existed

1,233 entries walked; 512 stored. Railway logs from the first producer deployment (04:28Z,
2026-09-13) name what the rest were — each logged once as *"has no fact type, so it is being
counted but not recorded"*:

| VRChat event type | `description` template, names redacted |
|---|---|
| `group.instance.kick` | `<actor> has issued an instance kick for <name>.` |
| `group.instance.warn` | `<actor> has issued an instance warn for <name>.` |
| `group.instance.create` | `Group plus instance created by <actor>.` |
| `group.instance.close` | `Group instance closed by <actor>.` |
| `group.instance.announcement` | `Group instance announcement created by <actor>.` |
| `group.instance.update` | `Group instance linked to event by <actor>.` |
| `group.request.create` | `User <name> requested to join the group.` |
| `group.request.reject` | `Member <actor> has rejected <name>'s join request.` |
| `group.post.create` | `Group post created by <actor>` |
| `group.calendarEvent.create` | `Calendar Entry created by <actor>` |
| `group.update` | `Group <group name> updated by <actor>.` |

Two consequences. The catch-up cursor says complete, so **nothing would ever re-read them**; a
versioned re-walk was added so that every deployment re-imports once after the mapping change, and
dedup by `auditEntryId` makes the already-stored 512 a no-op. And `group.update` here is a real
**audit entry**, distinct from the group-info producer's `vrchat.group.update` facts (which carry no
`auditEntryId`) — both can exist for the same moment, and telling them apart is the entry id's job.

The counts per type are unknown: the logger reports each type once per process, by design.
