# Purging a person

**Status:** implemented
**Date:** 2026-09-18
**Supersedes nothing.** Implements foundation spec §5.5's "a purge-user action erases every fact for
one user on request", and narrows evidence storage design §15.2 — what the receipt says, and where a
person reads it.

---

## 1. The problem, in the maintainer's words

> We need to create a purge tab in settings for purging a user.

## 2. What already existed

`IUserPurger` and `UserPurger` have been complete since M2. They are careful code: one transaction,
facts and their linked-fact rows in the right order, counted-only daily totals deleted because
nothing can recompute them, computed ones rebuilt rather than deleted because deleting a row a
rebuild would put straight back is theatre, Discord messages with every earlier text of an edited
one, standing giveaway entries removed and frozen entrant rows blanked so an old draw still
verifies.

Its only reference in the whole solution was one line of dependency injection. No endpoint, no
button, no caller. The documentation promised the capability on four pages while the not-built page
correctly said it did not exist.

So this design is mostly about **reaching** something already written, and about being honest on
screen about what it does and does not do.

---

## 3. What a purge removes, and what it leaves

Read from `UserPurger` rather than from the older prose, because the code is the thing that runs.

### 3.1 Removed

| What | Where |
|---|---|
| Every fact where they are the **subject** | `modbot_event`, across every partition |
| The rows saying which of those facts were one decision | `modbot_linked_fact` |
| The rows saying a record about them was imported | `import_record` |
| Counted-only daily totals dimensioned on them | `modbot_daily_total`, origin Counted |
| Their Discord messages, and every earlier text of an edited one | `discord_message`, `discord_message_edit` |
| Their standing giveaway entries | `giveaway_entry` |
| Their **name and ids** in a past draw's entrant list | `giveaway_entrant` — the place and the weight stay |

Computed daily totals are worked out again for every day involved, so no surviving aggregate still
counts a fact that is gone.

### 3.2 Kept, deliberately

- **Facts where they were the actor.** A moderator's ban is a record about the person who was
  banned. Erasing it on the moderator's request would delete somebody else's moderation history
  (foundation §5.8).
- **Case files about them**, with their written rationale, their evidence and the profile snapshots
  taken at the time (evidence storage design §15.1). A case file is the group's record of its own
  decision; letting a banned person delete it makes purge-user a tool for laundering a moderation
  history.
- **The fact that the purge happened** (§6).

### 3.3 Kept, because erasing it would be theatre

`vrchat_user`, `group_member`, `group_ban`, `discord_member` are **current state, not history**.
Their own doc comments say so: "anything here can be rebuilt by the next sweep." Deleting a profile
row for somebody still in the group means the next profile sync fetches it again within hours. A
purge that removed rows the next sync recreates would be a screen telling an operator something
untrue, which is worse than a screen that tells them the row stays.

This is the same argument `UserPurger` already makes for computed daily totals, applied one table
outwards. The fact log is the part that cannot be rebuilt, and that is the part a purge destroys.

**Recorded as a known gap, not as a resolved question:** a person who has left the group and been
purged still has a `vrchat_user` row with the display name and bio VRChat last returned, until an
operator sets a retention window or the row is removed some other way. Nothing sweeps it, because
nothing sweeps that table at all. Whether Modbot should drop current-state rows for a purged person
who is no longer a member is a real question and is left open here rather than answered by accident.

### 3.4 The linked account is named, never followed

Somebody may have a VRChat account and a proved Discord account link. The preview **names** the
other account so the operator knows it is there; the purge erases only the account whose id was
typed.

Following the link would make this the one destructive action in Modbot that widens itself past
what a person typed — and the typed confirmation (§5) would then be confirming an id that is not
the whole of what gets destroyed. Two purges is the honest shape.

---

## 4. The screen

One tab, **Settings → Purge a person**, `#purge`. One card, three steps in order, each drawn only
once the step above it has an answer.

1. **Account** (VRChat or Discord) and **Id**, then **Look up**.
2. The counts: name, whether they are in the group or server, whether they are banned, the linked
   account if there is one, then *Removed* (facts, Discord messages, daily totals, days worked out
   again, giveaway entries, places in past draws, imported records) and *Kept* (case files,
   evidence files).
3. **Type the id to confirm**, then **Purge**.

Afterwards the same two lists again, as the receipt, with what actually went.

No explanatory paragraph anywhere on it (working conventions: UI text). The counts *are* the
warning: "Facts 41,208 / Case files kept 2" says more than a sentence about irreversibility, and it
says it in numbers an operator can check.

### 4.1 Where the numbers come from

`PurgePreviewer` counts from exactly the tables `UserPurger` writes to, and lives in the same folder
for that reason. A count in the API slice and a delete in the analytics slice would drift the first
time either grew a table, and the failure would be a screen that promised a number the purge then
contradicted.

Where Modbot cannot answer for a platform the field is **null, not zero**: a Discord account has no
group ban list and no case files, and somebody no sweep ever listed has no membership either way.
The screen draws "Never seen" rather than "Left", because those are different facts.

### 4.2 Not on the person's popup

Considered and rejected. The subject popup is a read surface that opens for anyone holding
*See profiles*; a purge needs Administrator, so the control would be invisible to almost everyone
who sees that popup, and for the few who could see it the most destructive action in Modbot would
sit a few pixels from *see their history*. Settings is where the deployment's other irreversible
control already lives — the retention window — and reaching it takes a deliberate trip.

---

## 5. How it asks

**A typed id, checked ordinally, and nothing else.**

The two existing patterns for a dangerous action were both read before inventing anything:

- **Destroying evidence** requires the `DestroyEvidence` permission and a **written reason**, kept
  forever on the destruction fact.
- **Banning** requires a single-use key the browser generates when the confirmation dialog opens,
  so one confirmation cannot act twice.

Neither is copied whole, and both refusals have a reason.

**No written reason.** Evidence's reason survives attached to a case the group may have to defend,
so the text is worth keeping. A purge's record must not carry text that could name the person it
erased (§6), and a free-text box on this screen is precisely the field into which a moderator would
type their name. The typed id is checked and then thrown away.

**No single-use key.** That key exists because two bans are worse than one. A second purge of the
same person finds nothing left and removes nothing, so the double-press it guards against is already
harmless, and a key would need a table and a migration to guard nothing.

What is left is the typed confirmation, which is the usual shape for this class of thing:
`confirmation` must equal `subjectId` exactly, `StringComparison.Ordinal`, untrimmed on the typed
side. The id is opaque text (foundation §3.1.1) and "close enough" is not a standard to erase
somebody by.

---

## 6. The record the purge leaves

`modbot.user.purged`, written inside the same transaction as the deletes.

- **Subject:** `Modbot` / `"purge"`. It names nobody, because an erasure log that names the erased
  person is not an erasure.
- **Actor:** the Modbot account id, with the username at the time in `actorDisplayName` — the same
  shape every other Modbot-actor fact uses.
- **Payload:** the platform (`"VRChat"` or `"Discord"`) and the counts — facts, counted daily
  totals, days worked out again, messages, giveaway entries, giveaway places.

The platform is safe to record: "a VRChat account was purged" points at nobody, and it is the one
piece of context that makes the entry readable a year later.

This is the change to `UserPurger`: `PurgeAsync` now takes a `PurgeActor`, so the fact can say who.
Before this, the only thing in Modbot that could destroy a person's history recorded that it had
happened and not who had done it. The counts were already there.

**What the fact does not carry:** the kept counts. Case files survive the purge and still name the
person, so they remain countable at any time; putting the number in the fact would add nothing the
database cannot answer, and every field on this fact is one more thing to have to prove says
nothing.

---

## 7. The permission

**Administrator. No new flag.**

Evidence storage design §14 already settled this, in the table that sets Detach against
`ManageEvidence`, Destroy against `DestroyEvidence` and Purge-user against `Administrator`. This
design implements that row rather than reopening it.

A flag of its own was considered and is wrong here. A narrow permission exists so a capability can
be handed to somebody who holds nothing else, and "can erase any person's history" is not something
anybody should hold on its own. Administrator is also checked rather than expanded
(`PermissionAuthorizationHandler`), so nothing has to be granted to existing accounts.

Note that this makes Purge the one Settings tab that is not gated on *Change settings*, and the tab
is not drawn at all for somebody without Administrator.

---

## 8. The API

| Route | Needs | Answers |
|---|---|---|
| `GET /api/settings/purge?platform=&subjectId=` | `Administrator` | `PurgePreviewResponse` — the counts, nothing changed |
| `POST /api/settings/purge` | `Administrator` | `PurgeReceipt` — what went, and what was kept |

`platform` is `VRChat` or `Discord`, case-insensitively. `Modbot` is refused: it is Modbot's word
for itself as the actor of its own records, and nobody is a person on it.

Ids go in the query string rather than the path, for the reason `/api/people/lookup` already gives:
a legacy VRChat id is arbitrary text.

A purge of somebody Modbot has never seen removes nothing and answers `200` with zeroes. A person
can ask to be erased from a group that never recorded them, and answering with an error would make
the operator think something had gone wrong.

---

## 9. What this changes elsewhere

- `docs/content/docs/not-built-yet.mdx` loses "**Removing everything stored about one person** in
  one step". It is built.
- The four documentation pages that already promised purging are correct for the first time; they
  are being brought in line separately.

## 10. Decisions

1. **Administrator, no new permission bit** (§7), implementing evidence storage design §14 rather
   than reopening it.
2. **A typed id, no written reason, no single-use key** (§5). Each refusal is about this action's
   shape rather than about copying less work.
3. **The purge's fact names the actor** (§6). `PurgeAsync` grew a parameter for it.
4. **Current-state rows stay** (§3.3), because the next sync rewrites them; the open question about
   a purged non-member's profile row is recorded rather than quietly answered.
5. **The linked account is named, never followed** (§3.4).
6. **Counting lives beside purging** (§4.1), so the screen and the purge cannot disagree.
7. **Not on the person's popup** (§4.2).
