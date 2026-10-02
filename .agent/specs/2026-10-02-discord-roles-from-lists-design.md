# Modbot — Discord roles from saved lists

- **Date:** 2026-10-02
- **Status:** Built
- **Covers:** pairing a saved list with a Discord role, so everybody in the list holds the role and
  loses it again when they leave the list; which roles may be paired; the preview, the switch and
  the brake; what Modbot writes down; purge
- **Depends on:** lists design (the rule tree and `GiveawayRuleChecker.PeopleAsync`), Discord sync
  (role pairs, the sync loop, `IDiscordGateway.ChangeRoleAsync` and its pace), the server index
  (`discord_role.permissions`, `bot_can_assign`), account linking (proven links)
- **Narrows:** lists design §1 and §8, which said a list never gives a role to anybody. It still
  never does by itself: only a pairing somebody makes here, with its switch on, does.
- **Approved:** 2026-10-02, option A ("Discord roles from saved lists")

---

## 1. What this is

A **list role** pairs one saved list with one Discord role. Everybody the list lets through, and who
is in the Discord server, gets the role. When they stop being in the list, the role goes again.
"Visited 5+ times" gives @Regular; "18+ on VRChat" gives @18+.

It changes one thing in Discord: whether a member holds a role somebody paired here. Nothing else —
no other role, no nickname, no message.

## 2. Who can get the role

A list's rules are about VRChat people, Discord members, or both (lists design §5). A Discord role
can only be held by a Discord member, so:

| Somebody in the list who is | Gets the role? | Counted in the preview as |
|---|---|---|
| a Discord member (linked or not) in the server | yes | give, or already have it |
| a VRChat account with no **linked** Discord account | no | no linked Discord |
| linked, but not in the server | no | not in the server |

"Linked" is the proven link (`discord_account_link` with no `unlinked_at`), the same one the
checker joins people by. A Discord id somebody typed somewhere never counts. A member who unlinks
stops matching rules about their VRChat account, and loses the role the next pass, like anybody
else who leaves the list.

## 3. Only what it gave

**A list role only ever takes away a role it gave itself** (recommended in the brief, built). Each
give is written down in `discord_list_role_given` (the list role, the Discord account, the VRChat
account when linked, when). A take needs that row.

| Case | What happens |
|---|---|
| In the list, does not hold the role | given; row written |
| In the list, already holds it (given by hand) | nothing; no row |
| Out of the list, holds it, row says Modbot gave it | taken away; row removed |
| Out of the list, holds it, no row (given by hand) | nothing, ever |
| Modbot gave it, the member list does not show it, still in the list | nothing: not given again while the row stands (taken off by hand, or the update has not come) |
| Out of the list, row stands, the member list does not show the role | taken away anyway: a missing role on the stored row is a reason to ask Discord, not proof |
| Modbot gave it, then saw them leave the server | row removed (leaving took every role); given again if they come back and still match |
| Row stands, no member row at all | kept: Modbot never saw them leave |
| Row older than their current join | they left and came back between two passes: row removed, given again |

**A row is only forgotten when Modbot saw the person leave** (the member row's `left_at`), never
because the stored member row lacks the role (review 2026-10-02). After each give or take the pass
adds or removes that one role id on the stored member row in a single statement, so the next pass
reads what it just did without waiting for Discord's update, and no other role is rewritten.

The second-to-last row is the "never fight a person" rule: a moderator who takes @Regular off
somebody by hand is not overruled a minute later. Once that person drops out of the list the row is
removed quietly, and coming back into the list gives the role again.

A row exists only for a role Modbot gave and believes they still hold, which is also what `/me`
lists under "roles Modbot gave you".

## 4. Which roles may be paired

Refused when the pairing is saved, and checked again on every pass (a role can gain a permission
after it was paired; the pass then changes nothing and says why):

- **@everyone**;
- **a bot's or an integration's role** (`managed`);
- **a role the bot cannot give** (`bot_can_assign` false: no Manage Roles, or the role is not below
  the bot's highest role);
- **a role whose permissions Modbot has not read yet** (`permissions` null);
- **a role gone from Discord**;
- **a role that carries a staff power.** These lists are for community roles. Staff roles have their
  own feature. Any one of these server-wide permission bits refuses the role:

| Permission | Bit |
|---|---|
| Administrator | 3 |
| Manage Server | 5 |
| Manage Roles | 28 |
| Manage Channels | 4 |
| Manage Messages | 13 |
| Ban Members | 2 |
| Kick Members | 1 |
| Moderate Members (time out) | 40 |
| Mention @everyone, @here and All Roles | 17 |
| Manage Webhooks | 29 |
| Manage Nicknames | 27 |
| Manage Threads | 34 |
| Manage Events | 33 |
| Manage Expressions | 30 |
| View Audit Log | 7 |
| Mute Members, Deafen Members, Move Members | 22, 23, 24 |

  The first nine are the brief's. The rest are added because each one lets a holder act on other
  members or the server — post as anybody (webhooks), rename people, hide threads, move or silence
  people in voice, read the moderation log — which is a staff power by any reading.
- **a role something else already decides**: a role paired with a group role (role sync), the
  linked-member role and the 18+ role (account linking), or a role another list gives. Two things
  deciding one role would undo each other every minute. Pairing a group role with a role a list
  gives is refused the same way.

Only roles somebody pairs here are ever touched. There is no "every role except".

## 5. The switch, the preview, the brake

**Only on a fresh member list** (review 2026-10-02). Who holds the role is read from the stored
member list, which falls behind whenever the bot is disconnected or member updates are off. The bot
clears `discord_server.members_read_at` when it connects and when it loses the connection, and sets
it once it has compared the whole list; it stays clear while the Server Members intent is off,
since Discord then sends no list. A plan is made only while it is set and the bot noted it was
listening in the last 5 minutes (`ListRolePlanner.MembersFreshFor`); otherwise nothing is given,
taken or forgotten, and the card says "Waiting for the member list."

**Old VRChat data takes nothing.** A list asking anything answered from VRChat (every rule but
Discord days, voice hours, messages, Discord role and linked accounts) takes nobody's role while
the group's member sweep or audit log has not completed in the last 6 hours
(`ListRolePlanner.VRChatStaleAfter`): people may only look gone. Gives still go ahead.

**Lists that ask nothing are refused.** Rules that let everybody in are refused when a pairing is
saved and on every pass; stored rules that cannot be read are never taken to mean "everybody".

**The switch.** *Give roles from lists*, `settings.discord_list_roles_on`, **off by default**. Off
gives and takes nothing at all. Each list role also has its own **On**; a new one is on, but nothing
happens while the switch is off. Turning the switch on is refused while the bot lacks Manage Roles.

**The preview** is the same code as the pass (`ListRolePlanner`), changing nothing: who would be
given the role, who would lose it, how many already have it, how many in the list have no linked
Discord account, and how many are not in the server. The screen shows it before a new list role is
saved and before the switch is turned on: the save and the turn-on buttons appear only after it.
It needs See members and See profiles as well as Manage role and ban sync, because it names people
in a list (lists design §6).

**The brake.** A pass that would take the role from many people at once takes nothing and gives
nothing for that list role, says so once, and waits:

```
losing = taking + leaving
stops  = losing >= 3 && (losing > 25 || losing * 2 > holders + leaving)
```

`holders` is everybody in the server holding the role now, given by hand or not; `leaving` is people
Modbot gave the role to and saw leave the server, whose rows would be forgotten. The brake runs
before any row is forgotten, so a member list that suddenly shows everybody gone stops the pairing
with its rows intact. The floor of 3 and
the "more than half" are the staff roles brake's (staff roles design §6, built alongside); 25 is
higher than its 5 because a community role has many more holders and a few people drifting out of
"regulars" each day is normal. It catches a list whose rules were changed or broken, a retention
change, a member list that went missing. Giving is not braked: the preview is what guards a first
run.

Saving a pairing needs what the preview needs (Manage role and ban sync, See members, See
profiles), since saving follows it. **Apply** (Run role and ban sync, with See members and See profiles) is pressed after looking at the
list. It carries the number of removals the person saw; if more would be taken now, it is refused
("More would be taken away than you saw. Look again."). Otherwise that many removals are allowed
(`removals_allowed`), counted off as each role is taken or each row forgotten, and the passes carry
on through the brake while the backlog fits it; once a pass is under the brake the allowance is
cleared. Nothing is sent from the request itself:
the next pass, within a minute, does the work at the usual pace.

A list that cannot be answered — a rule reaching past retention, more than 50,000 people, a list
too big once written out — is never read as "nobody": the pass changes nothing for it and says why.

## 6. When it runs, and how fast

On the Discord sync loop (`DiscordSyncService`), after role sync, in its own try and its own
scope so a failure in either never stops the other, once a minute while the bot has a ready
session. Changes a pass does not get to, because of the 50 limit, the brake or a refusal from
Discord, are counted as left. At most **50 changes a pass** across all list roles (the role sync's number, for the
same reason: Discord queues the bot's requests behind one another). Each change is one
`ChangeRoleAsync` with a reason in Discord's audit log: `Modbot: in the list “Regulars”` or
`Modbot: no longer in the list “Regulars”`. The list's name is the only thing in it.

Each pass works every enabled list role out afresh with the checker; a list is small and bounded
(lists design §5).

## 7. What is written down

| Fact | Subject | Payload | Log |
|---|---|---|---|
| `modbot.list.role.give` | the Discord account | list id and name, role id and name, list role id, VRChat id when linked | Moderation |
| `modbot.list.role.take` | the Discord account | the same | Moderation |
| `modbot.list.role.stop` | the list role | list and role names, how many it would take | Operational |

No display names: the subject is the account, and the audit log names them from its own records.
Setting up is a settings change (`discordListRoles`): the switch, and each list role added, changed
or removed, in words ("Regulars gives Regular, on").

A failed change is not a fact: it is the list role's **problem** on the card and in the bot's
status, like role sync's.

## 8. Purge

Erasing a person deletes their `discord_list_role_given` rows (by Discord id, and by VRChat id).
Modbot then no longer knows it gave them the role, so it never takes it away; the role stays in
Discord until somebody removes it there. Their give and take facts go with the rest of their facts.

## 9. Lists

A list a list role uses is **in use**: it shows "Discord role: Regular" beside the giveaways and
events, cannot be deleted, and changing it needs **Manage role and ban sync** as well as Manage
lists, because changing it changes who holds the role. The list's own page has a **Give a Discord
role** button for those who may set one up, which opens Settings → Discord with the list picked.

## 10. Where it lives

Settings → Discord, a card **Roles from lists** beside **Role and ban sync**, where the VRChat ↔
Discord role pairs are. Labels only: the switch, the list and role pickers, each list role's row with
its **On**, how many it has given, **Show who would change**, **Remove**, and **Apply** while it is
stopped.

## 11. Not built

- **Taking back on remove.** Removing a list role, or switching it off, leaves the role with
  everybody who has it and forgets which ones Modbot gave. A "take them back" button would be a
  second, bigger brake decision.
- **Waiting out close calls.** Somebody near a presence threshold can fall in and out of a list; the
  role follows. A grace period is a later choice.
- **Staff role mappings.** The staff roles feature (built alongside) maps Discord roles to Modbot
  roles. A role it maps is not yet refused here; whichever lands second adds the check both ways.
