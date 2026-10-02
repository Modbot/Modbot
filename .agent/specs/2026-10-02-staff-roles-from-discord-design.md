# Modbot — Staff roles from Discord

- **Date:** 2026-10-02
- **Status:** Accepted 2026-10-02. Every decision answered (§10)
- **Covers:** letting a Discord role decide whether a staff account holds a Modbot role. The
  screens call a mapping a **linked role**; this spec keeps "mapping" for the row.
- **Depends on:** accounts and access (§3 roles, §3.5 role order, §4.6 proving a Discord account),
  the Discord member list (`discord_member`, kept by the bot), the sync loop (`DiscordSyncService`)
- **Related:** M5 role sync (`RoleSync`, VRChat group roles ↔ Discord roles), which this copies the
  shape of: a pass that compares state, a dry run that is the same code, a cap per pass, a switch

---

## 1. Why

A server with ~39 staff in 14 Discord department roles (Thy Kingdom review, 2026-10-02) gives every
Modbot role by hand, a second time, after giving the Discord role. The two drift: somebody is taken
off staff in Discord and keeps their Modbot permissions until somebody remembers. Discord is where
these teams already decide who is staff, so Modbot should follow it.

## 2. What is mapped

**A Discord role gives a Modbot role.** Stored in `discord_staff_role`: Discord role id, Modbot role
id, who made it and when. Several Discord roles may give the same Modbot role (14 departments →
*Moderator*); holding any one of them is enough. One Discord role gives one Modbot role.

**Only what somebody maps syncs.** *Decided 2026-10-02: "it should be set up by the User what roles
are synced".* There are no mappings until a person makes one, and each one is chosen explicitly:
which Modbot role, which Discord role, and its direction (§3). Nothing is guessed from names, nothing
is suggested and saved on its own, and a Modbot role or Discord role nobody mapped is never looked at.
A new deployment, and one that never opens the screen, behaves exactly as before.

Refused when saving:

| Refused | Why |
|---|---|
| The Modbot role is Administrator, or carries the Administrator permission | Discord must never be able to make an administrator. |
| The Modbot role is not below the saver's highest role, or allows a permission the saver lacks | The rules for handing out a role by hand (§3.5) apply to a rule that hands it out. |
| The Discord role is @everyone or a managed role (`DiscordRole.Everyone`, `.Managed`) | @everyone is the whole server; managed roles belong to bots and boosting. |
| The Discord role is gone from the server (`RemovedAt`) | Nothing can hold it. |

Saving and removing a mapping takes **Manage roles and Manage users** together: it changes who
holds a role (users) by a rule about a role (roles).

## 3. Direction

Each mapping has a **direction**, chosen when it is saved:

| Direction | What it does |
|---|---|
| **Discord decides** (the default) | Discord's role decides the Modbot role. Nothing Modbot does changes anybody's Discord roles, and the bot needs no extra Discord permission: it reads the member list it already keeps. |
| **Both ways** | As above, and giving or taking the Modbot role in Modbot gives or takes the Discord role. |

### 3.1 Both ways

*Decided 2026-10-02: "certain roles yes should be two-way".*

- **One to one.** A both-ways mapping must be the only mapping for its Modbot role and for its
  Discord role: with two Discord roles behind one Modbot role, giving the Modbot role would not say
  which Discord role to give. Saving one that breaks this is refused with 400, and a partial unique
  index (`ux_discord_staff_role_both_ways`, `role_id` where `direction = 'both'`) holds the database
  to two both-ways rows never sharing a Modbot role. A both-ways row beside a one-way row is beyond
  an index; saves take an advisory lock (`StaffRoles.LockSavesAsync`), and so do VRChat role pair
  saves, so two at once cannot both pass these checks.
- **Never a powerful Discord role, never a paired one.** Saving both ways is refused for a Discord
  role that carries Administrator, Manage Server, Manage Roles, Ban Members or Kick Members, or whose
  permissions the bot has not read yet: Modbot must never hand those out. It is refused too for a
  Discord role already in a VRChat role pair (M5), because two syncs writing one role undo each
  other, and a VRChat role pair is refused on a Discord role linked both ways. *Discord decides*
  allows both, since it changes nothing in Discord. Power is checked again on every pass and before
  every change in Discord, from the role as the server index last read it: a Discord role that has
  gained one of those permissions makes the link *Not set up*.
- **The bot must be able to give the role**: Manage Roles, and its own highest role above the mapped
  one (`DiscordRole.BotCanAssign`, kept by the server index). When it cannot, the mapping shows
  **Not set up** on the screen and the Discord health row says which role, and the mapping works as
  *Discord decides* until it can: Discord's changes still reach Modbot, and changing the role by hand
  in Modbot is refused (§5) rather than accepted and never sent. A Discord refusal at the moment of
  giving (403, the role moved above the bot) sets `discord_staff_role.refused_at`, which makes the
  mapping *Not set up* in every place that asks (`StaffRoles.Works`: the pass, the preview, the
  hand-change lock, the screen), with one `modbot.copy.failed` fact. The pass asks no more for that
  mapping that run, and does not ask again until a day has passed or any role in the server has
  changed since (the server index stamps a role only when something about it changes, so a bot
  given Manage Roles or moved up shows as a change). Nothing fails without saying so, and nothing
  fails once per person per minute.
- **Who wins.** For each covered account and both-ways mapping Modbot keeps what both sides last
  agreed on (`discord_staff_role_state`: account, mapping, the Discord account it was about, held,
  when). An agreement about a different Discord account than the one the account proves now counts
  as none. A pass compares each side's **held role** with that, never a time: the member row's
  change time moves with voice and name updates too, and a stamp taken when the agreement is
  written would hide a Discord change that landed between the plan's read and that write.

  | Discord vs agreed | Modbot vs agreed | Pass |
  |---|---|---|
  | same | same | nothing |
  | changed | same | Modbot follows Discord |
  | same | changed | Discord follows Modbot |
  | changed | changed | both moved the same way (one yes-or-no each), so they agree: written down |
  | no agreed state yet (first pass after saving) | | whichever side holds the role, the other is given it; the preview shows these first |
  | no agreed state, and the account is not in the server, or proved its Discord account after the mapping was saved | | Discord decides: a new or re-proven Discord account is never handed the role; one without it loses the Modbot role |

  After every change the agreed state is written in the same transaction as the fact. **That is
  what keeps it from bouncing:** when the bot gives or takes a Discord role, the pass writes that
  change into the stored member row as it makes it, so the row agrees before Discord's own update
  arrives, and that update then changes nothing. The write adds or removes that one role id inside
  the database in a single statement, never a read-change-write of the whole list, so a roles
  update the gateway's recorder makes at the same moment survives it. A Modbot role the pass gave
  matches the agreement on the Modbot side the same way. No change travels back to where it came from. (Written through,
  the member update that follows carries no role change, so the role fact for it is the pass's own
  `modbot.copy.role.give`/`.take`.)
- **Not in the server is holding no Discord role**, always, agreement or not: Discord decides, the
  Modbot role goes, and nothing is ever given in Discord to somebody not in the server.
- **Agreed as held means linked.** Whenever both sides agree the role is held, the Modbot role is
  marked as from Discord, so a role first given by hand in Modbot goes with the link like one
  Discord gave.
- **Leaving the server, unlinking Discord**: the Modbot role goes, as in §5. The Discord role stays
  as it is: once the Discord account is off the Modbot account, Modbot no longer acts for it in
  Discord. (The first draft took back a Discord role the bot had given, like M5 role sync; the
  account no longer names the Discord account by then, so it was dropped.) Disconnecting, and
  connecting any Discord account (the same or another), clears every agreement the account had, and
  so does the pass when it takes a role because the link is gone. Connecting the same Discord
  account again therefore follows Discord and leaves its role alone; connecting another one that
  lacks the role takes the Modbot role.
- **Protection is the same.** Only covered accounts (§4): proven Discord account, enabled, no
  Administrator role. Administrators and the owner are never given or taken anything on either side.
  A hand change in Modbot has already passed the role order rules (§3.5) for the person making it;
  the Discord half goes through only for the role this mapping names. The mapping's saver must
  outrank the Modbot role as in §2. Through a both-ways mapping, anybody who may hand out that
  Modbot role can hand out that one Discord role; that is what the direction means, and the screen
  names both roles side by side.
- Discord changes from the bot carry the audit log reason "Modbot: staff role set in Modbot".

## 4. Who is affected

An account is **covered** when all of these hold:

- it has a **proven** Discord account (`DiscordVerifiedAt` set). Typed-in ids never count here, not
  even before 1 November 2026: everywhere else a wrong typed id sends a message to the wrong person;
  here it would hand somebody permissions. The unique index on proven ids already means one Discord
  account is one Modbot account.
- it is enabled and not deleted;
- it holds **no** role that carries the Administrator permission. Administrators, and so the owner
  (`OwnerAccount`), are never given or taken anything.

Everyone else is left exactly as they are.

**No account is ever made automatically.** An account needs the person's own password, email and
VRChat link (§4.3), so Modbot cannot make one for them. The preview lists Discord members who hold a
mapped role with no Modbot account, and those whose Modbot account has no proven Discord account,
so an administrator knows whom to invite or ask to connect.

## 5. What the pass does

For each covered account and each Modbot role that has a mapping:

| Holds a mapped Discord role | Holds the Modbot role | Pass |
|---|---|---|
| yes | no | gives it, marked as from Discord |
| yes | yes | nothing (a hand-given one is marked as from Discord from now on) |
| no | yes, from Discord | takes it away |
| no | yes, given by hand | takes it away (decision 2: Discord fully decides) |

Leaving the server counts as holding no Discord role, and so does holding a Discord role the server
index has found deleted: a member row keeps a deleted role's id until that member's next update, so
deleting a linked Discord role takes its Modbot role from everybody who held it on the next pass
(brake permitting). **Unlinking Discord** (or proving a different
account) takes away every role the mapping gave, so removing the link is not a way to keep them.

`modbot_user_role` gains `from_discord` (bool). Removing a mapping takes nothing away; the roles it
gave become ordinary hand-given ones, and the preview says so.

A role change takes effect on the person's next request (§5 of accounts and access refreshes the
permissions claim), so nobody needs to sign in again.

**By hand.** On a covered account, giving or taking a role behind a *Discord decides* mapping (or a
both-ways one that is *Not set up*) by hand is refused with 400 "This role follows a Discord role.",
and the users page greys that role's box. Otherwise the next pass would quietly undo it. Behind a
working both-ways mapping it is allowed, and the next pass gives or takes the Discord role (§3.1).

## 6. How fast

In the existing sync loop (`DiscordSyncService`), once a minute, after the role and ban sync. The
member list is kept current by the gateway, so a role change in Discord shows within about a minute,
and a both-ways change made in Modbot reaches Discord within about a minute.

- **Only after this connection's catch-up has compared the member list.** Between connecting and
  the catch-up, `discord_member` holds roles from before the bot went away; acting on them would
  take roles from people who were given them meanwhile.
- **At most 50 account changes a pass**; the next pass carries on. **One pass at a time**: the
  minute loop and Apply go through the same gate.
- **A brake on taking away.** If one pass would take roles from at least 3 covered accounts and
  either more than 5 or more than half of them, on either side, it takes nothing (it still gives), puts a problem on the Discord health row, records one
  `modbot.role.discord.held` fact, and waits for somebody to press **Apply** on the staff roles screen
  after seeing the list. Giving is not braked. **Apply** is refused unless the person pressing it
  could make every change by hand (§3.5): each role given or taken below their highest role, and
  each account it changes too. The plan they were checked against is the plan the runner carries
  out, and Apply records `modbot.role.discord.apply` naming them.
- **Each change is checked again as it is made**: the account is still enabled, holds no
  Administrator role, and for a give still proves the same Discord account, the mapping still exists
  with the same Discord role, and the switch is still on; for a take, the role is still linked and
  the switch still on. A change in Discord also needs the person still in the server and the
  Discord role still one the bot may give with no power over the server. This catches a Discord role deleted by mistake, a
  server swapped in settings, or the bot losing sight of members.
- Each pass re-checks §2's Administrator rule: a mapped role later edited to carry Administrator is
  skipped, with a health problem, until the mapping is removed.

### 6.1 Only while member updates arrive

*Added 2026-10-02, after the live server's member list was found 17 days stale (member events
stop when the Server Members intent is off or refused).* Every decision here reads the member roles
Modbot has stored. If those stop changing while Discord's go on, somebody taken off staff in
Discord keeps their Modbot role. So the pass acts only while they are kept current:

- **Current** (`DiscordBotService.MemberUpdatesCurrent`): the bot is connected, the session asked
  for member events and Discord did not refuse them, and the member list was compared since it
  connected. A quiet server sends no member events, so their being allowed is as far as Modbot can
  see; that is the signal.
- The pass runs every minute with the bot down too. Each run notes the last time member updates
  were current (`discord_sync_state.staff_roles_members_current_at`). While they are not, it gives
  and takes nothing. Within 10 minutes of the last current run it says nothing (a reconnect); past
  that it sets `staff_roles_members_off_at`, puts "Modbot isn't receiving member updates from
  Discord." on the card and on the Discord health row, and records one
  `modbot.role.discord.no-member-updates` fact. When updates are back, it carries on.
- `StaffRoles.MemberUpdatesMissing` is the one rule the screens read: missing while that mark is
  set, or when no run has found them current in the last 20 minutes (the pass itself is not
  running). Then every link is **Not set up**, the card says why, the preview lists it as a
  problem, and every linked role is locked against hand changes, since the pass would otherwise
  undo them once updates return.

## 7. Facts

| Fact | When | Actor | Payload |
|---|---|---|---|
| `modbot.user.roles.change` (existing) | the pass gave or took a role | none (Modbot) | before/after names and ids as today, plus `why: "discord-role"`, the Discord user id and the Discord role ids that decided it |
| `modbot.copy.role.give` / `.take` (existing) | a both-ways pass gave or took the Discord role | none (Modbot) | as M5 role sync writes them, with the mapping id in place of the pair id and `why: "staff-role"` |
| `modbot.copy.failed` (existing) | Discord refused | none | the role, the person, Discord's error |
| `modbot.role.discord.map` | a mapping was saved or its direction changed | the saver | Modbot role id and name, Discord role id and name, direction, how many accounts the preview said would gain or lose |
| `modbot.role.discord.unmap` | a mapping was removed | the remover | the same, without counts |
| `modbot.role.discord.held` | the brake stopped a pass | none | how many would have lost a role |
| `modbot.role.discord.apply` | somebody pressed Apply | the person | given, taken, left, the problem if any |
| `modbot.role.discord.no-member-updates` | member updates went missing past the wait (§6.1) | none | when they were last current |
| `modbot.settings.change` (existing) | the switch turned on or off | the person | the switch |

All operational log, moderation retention, like every other account fact. Reusing the existing
roles fact means the audit log, held-roles history and Discord event routes see these changes with
no new code.

## 8. Preview before saving

`POST /api/staff-roles/preview` takes the mappings as they would be after the save and runs the pass
with `apply: false`: no change, no fact. It answers with:

- accounts that would **gain** a role, and which;
- accounts that would **lose** one, and whether it was from Discord or by hand;
- for both-ways mappings, accounts that would **get or lose the Discord role**, and whether the bot
  can (*Not set up*);
- Discord members holding a mapped role with **no Modbot account**;
- accounts with the Discord role whose Discord account is **not proven**.

The screen shows this list and saves only when confirmed. Saving with the switch off shows the same
list for when it is turned on. Removing a mapping takes nothing away, so it saves straight away.

### 8.1 Two screens, one list

*Decided 2026-10-02: "A and B".* The mappings are edited in both places, through the same
endpoints and the same checks, and each shows what the other made:

- **Settings → Discord → Modbot roles from Discord**: every mapping as *Discord role → Modbot role*
  with its direction, *Not set up* where it applies, an add form (Discord role, Modbot role, which
  way), the switch, and **Apply** while the brake holds. Shown to accounts with both permissions.
- **Settings → People and roles → Roles**: an open role lists its **Discord roles** under its
  permissions, with the same row and an add form without the Modbot role picker. Not shown for
  Administrator, and with nothing to add on a role the person cannot map. While the switch is off
  it shows *Off*.

## 9. Switch

`settings.discord_staff_roles_on`, **off by default**. Off: the pass gives and takes nothing; the
preview still works, because seeing what would happen is how somebody decides to turn it on.
Turning it on shows the preview first. Changing it takes Manage roles and Manage users.

Endpoints, all under Manage roles + Manage users:

| Operation | Endpoint |
|---|---|
| List mappings and the switch | `GET /api/staff-roles` |
| Preview | `POST /api/staff-roles/preview` |
| Add a mapping | `POST /api/staff-roles` |
| Change one (Discord role, Modbot role, direction) | `PUT /api/staff-roles/{id}` |
| Remove one | `DELETE /api/staff-roles/{id}` |
| Switch | `PUT /api/staff-roles/on` |
| Apply past the brake | `POST /api/staff-roles/apply` |

## 10. Decisions

Decided 2026-10-02:

1. Losing the Discord role takes the Modbot role only; the account stays enabled, even with no role.
2. Discord fully decides: a hand-given mapped role on a covered account without the Discord role is
   taken away, and the preview shows these first.
3. Gaining the Discord role gives the Modbot role automatically, within about a minute; no approval
   step. Anybody who can hand out a mapped Discord role in Discord can therefore give its Modbot
   role, which is why only explicitly made mappings exist (§2) and the §2 protections hold.
4. Both screens, backed by the same list (§8.1).
- Some mappings work both ways (§3.1).
- Only roles somebody maps sync, each chosen explicitly with its direction (§2).

## 11. Testing

Written with the build, run in the testing pass. Against real PostgreSQL with a scripted gateway:

- Saving refuses Administrator roles, roles carrying Administrator, roles at or above the saver,
  permissions the saver lacks, @everyone, managed and removed Discord roles; needs both permissions.
- A covered account gains the role when it holds the Discord role, loses it when it does not, and
  loses it when it leaves the server or unlinks Discord.
- Typed-in Discord ids, disabled accounts and administrators are never touched.
- Two Discord roles giving one Modbot role: losing one keeps it, losing both takes it.
- With no mappings, the switch on changes nothing and reads no member; an unmapped role is never
  given or taken.
- The switch off changes nothing; the preview answers anyway and writes no fact.
- Nothing happens before this connection's member list was compared.
- 50 changes a pass; the brake stops a large take-away and Apply carries it out.
- Every change writes `modbot.user.roles.change` with `why: "discord-role"` in the same transaction.
- Giving or taking a role behind a *Discord decides* mapping by hand on a covered account is refused.
- Both ways: a Modbot-side change gives or takes the Discord role; a Discord-side change reaches
  Modbot; the member update that follows the bot's own change does nothing (no bounce), nor does a
  pass before that update arrives; the first pass gives the missing side.
- Both ways refused when another mapping shares its Modbot or Discord role, for a Discord role in a
  VRChat role pair, and for a Discord role with power over the server; the database refuses two
  both-ways rows for one Modbot role.
- A held pass still gives; only taking waits for Apply. Apply is recorded and refused to somebody
  who could not take the roles away by hand.
- A Discord refusal makes the mapping Not set up everywhere, writes one failed fact, stops asking
  until a day has passed, and locks the role against hand changes.
- Both ways: somebody not in the server loses the Modbot role on the first pass, and somebody who
  left after an agreement loses a hand-given one with no call to Discord; proving a different
  Discord account without the role takes the Modbot role; connecting the same one again keeps the
  Discord role; a Discord change between the plan's read and the agreement's write is still seen; a
  Discord role that became powerful is never given; a hand-given role agreed both ways goes with the
  link. Apply is refused when an affected account is above the presser.
- Bot unable to give the role: *Not set up* reported, the Modbot-to-Discord half does nothing, hand
  changes are refused, Discord's changes still reach Modbot; a 403 at give time sets the same.
- Administrators are never given or taken a Discord role through a both-ways mapping.
