# Modbot — acting from Discord: right-click menus, forms and card buttons

- **Date:** 2026-10-01
- **Status:** Built
- **Covers:** three right-click menus in the Discord server, the forms they open, buttons under
  single-card log messages, and the confirmation step every action that changes VRChat goes
  through
- **Depends on:** proving which Discord account a staff member is (accounts and access §4.6,
  `StaffDiscord`), M4 moderation actions (§4 keys, §9 per-action reasons), notes, the event cards
  design (2026-09-23 §5, buttons allowed on a message that is one card), the own-server guard that
  `/me` brought (`IsForThisServer`, the `modbot:` button prefix)
- **Narrows:** nothing. Every rule the web app applies to these actions applies here unchanged.
  Who sees `/lookup` and `/recent` in Discord (their `default_member_permissions`) is left to the
  cards-and-commands work (TASK-039) running beside this; only the new right-click menus are set
  here (§10).

---

## 1. Why

Moderation teams live in Discord; the web app is where they go afterwards. Until now the bot could
read (`/lookup`, `/recent`) but nobody could act from Discord. A moderator who saw a card for a
warn, a kick or a join request had to open Modbot, find the person and press the same button there.

## 2. What a staff member can do

| Where | What | Needs (the web app's own permission) |
|---|---|---|
| Right-click a member → Apps → **Look up in Modbot** | A private card: the linked VRChat profile (the `/lookup` card), or, with no link, what Modbot has on the Discord account | See profiles |
| Right-click a member → Apps → **Add a note** | A form with one text box; the note is written about that Discord account | Write notes |
| Right-click a message → Apps → **Report this message** | A form with one text box; a note about the message's author, with the message kept in it | Write notes |
| Button **Add note** under a card | The same note form, about the card's person | Write notes |
| Button **Kick** / **Ban** under a card | A form with the group's reasons and a note, then a confirmation | Kick / Ban |
| Button **Approve** / **Reject** under a join request card | Reject: the reasons form; both: a confirmation | Answer join requests |
| Link button **Open case** under a ban card | Opens the case file in Modbot | (Modbot's own sign-in) |

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
`modbot.note.add` fact.

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

## 8. Reporting a message

**Report this message** writes a note about the message's author. The note's text is what the
moderator typed, then the channel, the time, the message's words (cut to fit the note's 2,000
characters) and a link to the message. The fact also keeps the message as data
(`reportedMessage`: id, channel, time, words, link), so a later case-file feature can lift it.

Discord hands the bot a right-clicked message's words even without the Message Content intent, so
this needs no new intent. A message from a bot, or from Modbot itself, cannot be reported.

## 9. Never on Modbot's own accounts

- The VRChat account Modbot signs in as: refused by the service at step 2 of §4.
- The bot's own Discord account: **Add a note** and **Look up in Modbot** on it, and **Report this
  message** on its messages, are refused.

## 10. Who sees the menus

The slash commands' own default permission is left to the work on cards and commands that runs
beside this; only the new menus are set here.

The menus are registered with **Timeout Members** as their default Discord permission
(`default_member_permissions`), so ordinary members do not see them in the Apps menu. A server admin
can change who sees them under Server Settings → Integrations → the bot. This only hides them:
Modbot's own permission check (§3) is what decides.

## 11. The gateway

Discord wants an answer within three seconds, and a form must be that first answer: it cannot follow
a "thinking…" reply. So right-click menus, buttons and form submissions are no longer deferred before
the handler runs. The handler shows a form, replies, or changes the message the button sits on; if it
has done none of these after two seconds, the gateway defers on its behalf and the answer arrives as
a follow-up. Slash commands still defer first, as before.

The own-server guard runs before any of this, unchanged: a menu, a button or a form from another
server, or whose id does not start with `modbot:`, is left completely alone.

## 12. Not in this version

- Unban from a card. The web app offers Unban instead of Ban to somebody banned; the cards keep to
  Ban, Kick, Approve and Reject.
- Buttons under `/lookup`'s reply (the right-click lookup has them).
- A switch to hide the buttons from a channel members can read. Pressing one without the permission
  is refused, so a visible button is only a visible button.
- Discord-side actions (a Discord ban or timeout by hand) from a button: the web app has none either.
