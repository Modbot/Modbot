# VRChat group audit log — findings from live data

- **Source:** 638 facts written by Modbot's audit-log producer against a real ~16k-member group,
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
