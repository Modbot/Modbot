# Modbot — acting from Discord: right-click menus, forms and card buttons

- **Date:** 2026-10-01
- **Status:** Built
- **Covers:** two right-click menus in the Discord server, the forms they open, buttons under
  single-card log messages, and the confirmation step every action that changes VRChat goes
  through
- **Depends on:** proving which Discord account a staff member is (accounts and access §4.6,
  `StaffDiscord`), M4 moderation actions (§4 keys, §9 per-action reasons), notes, the event cards
  design (2026-09-23 §5, buttons allowed on a message that is one card), the own-server guard that
  `/me` brought (`IsForThisServer`, the `modbot:` button prefix)
- **Narrows:** nothing. Every rule the web app applies to these actions applies here unchanged.
- **Updated 2026-10-03,** when this was brought onto staging with the cards-and-commands work
  (TASK-039) as step 0 of the Discord commands design: **Report this message** is gone (§8), the
  menus and the staff slash commands share one `StaffOnly` setting (§10), and the join gate's
  buttons are still acknowledged first (§11).
- **Updated 2026-10-07,** with `/gate` and `/events` (Discord commands design §3.2, §3.6 and §3.7, steps 4 and 5):
  the join gate from Discord, and a public list of the next events for members (§14).
- **Updated 2026-10-07,** with `/ban` and `/kick` (Discord commands design §3.3, step 3): the same form,
  confirmation and acting once, started from a typed command and able to act on the Discord server (§13).
- **Updated 2026-10-07,** step 2 of the Discord commands design: `/note`, `/watch` and `/live` are
  typed commands beside the menus (§2), with `IStaffActions.StartWatchAsync` (§5).

---

## 1. Why

Moderation teams live in Discord; the web app is where they go afterwards. Until now the bot could
read (`/lookup`, `/recent`) but nobody could act from Discord. A moderator who saw a card for a
warn, a kick or a join request had to open Modbot, find the person and press the same button there.

## 2. What a staff member can do

| Where | What | Needs (the web app's own permission) |
|---|---|---|
| Right-click a member → Apps → **Look up in Modbot** | A private card: exactly what `/lookup discord:` shows for that member, the linked VRChat profile or the Discord account's own story, and only what `/lookup` would show this caller (notes need See the audit log, join requests need See join requests; changed 2026-10-03, the first version counted notes for anybody with See profiles) | See profiles |
| Right-click a member → Apps → **Add a note** | A form with one text box; the note is written about that Discord account | Write notes |
| Button **Add note** under a card | The same note form, about the card's person | Write notes |
| Button **Kick** / **Ban** under a card | A form with the group's reasons and a note, then a confirmation | Kick / Ban |
| Button **Approve** / **Reject** under a join request card | Reject: the reasons form; both: a confirmation | Answer join requests |
| Link button **Open case** under a ban card | Opens the case file in Modbot | (Modbot's own sign-in) |

| `/note` (added 2026-10-07) | `text` (1 to 2,000) and exactly one of `member` or `vrchat` (a VRChat name or id, with suggestions); "Note saved." and an **Open in Modbot** link | Write notes |
| `/watch` (added 2026-10-07) | `reason` (1 to 200), `for` (1 day, 1 week, 30 days, Until stopped), `follow-up` (None, Tomorrow, In a week), and one of `member` or `vrchat`; "Watching …" and an **Open in Modbot** link | Write notes |
| `/live` (added 2026-10-07) | A private card for each open group instance: world, people (`12/40`), when it opened; names (at most 20, then "and N more") only while a moderator's companion is watching it, by the Live page's rule (`InstancePeopleReader`); an **Open Live** link; no flags and no watched tags (user decision 2026-10-01) | See live instances |

The lookup reply carries **Add note**, **Kick** and **Ban** too, for a member linked to VRChat, so a
moderator can go from a right-click to a ban without leaving Discord.

Only the buttons the moderator could use on the web app are shown on a private reply. A card in a
channel is read by everybody in it, so its buttons are the same for everybody, and a press by
somebody who may not use one is refused with the permission's name.

## 3. Who is acting

Every interaction resolves the presser through `StaffDiscord.AccountForAsync` before anything else,
exactly as the slash commands do: a proven Discord account, or until 1 November 2026 a typed one
held by nobody else. Then, in this order:

1. no account → "Connect Discord on your account page in Modbot first."
2. disabled → "Your Modbot account is disabled."
3. anything that writes (a note, an action) and the account has no VRChat link → "Link your VRChat
   account in Modbot first." The web app refuses every request from such an account
   (`VRChatLinkedRequirement`); the lookup follows `/lookup`, which never asked for it.
4. missing permission → "You need the "Ban" permission in Modbot." (the role editor's label)

The check runs again when a form is sent and again on the confirmation press: a role taken away
between the press and the confirmation is a refusal, not a ban.

## 4. The confirmation, and acting once

A press on **Ban** never bans. The order is:

1. **Button** → the form: the reasons offered for that action (switched on, marked for it, at most
   the first 25 by the list's order, which is Discord's limit for one list), and a note. A reason is
   required where the web app requires one: always for a ban, and for kick and reject when the
   group's "require a reason" switch is on.
2. **Form sent** → the same up-front checks the service runs before it sends anything
   (`ModerationActionService.CheckAsync`): the group is set up, the person is not Modbot's own
   VRChat account, the reasons are on the list, switched on and marked for the action, and a reason
   that needs a written note has one. A refusal is said here, before the confirmation.
3. **Confirmation** → a private message: "Ban **name**?", the reasons and the note, a red **Ban**
   and a **Cancel**.
4. **Ban pressed** → the message changes to "Banning **name**…" with no buttons, then to the answer.

**One confirmation acts once.** The confirmation's token is the service's key (`discord:<token>`),
so a second press — two clients, a retry, a fast double tap — finds the claimed row and gets the
first answer back (M4 §4.3). Removing the buttons at the first press is a courtesy; the key is the
guarantee.

The waiting confirmations are held in memory for 15 minutes, which is as long as Discord lets a
reply be changed anyway. A restart forgets them: the confirmation then says it has run out, and the
moderator presses the card's button again. A per-process cap keeps one person from filling memory.

Approve takes no reasons, so its button goes straight to the confirmation.

## 5. Same service, same facts

Every action runs through `ModerationActionService` with the staff account as the caller, built
exactly as the web endpoint builds it (the case-file writer, the profile refresh, the linked Discord
ban for ban and unban; the join request reader for approve and reject). So a ban from Discord writes
the same `modbot.action.ban` fact, the same case file from the reasons and the note, and bans the
linked Discord account the same way. A note goes through `NoteService` and is the same
`modbot.note.add` fact. A watch (`/watch`, added 2026-10-07) goes through `WatchService.StartAsync`
by `IStaffActions.StartWatchAsync`, built as the DI registration builds it, and is the same
watch fact; the service checks Write notes again and refuses a person who is already watched in
its own words, which the reply repeats. `/note` and `/watch` follow §3's order (account,
disabled, VRChat link, permission) and write the `modbot.discord.command` fact with the person
under `target` (VRChat) or `targetDiscord` (Discord) and an outcome of `answered`, `refused` (the
service said no) or `invalid` (no person, two people, an empty note).

**A VRChat name is a profile's** (review, 2026-10-07). The web app shows a stored VRChat name only
with See profiles (`PersonSight.VRChatName`), and Write notes does not include it. So for a caller
without See profiles `/note` and `/watch` suggest nothing, search no name, and name nobody in a
reply: `vrchat` takes an exact id Modbot already holds, and anything else (a name, a typo, an id
Modbot has never seen) gets one sentence that quotes nothing back. Callers with See profiles keep
the name search, the list of several matches and the name in the reply.

On top of those, every interaction writes a `modbot.discord.command` fact like a slash command does
(`command`: the menu or the action, `outcome`, `target`), so the audit log says the ban came through
Discord.

## 6. The card after an action

A ban, kick, approve or reject started from a card in a channel adds one line above that card,
"Banned by **alice**", and takes the card's Kick, Ban, Approve and Reject buttons away; **Add note**
and **Open case** stay. This is the one thing that is not private: the channel sees that somebody
dealt with it, which is what stops a second moderator starting the same ban. Everything else —
forms, confirmations, answers, lookups — is visible only to the person who pressed.

The line is the message's own text, above the card, rather than a field inside it: the card's
embed and its picture are left exactly as they were posted. Rewriting the embed would mean sending
it back with the picture's Discord address, which Discord signs and lets run out. A second action
on the same card adds a second line.

**Repeats folded into a card** (Discord event repeats design, added 2026-10-03 when this met it on
staging). A repeat of the same change is written into the post before it by rewriting that message
whole, buttons included, so the fold sends the card's buttons again rather than taking them away.
And a card somebody acted on takes no more repeats: rewriting it would bring back the buttons the
action removed. The fold (`IDiscordGateway.FoldRepeatAsync`) never writes the message's text, so it
cannot lose the line, and it reads the card just before rewriting it and refuses one that has a
line, so a confirmation that lands in the middle of a pass is still seen; the repeat then starts a
post of its own. Marking a card also forgets it as the channel's post for repeats, which saves the
next pass that read.

## 7. Which cards carry buttons

**Only a message that is one card.** Discord puts buttons under a message, not under an embed, and
the log batches up to ten cards in one message when it is catching up. A row of buttons under ten
cards cannot say which card it acts on, so a batched message carries none. A quiet log posts one
card per message, which is when the buttons matter.

| The card's person | Buttons |
|---|---|
| A VRChat person, on a card about them (joins, leaves, warns, kicks, roles, profile changes, flags, notes, Modbot's own actions) | Add note, Kick, Ban |
| A VRChat person on a ban card | Add note, and Open case when a case file covers the ban |
| A VRChat person who asked to join | Approve, Reject, Add note |
| A Discord account (member events, timeouts, Discord bans, notes, flags on Discord messages) | Add note |
| Anything else (instances, the group, settings, worlds) | none |

Kick is offered on every person card because whether somebody is still a member changes between the
post and the press; VRChat answers a kick of a non-member with "they are not in the group".

A button id is `modbot:` plus the action and the id. An id that would make it longer than Discord's
100 characters leaves that button off rather than cut an id.

## 8. Reporting a message (dropped 2026-10-03)

This version had a staff-only message menu, **Report this message**, which wrote a note about the
message's author with the message kept in it. It was taken out before it shipped (Discord commands
design, decision 3): a **Report to mods** menu open to every member replaces it in a later step, and
two report menus side by side would confuse staff. A note can be made from that report.

The gateway keeps what a message menu needs (`DiscordCommandKind.Message`, `DiscordTargetMessage`)
for that menu. No message menu is registered until then, so Discord sends none.

## 9. Never on Modbot's own accounts

- The VRChat account Modbot signs in as: refused by the service at step 2 of §4.
- The bot's own Discord account: **Add a note** and **Look up in Modbot** on it are refused.

## 10. Who sees the menus

The menus are registered with **Timeout Members** as their default Discord permission
(`default_member_permissions`), so ordinary members do not see them in the Apps menu. A server admin
can change who sees them under Server Settings → Integrations → the bot. This only hides them:
Modbot's own permission check (§3) is what decides.

The staff slash commands (`/lookup`, `/recent`, `/modbot`, and from 2026-10-07 `/note`, `/watch` and `/live`) are hidden the same way, by the same
`DiscordCommandDefinition.StaffOnly` (cards and commands, TASK-039): one setting, one Discord
permission, for every staff command and menu.

## 11. The gateway

Discord wants an answer within three seconds, and a form must be that first answer: it cannot follow
a "thinking…" reply. So right-click menus, the staff buttons (§7's, and a confirmation's) and form
submissions are no longer deferred before the handler runs. The handler shows a form, replies, or
changes the message the button sits on; if it has done none of these after two seconds, the gateway
defers on its behalf and the answer arrives as a follow-up. Slash commands went the same way
(changed 2026-10-07, Discord commands design §3.2): they used to defer first, and now answer through
the same machinery (`DiscordInteractionAnswer`), so a slash command can open a form as its first
answer. The two-second acknowledgement is as public or private as the command's definition says
(`DiscordCommandDefinition.Reply`), read before anything is acknowledged, because Discord fixes a
reply's audience when the interaction is acknowledged.

**Every other button is still acknowledged the moment it arrives** (added 2026-10-03, when this was
brought onto staging beside the join gate). The join gate's buttons -- a member's Get in, I agree and
Check, in the server or in the direct message the gate sent, and staff's Hold, Lift hold and Pause
invites on an alert -- and `/me`'s never show a form, and the gate takes a lock and reads the
settings, the member and their row before it answers; waiting two seconds first would leave it one.
`DiscordNetGateway.AnswersInPlace` says which presses wait. The bot routes a press to the join gate
first, then to the staff buttons, then to `/me`'s.

The own-server guard runs before any of this, unchanged: a menu, a button or a form from another
server, or whose id does not start with `modbot:`, is left completely alone. A press in a direct
message is this Modbot's only when its id ends with this server's mark (`@<server id>`), which the
join gate's buttons and a reminder's Stop (§15) carry.

## 12. Not in this version

- Unban from a card. The web app offers Unban instead of Ban to somebody banned; the cards keep to
  Ban, Kick, Approve and Reject.
- Buttons under `/lookup`'s reply (the right-click lookup has them).
- A switch to hide the buttons from a channel members can read. Pressing one without the permission
  is refused, so a visible button is only a visible button.
- Discord-side actions (a Discord ban or removal) from a button under a card. The web app has no button
  for them; they are done with `/ban` and `/kick` (§13). A Discord timeout has no command yet.

## 13. `/ban` and `/kick` (added 2026-10-07)

Discord commands design §3.3 and decisions 4 and 5. Everything in §3 to §5 and §11 applies unchanged: who is
acting, a form, a confirmation, the key, the facts. What is new is that a typed command starts the flow, and
that the flow can act on the Discord server.

### What each one means

**`/ban` is the web app's ban.** `member` (picked from Discord's list) or `vrchat` (a name or an id), exactly one.

| Who is named | What is banned | Needs |
|---|---|---|
| A VRChat person | The VRChat group; the moderation service bans their linked Discord account too (`AlsoInDiscordAsync`, whatever the ban sync switches say) | Ban |
| A member linked to VRChat | The same, for their linked VRChat account | Ban |
| A member with no VRChat link | The Discord server only | Ban on Discord |

The confirmation says which: "Ban **name** from the VRChat group and the Discord server?" when a linked Discord
account goes too, "Ban **name** from the Discord server?" for the server alone, and the card button's "Ban **name**
from the group?" for a VRChat person with no link.

**`/kick` goes by who is named**: a member is removed from the server (Remove from Discord), a VRChat person is
kicked from the group (Kick). `from` (Discord server / VRChat group / Both) picks the other place for somebody with
both accounts linked; one without the account asked for is refused in plain words. **Both** is one confirmation
and needs both permissions.

### The flow

1. The command checks, in §3's order, an account, enabled, a VRChat link (both commands write), then that the
   caller holds either of the two permissions (`DiscordCommands.CanUse`). Which one a run needs depends on who is
   named and where it acts, so the exact permission is checked once that is known (`PendingStaffAction.Needs`), and
   again when the form is sent and when the confirmation is pressed. The permission is checked before anything
   about what the person has linked, so nobody is told about an account in a place they may not act in.
2. The bot's own account is refused (§9). For the Discord server the service's own refusals come before any form:
   the bot, the server's owner and any Discord account linked to a Modbot staff account (TASK-058), the same three
   as the API, from the same code.
3. **The form.** A group action has the group's reasons and a note, as under a card. A Discord ban has a reason
   (required, as a group ban always needs one) and "Delete their messages from": None, 1 day or 7 days. A Discord
   removal has an optional reason.
4. **The confirmation**, red, with Cancel, then "Banning…" or "Removing…", then the answer. A Discord action has
   no key to claim in the moderation service, so the token claims it in `PendingStaffActions.RunOnceAsync`: the
   first press runs every place the action acts, and any later press, even a simultaneous one, gets those answers
   again and sends nothing. A group-only action keeps the service's `discord:<token>` key as before.
5. **Both places answer on their own.** A group kick that VRChat refuses does not stop the Discord removal, and a
   refusal from Discord does not undo the group kick. Each says what happened on its own line.
6. A 429 on `groups.moderate` is reported as VRChat said it and never retried (§4.3.1 of the foundation spec).

### One service for the Discord server

The four `/api/discord` endpoints used to hold their rules in a private method. It is now
`DiscordMemberActionService` (Api), which the endpoints and `IStaffActions` (`DiscordCheckAsync`,
`DiscordBanAsync`, `DiscordKickAsync`) both call, so the refusals, the audit-log reason ("Modbot: banned by
<account>: <reason>"), the fact (`modbot.action.discord.ban` / `.kick`, written only when Discord changed
something) and the order of the checks cannot drift. Every status, sentence and code the endpoints gave is kept.

### Privacy

A VRChat display name is a profile's: the web app shows it only with See profiles (`PersonSight.VRChatName`), and
Ban, Kick and Write notes do not include it. So `/ban` and `/kick` use the one helper `/note` and `/watch` use
(`StaffCommands.PersonAsync`):

- Without See profiles the `vrchat` option takes an exact id Modbot holds. A name, a typo and an unknown id get
  one identical sentence that quotes nothing back, and no name is searched.
- The form's title and the confirmation then show the id the caller typed, never the person's VRChat name.
  A Discord member is shown by the name Discord showed in its picker (`discord_member.display_name`), also when
  they are linked: the linked VRChat name is never used.
- The suggestions under `vrchat` need See profiles, and the permission for acting on a VRChat person (Ban for
  `/ban`, Kick for `/kick`).

One thing the design makes visible: the confirmation's wording and the permission that is asked for depend on
whether a member is linked to VRChat, so a caller who holds only one of the two permissions learns whether the
member is linked from which permission they are told they lack. The link table is not otherwise readable
without a profile permission; this follows from decision 4's one meaning for `/ban`.

### Facts

The same as §5. A finished or refused step writes `modbot.discord.command` with `command` (`ban` or `kick`), an
`outcome` (`done`, `repeat`, `partial` when only one of two places did it, `refused`, `failed`, `error`, `off`,
`invalid`, `no-permission`, ...), `target` for the VRChat account and `targetDiscord` for the Discord one. A ban or
removal on the server also writes `modbot.action.discord.ban` / `.kick` under the moderator's name.

## 14. `/gate` and `/events` (added 2026-10-07)

Discord commands design §3.2, §3.6, §3.7 and decisions 6, 8 and 12. `/gate` is a staff command like `/ban`;
`/events` is the first command that answers in public and the first for members with no Modbot account.

### `/gate`

Shown to moderators, on by default, private. It needs Manage the join gate, and the steps that write
(`let-in`, `hold`, `lift`) need a VRChat account linked, as everything in the web app does. The checks run in §3's
order, and write the `modbot.discord.command` fact with `command` `gate`.

| Step | What it does | Code it calls |
|---|---|---|
| `waiting` | Up to 10 people at the gate, oldest first: name, when they joined, a **Let in** button each, and "and N more". | `discord_gate_entry`, as the web app's list reads it |
| `let-in member:` | Gives them the member role now. | `JoinGate.LetInAsync` |
| `hold` | Holds new joiners at once. The reply has **Lift hold**. | `JoinGate.HoldAsync` |
| `lift` | Lifts the hold. | `JoinGate.LiftHoldAsync` |

- **They are the gate's own actions.** The facts (`discord.gate.let-in`, `.held`, `.hold-lifted`), the lock the
  passes and presses share, and the refusals ("The join gate is not on.", "That person is not at the join gate.")
  are the ones the web app and the alert's buttons already give.
- **Hold asks for nothing first** (decision 12), as the alert's button does. Both build the reply from one method,
  `JoinGate.HeldReplyAsync`, so the **Lift hold** button is the same.
- **The Let in button is new, and it is the gate's.** The gate had Hold, Lift hold and Pause invites for staff and
  no button that names a person. It is `modbot:gate:letin:<Discord id>`, under the gate's prefix, so the bot's
  routing sends it to `JoinGate.PressAsync` before anything else and it is acknowledged the moment it arrives, as
  the others are. A press checks the presser's account and Manage the join gate again.
- **`waiting` also needs See members.** The web app shows who is at the gate only with See members (it says so on
  the card), and the bot shows no more than the web app. It shows no linked VRChat account and no 18+ check, which
  need See profiles there.
- A name is shown as text, escaped, and a button's label is "Let in" and the name, cut to 40 characters.

### `/events`

Shown to everyone, **off by default** (decision 6), answers in **public** unless the member sets `private` to yes.
No Modbot account is needed. Written to the access record with the caller's Discord id and no account.

- **Which dates.** The next 5 dates in the next 14 days, from `CalendarRepeat.Between`, so a date moved or
  cancelled on its own is right, and a date with its own title shows it.
- **Which events** (decision 8). Scheduled or open, not deleted, and **published in this Discord**: a
  `calendar_event_place` row for the Discord event or the channel post in state `published`, with the event's own
  switch for that place still on (the row can be a pass behind a switch turned off). Never a draft, a cancelled or
  finished event, or one that was never posted. The answer therefore says nothing the server does not already show.
- **A line** is `**Title** · <t:unix:f> (<t:unix:R>)`, then the world's name when Modbot knows it, then "Open now"
  while the event is open and this date is the open one. The title is escaped. Times are Discord's own timestamps,
  so each reader sees their own time zone.
- **Join buttons**, at most 5, only when Who can join is Anyone **and** the event's instance is open and not closed
  (`CalendarJoinLink.OpenAsync`, which the Discord event and the channel post use too). The link is
  `InstanceJoinLink.For(location)`, VRChat's launch page, so it works without a public address. An event only
  members, or members and their friends, can join is listed with no button.
- **The 60-second channel rule.** After a public `/events` in a channel, a second one there inside 60 seconds is
  answered in private. Public or private is fixed when the interaction is acknowledged, so the gateway decides
  **before** it acknowledges: `DiscordNetGateway.RepliesInPublic(…, channel, window, now)` asks
  `PublicReplyWindow.TryTake`, which also takes the window for a public one. A private ask does not take it, and a
  refused try does not extend it. The window is kept in memory per process, like the member limits, so a restart
  forgets it. It comes from the command's definition (`DiscordCommandDefinition.PublicOncePer`), not from a
  special case. If nothing says which channel, the answer is not limited.
- **A member limit** of 5 a minute per Discord account (`MemberCommandLimits`, its own instance under the key
  `events`, so `/me` and `/verify` do not use it up). A refused run answers "Slow down. Try again in a minute." and is
  not recorded, as with `/me`.
- **A late run of a switched-off `/events`** is told so by the generic switch check. Because the acknowledgement
  was made before the check, that sentence is public too; the command is not registered while it is off, so this
  is only the minute before Discord drops it.

### Facts

`modbot.discord.command` as in §5 for both commands. For `/gate`: `outcome` is `answered`, `refused`, `invalid`,
`no-permission`, `no-vrchat`, `not-linked`, `disabled` or `off`, and `targetDiscord` names the member let in. For
`/events`: `answered` or `off`, with no target.

## 15. `/remindme` (added 2026-10-07)

Discord commands design §3.5, §4 and §5, and decisions 6 and 9. A member asks for one direct message before the
next date of an event. For every member, no Modbot account, private, **off by default**.

### What it does

| Run | Result |
|---|---|
| `/remindme event: before:` | Stores one reminder and sends one confirmation direct message, "I'll remind you about **Title** at <t:f>. It starts <t:f>.", with **Stop**: the first time is when the reminder is due, the second when the date starts. The private reply is "I'll remind you <t:f>." (the due time). |
| `/remindme` (no event) | Lists the member's waiting reminders, soonest first, each with a **Stop** button ("Stop 1", "Stop 2"... when there is more than one). |
| 50007 on the confirmation | "Your direct messages are closed, so Modbot can't remind you." and **nothing is stored**: the row is written in a transaction that is rolled back unless the confirmation went out. Any other failure to send gets "Modbot could not send you a message." and stores nothing too. |

- `event` suggests, and accepts, only what `/events` would show (`EventsCommand.ListedAsync`, one query for both):
  scheduled or open, not deleted, published in this Discord. It is the event's id as the value; a title typed by
  hand also works when exactly one listed event has it. The suggestion is the event's next date that has not
  started, soonest first, at most 25, and only for a caller whose switch is on. Suggestions are not facts.
- `before` is **15 minutes**, **1 hour** (the default) or **1 day**.
- **Running it is the opt-in** (decision 6), for that one reminder. It signs nobody up for event invites, and
  stopping event invites under `/me` does not stop a reminder.
- **Repeating events** (decision 9): the next date only. The next date is the first one that starts after now,
  by `CalendarRepeat.Between`, so a date running now is not it.
- **Refused**, in plain words and with nothing stored: "Modbot can't find that event.", "That event has no dates
  left.", "That is too close to the start. Pick a shorter time." (the time it would be sent is more than 15 minutes
  ago, so it never would be), "You already have a reminder for that date." and "You already have 10 reminders."
- **The ten-waiting limit and the one-per-date rule are checked under a per-member lock** (`pg_advisory_xact_lock`
  on the member's id, held for the sign-up's transaction), so two runs at once cannot both count nine and both add
  a tenth.
- **Member limit** of 5 a minute per Discord account, its own `MemberCommandLimits` under the key `remindme`.
  A refused run answers "Slow down. Try again in a minute." and is not recorded. Stop is not counted: it only
  ever makes less happen.
- Recorded as `modbot.discord.command` with no Modbot account: `outcome` is `answered`, `listed`, `dms-closed`,
  `failed`, `no-event`, `no-dates`, `too-close`, `duplicate`, `limit` or `off`.

### The table

`event_reminder` (`EventReminder`): Discord id, event id (cascades with the event), the date's **planned** start,
minutes before, remind at, state, created, updated, sent and stopped times. The state is `waiting`, `sending`,
`sent`, `stopped`, `skipped` or `failed`. A unique index over the **waiting** rows says one per member per event
date; ten waiting per member is checked when asking. A date is named by its planned start
(`CalendarOccurrence.PlannedStartsAt`), the name it keeps when it is moved on its own.

### The sending pass

`EventReminderMessages.RunOnceAsync`, run from `CalendarDiscordService`'s loop (so only while the bot is
connected), in a `try` of its own so neither it nor the calendar's pass holds the other up.

1. Deletes rows that are over, so no Discord id is kept without an end: `sent`, `stopped`, `skipped` and `failed`
   rows 30 days after they last changed, a row stuck `sending` (the process ended in the middle) after a day, and a
   `waiting` row 30 days after it was due (nobody handled it: the command was off). This runs whether or not the
   command is on.
2. Does nothing more while `/remindme` is switched off: the operator's switch stops the messages as well as the
   command. **Stop** always works.
3. Looks at the waiting reminders due within the next 2 days (at most 500), because a date moved earlier brings
   its reminder forward. For each, the date is worked out as it stands now (`CalendarRepeat.ForDate`):
   - the event is no longer one `/events` would show, or the date was cancelled: `skipped`. The listing rule is the
     one query `EventsCommand.Listed`, asked again here, so a message never names (or links) an event that was taken
     off Discord, cancelled, unpublished or deleted after the member asked;
   - the time is worked out again from where the date is: a moved date is followed, and a reminder not due yet is
     left waiting with its new time;
   - the date has started, or the reminder is more than **15 minutes late** (a bot that was off): `skipped`.
4. A due reminder is marked with one statement, `waiting` to `sending`, **before** the message goes out. A crash
   between the two leaves it `sending`: counted as sent and never sent again. The same statement is how **Stop**
   works (`waiting` to `stopped`), so a press as the message goes out either stops it or finds it sent.
5. 2 seconds between messages (`CalendarInviteMessages.Between`), none before the first, at most 5 a pass.
6. The message is "**Title** starts <t:f> (<t:R>)." (the date's own title when it has one, escaped). A **Join** link
   button only when Who can join is Anyone, this date is the one the event has open, and its instance is not
   closed (`CalendarJoinLink.ForAnyoneAsync`, the rule `/events` uses). `sent` when Discord took it; `failed` when
   it did not (direct messages closed, or a refusal), never tried again.

### Stop

`modbot:remind:stop:<reminder id>@<server id>`, on the confirmation and on each line of the list. A press in a
direct message names no server, so the mark is how this Modbot, and no other sharing the bot, answers it
(`DiscordNetGateway.IsOurButton`); it is **acknowledged the moment it arrives** like every button that never shows a
form (`AnswersInPlace` is false), and the bot's routing hands it to `DiscordCommandHandler.HandleButtonAsync`
after the join gate and the staff buttons. Only the member the reminder belongs to can stop it. "Reminder stopped."
for a waiting or already stopped reminder; "Nothing to stop." for one that was sent, skipped, failed, is being sent,
or is not theirs. The reminder message itself carries no Stop: for a repeating event it would stop nothing.

### Privacy

Stored: the member's Discord id, the event, the date, when it is due and its state. No name, no message text. Kept
until it is over (30 days after it finishes, a day if stuck sending, 30 days after it was due if never handled); a person purge deletes every row for the Discord account, and for a VRChat account
the rows of the Discord account linked to it, counted in the purge fact as `eventReminders`. Leaves the server:
two direct messages through Discord to that member. Nothing goes to Modbot Cloud. Documented in `bot-setup.mdx` and
`privacy.mdx`.

