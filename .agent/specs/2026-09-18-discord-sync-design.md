# Modbot — Discord role and ban sync

- **Date:** 2026-09-18
- **Status:** Built
- **Covers:** M5 §3 (role sync) and §4 (ban sync), and §7's dry run and permission reporting
- **Depends on:** M5 §2 (account linking, shipped), M4 (moderation actions), M0 (facts)

This narrows the M5 spec. Where the two disagree, this one is what was built.

---

## 1. What was missing

M5 shipped the bot, the slash commands, account linking, event routes, instance announcements,
message and voice history, and the analytics. The two halves that make Discord *connected* rather
than *observed* were never built:

- no mapping between a VRChat group role and a Discord role, and no setting for one;
- no sync job of any kind;
- and, decisively, **`IDiscordGateway` had no ban and no kick method at all**. It could post, edit,
  delete a message, time somebody out, send a direct message, add and remove a role, and read. A
  VRChat ban physically could not become a Discord ban.

The reverse direction was not wired either. `FactType.DiscordMemberBanned` and
`DiscordMemberUnbanned` were written by `DiscordEventRecorder` and every consumer was read-only,
and there was nothing anywhere recording that Modbot had caused an action — which is what M5 §4.2's
loop prevention needs.

`LinkedRoles` is not this. It grants two fixed roles from link state and the VRChat 18+ flag; it
reads no VRChat group role and belongs to the account linking design §6.

---

## 2. The two halves need opposite designs

This is the decision everything else follows from.

**Roles are state.** "Do they hold Staff here and there" has an answer at every moment, on both
sides. So role sync compares both sides and makes the mirror match the deciding one. Comparing
state is idempotent: a pass that has done its work finds nothing, and a change Modbot itself made
is already agreed with by the time the next pass looks. **There is no circle for a role change to
travel round, so role sync needs no loop prevention at all.**

**Bans are not state, for this purpose.** "VRChat has a ban and Discord does not" is ambiguous in
exactly the way that matters: either the VRChat ban is new and should be copied, or the Discord ban
was lifted and the *unban* should be copied. A comparing design cannot tell those apart and would
flap between them forever. So ban sync follows events — and a design that follows events is
precisely the one that loops.

So: role sync compares, ban sync follows events, and only ban sync carries the loop machinery.

---

## 3. Role sync

### 3.1 The pair is the unit, and it carries the authority

A pair is **one VRChat group role, one Discord role, and which side decides**: `vrchat`, `discord`
or `nobody`. M5 §3.1 called the third `Manual`; it is `nobody` here because that is the word a
volunteer moderator reads off a dropdown without being told what it means.

Authority belongs to the pair rather than to the deployment because that is what real groups want:
Staff decided in VRChat, Event host decided in Discord, at the same time. A single global setting
would force one of those to be wrong.

`nobody` records the disagreement and changes nothing. It is the starting value, because a group
that has not decided yet should get a report rather than a surprise.

**The database refuses a role in two pairs** — unique on each side. Two pairs naming the same role
with different sides deciding would undo each other on alternate passes, which is exactly the
flapping §3.1 exists to prevent, and it is cheaper to make impossible than to detect.

### 3.2 Only paired roles, only linked people, only people on both sides

Three separate restrictions, all of them narrowing:

1. **Only paired roles.** An opt-in list, never "everything except". A role nobody paired is not
   Modbot's business, whoever holds it.
2. **Only linked people.** Most members of a Discord server have no VRChat account tied to them.
   The sync reads the account links and joins through them, so an unlinked member is not merely
   skipped — they are never in the query's result at all. This is the failure mode that would be
   worst if it went wrong (a first run stripping roles from three thousand strangers), so it is
   structural rather than a condition somebody has to remember.
3. **Only people on both sides.** Somebody linked but no longer in the group, or no longer in the
   server, is skipped. A role cannot be mirrored onto a platform the person is not on, and asking
   VRChat to give a group role to a non-member only earns a refusal every minute forever.

### 3.3 Reverting is reported

M5 §3.1 asks that a change made on the mirror side be reverted and the revert reported rather than
performed silently. The report is a fact — `modbot.copy.role.give` or `modbot.copy.role.take`, with
the pair, both role ids, both account ids and one plain sentence saying which side decided. It
lands in the person's history where a moderator who just assigned that role will see it.

A pair set to `nobody` writes `modbot.copy.disagree` instead, **once a week per person per pair**.
The disagreement is still there on the next pass and on every pass after that; saying so once a
minute would bury the log it is trying to be useful in.

### 3.4 The bot's role hierarchy

M5 §3.3 asks for this to be a setup problem rather than a runtime discovery. Two places:

- Switching role sync on is **refused** when the bot does not hold Manage Roles in the server.
- A pair whose Discord role the bot cannot assign — above the bot's own, or owned by an integration
  — is reported **on the pair**, with the role named, and no member of it is attempted. The settings
  screen shows it beside the pair.

---

## 4. Ban sync

### 4.1 It reads the fact log

Not the gateway's events. Every ban Modbot knows about becomes a fact — from VRChat's group audit
log, from Discord's server audit log, or from the gateway when the bot may not read that — and ban
sync reads facts of four types in id order from a marker it keeps.

Three things follow, and all three are why:

- **One code path for live and for catch-up.** A ban seen this second and a ban seen after three
  days offline arrive the same way, because the audit-log readers fill in what was missed and the
  marker has not moved.
- **What the sync acts on is what a moderator reads.** There is no second stream of truth.
- **The loop check sits where M5 §4.2 asks for it**: "at the fact layer, so it holds regardless of
  which code path triggered the action". A ban issued by a slash command, by the Modbot UI, by an
  AutoMod rule or by a person in Discord all reach ban sync as facts, and all are checked the same
  way.

### 4.2 Switching a direction on starts from now

The marker jumps to the newest fact the moment a direction goes from off to on. Otherwise turning
a switch on would replay years of history as a flood of bans — the single worst thing this feature
could do in its first minute.

The backlog is a separate, deliberate act: §6.

### 4.3 Loop prevention, guarded twice

> Modbot bans in VRChat → the audit log records it → Modbot ingests it → Modbot bans in Discord →
> Discord emits a ban event → Modbot ingests it → Modbot bans in VRChat.

**Guard one: the copy record.** A row in `discord_copied_action` naming the direction, the kind, the
person and the time is written **before** the outbound call, and every incoming ban and unban is
checked against the rows first.

The ordering is the whole point. The fact that records the returning event is written *later*, by
whatever observed it, by code that has no idea a sync exists. A row written before the call is the
only record certain to be there when the event arrives.

Two properties of that row matter as much as its existence:

- **It answers for exactly one returning event and then stops.** `seen_back_at` is set when it
  excuses one, and it excuses nothing afterwards. Without that, one copy would silently swallow
  every later ban of the same person — somebody unbanned by hand and banned again an hour later
  would quietly fail to cross over, which is the kind of silent failure a moderation tool must not
  have.
- **A copy that failed excuses nothing.** No call reached the platform, so no event is coming, and a
  row left waiting would swallow the next real one. Same for a copy the platform said was
  unnecessary ("already banned"): both are closed immediately.

The look-back window is an hour forward-tolerant by five minutes. Generous, because a VRChat ban is
not seen until the group's audit log is next read and a deployment that has been rate limited reads
it much later; and far shorter than the gap between two separate decisions about the same person.

**Guard two: the actor, on the Discord side only.** Discord's audit log names whoever banned, and a
ban Modbot performed names the bot's own account. So **a ban a person issued and a ban Modbot copied
look different in the permanent record**, and only the person's one is ever copied onwards. The
gateway exposes the bot's own user id for this.

That second guard is worthless in the other direction, and saying why is the reason both exist:
**VRChat attributes everything Modbot does to Modbot's own account**. A ban a moderator pressed in
the Modbot UI and a ban Modbot copied from Discord are indistinguishable by actor — and the first
should cross over while the second must not. Only the copy record separates them.

### 4.4 Unbans follow their bans

No switch of their own, as M5 §4.3 asks. A group that copies bans but not unbans quietly
accumulates people who are forgiven in one place and banned forever in the other.

### 4.5 Ban, or remove

A VRChat ban can arrive in Discord as a ban or as a removal from the server, chosen per deployment.
A group that does not want a VRChat offence to earn a permanent Discord ban picks removal.

**A removal cannot be undone**, so with removal chosen a later VRChat unban copies nothing: there is
no Discord ban to lift, and the person simply rejoins. This is written down because it is the one
place where the two choices are not symmetric.

### 4.6 A copied ban keeps the messages

Discord will delete up to seven days of a banned person's messages. Modbot asks for none. Modbot
stores the server's messages so a moderator can read what somebody said, and a copied ban wiping
that is the opposite of what the record is for. Somebody who wants them gone deletes them in
Discord.

---

## 5. Pace

**Discord queues the bot's requests behind one another.** A sync that fired a thousand of them would
delay every announcement, card, command and moderation-log post the bot owes anybody — worse than
no sync at all. So:

| | Cap | Interval |
|---|---|---|
| Role changes | 50 a pass | one pass a minute |
| Bans copied | 20 a pass, 200 facts read | one pass a minute |
| First run | 100 a press | an operator presses it |

The same shape the linked-member roles job already uses, deliberately: one more loop that behaves
like the others rather than a new kind of thing.

The VRChat half is paced again by the gate. Role changes go through `moderation.write` at
**background** priority, so a moderator pressing Ban preempts a sync pass rather than queueing
behind it. Copied bans go through `groups.moderate` at interactive priority, as M5 §4.3 requires —
they are low volume and event-driven.

**The VRChat role endpoints have no measured rate limit.** Foundation §4.2 declared
`moderation.write` for "bans, kicks, role changes" and the maintainer set its budget — roughly one
request every three seconds — before anything used it. That number is a guess deliberately set too
low, not a finding, and §4.3.4's standing question about `PUT/DELETE
/groups/{id}/members/{id}/roles/{id}` is still open. The per-pass cap is the second brake.

---

## 6. The dry run and the first run

M5 §7 asks for a mode that shows what would change. Two buttons, and they are different things:

- **Show what would change** works out every difference and sends nothing: no request to either
  platform, no fact, no copy record. It is the same pass with one flag, so what it lists is what
  would happen rather than a second description that can drift. It answers whether or not the
  switches are on, because seeing what would happen is how somebody decides to turn one on.
- **Copy what is different** applies it. For roles that is the ordinary pass. For bans it compares
  the two ban lists — the group's, and Discord's, read once — and copies the differences.

**The first run never lifts anybody's ban.** Before there is any history, "this side has no ban" and
"this side lifted the ban" are identical, and guessing would quietly free people. It only ever bans,
only in a direction that is switched on.

---

## 7. When the bot lacks the permission

The common misconfiguration, and M5 §7 asks for it to be named at configuration time.

- **The invite link asks for it** only when it is needed: Ban Members when a group ban is copied
  into Discord as a ban, Kick Members when it is copied as a removal, Manage Roles when role sync is
  on. A deployment that syncs roles and nothing else never hands the bot the ability to ban anybody.
- **The switch is refused when the bot does not hold it**, naming the permission. A bot that has
  never connected is not second-guessed — nothing is known about what it may do, so the switch is
  allowed and the pass reports whatever Discord says.
- **At run time a 403 is its own outcome**, separate from an ordinary failure, because it will
  refuse every remaining copy the same way. The pass stops rather than earning a hundred identical
  refusals, records `modbot.copy.failed`, raises a bot status problem, and **leaves the marker where
  it was** — so the ban is picked up again the moment an operator fixes the permission, rather than
  being lost to a setup mistake.

"Already banned" and "not banned at all" are deliberately **not** failures. A sync that treated them
as failures would report a problem every pass forever.

---

## 8. Permissions

Two Modbot permissions, bits 35 and 36:

- **Manage role and ban sync** — make pairs, choose which side decides, switch the directions on,
  and see what a sync would do.
- **Run role and ban sync** — copy the roles and bans that are already different.

Separate because the first run against an established server can ban or move hundreds of people in
one press. Setting up "Staff goes with @Staff" and doing that are different decisions, and the
person who should be able to make the first is not automatically the person who should be able to
make the second. Neither is in the built-in roles.

---

## 9. What is stored

| Table | What it holds |
|---|---|
| `discord_role_pair` | The pairs, which side decides, each one's last problem, and both role names as they were when the pair was saved |
| `discord_copied_action` | Every change Modbot copied, which way, what caused it, how it went, and whether its returning event has been answered for |
| `discord_sync_state` | How far ban sync has read, when each half last ran, and each half's last problem |

Role names are kept on the pair so a fact about a role change can say "Staff" years later instead of
`grol_9f3c…` — the same reason the group's own role list is recorded. The screen shows each
platform's live name; the stored one is what goes into history.

---

## 10. Facts written

All at moderation retention except the last two, which are about the sync rather than about a
person:

| Type | Meaning |
|---|---|
| `modbot.copy.ban` | A ban was copied to the other platform |
| `modbot.copy.unban` | An unban was copied |
| `modbot.copy.remove` | A group ban removed somebody from the Discord server |
| `modbot.copy.role.give` | A paired role was given to match the deciding side |
| `modbot.copy.role.take` | A paired role was taken away to match the deciding side |
| `modbot.copy.failed` | A copy the platform refused (operational) |
| `modbot.copy.disagree` | A pair nobody decides found a difference (operational) |

The prefix is `modbot.copy.` rather than `modbot.sync.` because `modbot.sync.failed` already means
"a background sync job gave up", and two unrelated things under one prefix is how a log stops being
queryable.

Each carries the fact that caused it, both account ids, and one plain sentence. There is no actor:
nobody pressed anything at that moment, and the payload names the fact — and so the person — that
did.

---

## 11. Where this narrows M5

| M5 says | This says |
|---|---|
| §3.1 authority is `VRChat`, `Discord` or `Manual` | The third is called **nobody**, for the reason CLAUDE.md gives about words a volunteer has to have explained |
| §3.1 "the non-authoritative side is a mirror; changes there are reverted and reported" | The report is a fact in the person's history, and a pair nobody decides repeats itself at most once a week |
| §3.2 partial mapping is normal | And a role may be in at most one pair, enforced by the database |
| §4.2 "every synced action records its origin" | The origin is a row written **before** the call, not a mark on the fact — facts are immutable and are written later by code that knows nothing about the sync. A row answers for one returning event and then stops |
| §4.2 "checked at the fact layer" | Ban sync reads the fact log rather than the gateway, which is what makes that true |
| §4.3 a synced ban carries a classification | **Not built.** A copied ban writes no case file and picks no reason from the group's list: nobody chose one, and inventing one would put a moderator's name on a decision they did not make. §12 |
| §7 a dry run for role and ban sync | Built, as the same pass with one flag. Plus a separate first-run compare, because the minute-by-minute passes deliberately never touch the backlog |
| §7 minimum permissions | The invite link asks for Ban Members only when a ban is actually copied as a ban, and Kick Members only when it is copied as a removal |

---

## 12. Not built, and why

- **A classification on a copied ban** (M5 §4.3). A ban a moderator presses in Modbot picks reasons
  from the group's list and writes a case file. A copied ban has nobody to pick them: the decision
  was made on the other platform, often by somebody with no Modbot account. Attaching `Other` and a
  case file with no author would put a name on a decision nobody here made, which is the opposite of
  what §5.9.1's attribution rule is for. The copy fact carries the fact that caused it, so the
  original decision — and whoever made it — is one hop away. Worth revisiting if a group asks for it.
- **Copying a group removal, as opposed to a ban.** `vrchat.group.member.remove` is not watched.
  A kick is the reversible action and a ban is the one worth widening; adding a third direction
  before anybody has asked for it is surface for its own sake.
- **Copying timeouts.** A Discord timeout has no VRChat equivalent, and a VRChat instance kick has
  no Discord one.
- **Waking a pass on an event.** Both halves run on a one-minute timer. A ban crossing over within a
  minute is fast enough, and a signal per event would be a second path into the same work for no
  benefit a moderator would notice.
- **The auto-invite of M5 §2.3.** Still unbuilt; it belongs to linking, not to this.

---

## 13. Open questions

1. **The rate limit for the VRChat group role endpoints** (foundation §4.3.4). Never measured;
   `moderation.write`'s budget is a guess set deliberately low. Worth measuring before a large group
   turns role sync on with Discord deciding.
2. **Reading Discord's ban list on a very large server.** It pages a thousand at a time and the
   first-run compare reads all of it. A server with tens of thousands of bans will make that press
   slow; nothing paces it yet because nothing has hit it.
3. **Whether a copied ban should carry the other platform's reason text.** Discord's audit log
   carries the reason a moderator typed; VRChat's ban endpoint takes none. Copying it into the fact
   payload would be easy and would tell a moderator more.
