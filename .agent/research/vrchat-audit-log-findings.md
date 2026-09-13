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

## 6. Observed shapes after the re-walk — 1,241 entries, every type's key set stable

After the versioned re-walk recovered the dropped entries (§9), every recorded type has **exactly one
`auditData` key set across all its rows**. These are real shapes, not one-offs.

| Event | Rows | `auditData` keys | `targetId` is a… |
|---|---|---|---|
| `group.instance.kick` | 408 | `location` | `usr_` — the person kicked |
| `group.instance.warn` | 39 | `location` | `usr_` — the person warned |
| `group.instance.create` | 34 | `calendarEntryId, groupAccessType, roleIds` | `wrld_…:…~group(…)` — the **location** |
| `group.instance.close` | 20 | `groupAccessType, roleIds` | location |
| `group.instance.announcement` | 29 | `message, title` | location |
| `group.instance.update` | 1 | `calendarEntryId: {old, new}` | location |
| `group.post.create` | 19 | `authorId, imageId, roleIds, sendNotification, text, title, visibility` | `not_` — a notification id |
| `group.calendarEvent.create` | 6 | `accessType, description, imageId, title, type` | `cal_` |
| `group.role.update` | 1 | `lastUpdatedByUserId, permissions: {old, new}` | `grol_` |
| `group.update` (audit) | 2 | `bannerId: {old, new}` | `grp_` |
| `group.member.role.unassign` | 1 | `roleId, roleName` | `usr_` |
| `group.request.create` / `.reject`, `group.invite.create`, `group.member.join` / `.leave`, `group.user.ban` | 680 | *(empty)* | `usr_` |

Three things follow.

**Instance events carry the instance.** Kicks and warns put the full location string in
`auditData.location`; creates, closes, announcements and updates put it in `targetId`. It is the same
grammar the client already parses (`vrchat-log-format.md` §1.2), and the fact row already has
`world_id` / `instance_id` columns that these facts leave null. Lifting it — by delimiters only,
never by shape (§3.1.1) — is what makes "which instances get the most kicks" answerable, and it is
evidence-backed across 531 rows.

**`{old, new}` diffs are a general shape, not a `group.update` special case.** `role.update` and
`instance.update` use it too. The `changed` lift should apply wherever a value is an `{old, new}`
object, not to a named list of types.

**A post's `targetId` is the notification, not the post.** `not_…` is the id VRChat notifies members
with; the post's own body is in `auditData` (`title`, `text`, `authorId`). Do not treat that
`targetId` as a subject anyone can be looked up by.

Still unobserved: `group.calendarEvent.delete` / `.series.*`, `group.post.delete`,
`group.request.block`, `group.member.user.unban` / `group.user.unban`. Same rule — update from
`auditData` when one arrives.

## 7. The audit log's `offset` is hard-capped at 7,500

Reported by the maintainer, 2026-09-13, **for the audit log — and, as §7.1 measured, for the audit
log only.** Members, bans, invites and join requests have no offset cap up to 100,000. `offset=7501` on the audit log returns HTTP 400
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
- It does **not** extend to the other group endpoints (§7.1). A sweep of members or bans can page by
  offset without special handling.

### 7.1 Measured: no other group endpoint has the cap

`explore offset-probe`, 2026-09-13, same group, `n=1`, one request per 3.5 s, **72 requests, no
429**. Each endpoint was asked for offset 7,500 and 7,501, then bisected upward to 100,000 looking
for the first 400.

| Endpoint | 7,500 | 7,501 | 99,999 | Reading |
|---|---|---|---|---|
| group members | 200, empty | 200, empty | 200, empty | **no cap to 100,000** |
| group bans | 200, empty | 200, empty | 200, empty | **no cap to 100,000** |
| group invites | 200, empty | 200, empty | 200, empty | **no cap to 100,000** |
| group join requests | 200, empty | 200, empty | 200, empty | **no cap to 100,000** |

Past the end of the data every one of them returns 200 with an empty list, all the way up — the
audit log is the only endpoint that answers 400 there. **The 7,500 cap is the audit log's alone.**

Consequences:
- M1's member and ban sweeps can page by offset. The obvious design is fine; nothing needs filters
  or sort windows on this account.
- `/members` enumerates the whole group: the list ends at offset 4,739 and the group's own
  `memberCount` is 4,740–4,741, with two people joining or leaving during the run.
- The first run of this probe hit 403 on bans, invites and requests: the bot account lacked the
  group role permissions those endpoints need. Granted, and re-run — the numbers above are from the
  second run.

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
