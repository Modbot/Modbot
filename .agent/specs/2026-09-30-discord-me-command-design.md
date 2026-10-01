# Modbot — `/me`: what a member can see about themselves

- **Date:** 2026-09-30
- **Status:** Built
- **Covers:** a private `/me` slash command for every member of the Discord server, its "What Modbot
  keeps" list, and the "Ask to delete my data" request that reaches the staff
- **Depends on:** Discord account linking (2026-09-15), M5 role and ban sync (2026-09-11), reviews
  (spec 5.8.5), the purge tool, the "settings changed" entry every settings save writes
- **Narrows:** M5 §8, which lists "Discord-side appeals or ticket UI" as a non-goal. `/me` is not an
  appeal: it shows a member their own standing and lets them ask for deletion, and it asks nothing
  of the staff beyond a review. Appeals stay a non-goal until the case-file rework gives a banned
  person a reason written for them.

---

## 1. What this adds

A member of the community types `/me` in the Discord server and sees, privately, what the group's
Modbot holds about them:

| Field | Source |
|---|---|
| VRChat | the current account link for their Discord id (an ended link does not count), and the VRChat name Modbot has stored |
| Discord | the username Discord sends with the interaction |
| Member since | the group membership row of the linked VRChat account, while they are a member; "Not in the group" otherwise; left out when there is no link |
| Roles from Modbot | the linked and 18+ roles recorded on the link, and the role-sync roles Modbot's own copy records say it gave and has not taken back, while the member list says they still hold them |
| Standing | "Banned" when the group ban list or the Discord ban list has a standing ban for them, else "Good" |

Under the card: **What Modbot keeps** (a fixed list), **Ask to delete my data**, and, for a member
with no link while linking is set up, **Link your VRChat account**.

## 2. What it never shows

Only rows whose subject is the person who ran it are read. Never:

- anybody else, or who they were in an instance with (co-presence);
- who reported them, or anything a moderator wrote (notes, case-file text, ban reasons);
- flags, the words that matched, evidence;
- a ban's reason, rule or case file. Standing is two words and nothing more in this version.

No VRChat request is made. Everything comes from stored rows, so a member running `/me` a hundred
times costs the VRChat budget nothing.

## 3. What Modbot keeps

A fixed list that follows the privacy policy's "What does Modbot record about me?" heading for
heading and item for item, in shorter words: nothing the policy names is left out, the AI call log,
who opened evidence and sign-in addresses included, because the point is to say plainly what is
stored. Its title, "What Modbot can keep", carries the "at most": no line under it explains that
(no explanatory text in the UI). It is the same for everybody, because it describes what the software can keep, not what this group
holds: listing a person's actual records is a different job, and most of those records name other
people.

The list lives in one place, `WhatModbotKeeps` in `src/Modbot.Discord/Commands`. The docs page lists
the same lines word for word, and a test reads the docs page and fails when a line is missing. When
the privacy policy's list changes, both change with it.

## 4. Ask to delete my data

A request, never a deletion. Pressing it:

1. opens a review with the signal `data-deletion`. The review's "moderator" is the Discord account
   that asked, and `About` is the same id, so the open-review index already allows **one open
   request per account**; a second press says it was already asked. The evidence carries the
   Discord id and name and, when linked, the VRChat id and name.
2. writes a `modbot.member.delete-asked` fact, subject the Discord account, beside the
   `modbot.review.opened` the review writes. Both commit with the review or not at all.
3. posts one line to the alerts channel (Settings → AI → Alerts), when one is set, with a button to
   the review when a public address is set. A failed post is logged; the review is already there.
4. tells the member: "Sent to the group's staff. Case files are kept." The purge keeps case files by
   design, so the member is told so rather than promised everything goes.

At most 20 requests in any 24 hours across the server (the reviews opened in the last 24 hours, a
rolling window rather than a calendar day, so the reply says "Try again later", not "tomorrow"). The
Reviews page is where staff work, and a flood of requests must not bury the rest of it.

The staff answer with the purge tool and close the review with a note. Nothing here deletes.

## 5. The switch

**Members can use /me**, on the Account linking card under Settings → Discord, because `/me` rests on
the link. **Off by default**, for new and existing deployments alike: a command every member can run
is the operator's choice. Saving writes the usual "settings changed" entry (`discordLinking`,
field `meCommand`).

While off, `/me` is **not registered** on the server. That was chosen over "registered and
answering that it is off": a member should not see a command that would only refuse them. The bot
compares the switch with what it registered on every settings poll and registers the commands again
when they differ, so the switch takes effect in seconds without a reconnect. A call that arrives in
the moment between the switch going off and Discord dropping the command, and a press on a button
under an old reply, are answered "/me is turned off on this server."

**Demo mode.** A demo serves every visitor as an administrator and never starts the Discord bot, so
`/me` cannot run there. The switch refuses to turn on in a demo as well, and the command checks
demo mode before it answers, in case the bot is ever started in one.

## 6. Limits and records

- 5 uses of `/me` and its buttons per Discord account per minute, counted in memory. A use over the
  limit is refused and not recorded, so holding a key down cannot fill the audit log.
- Every answered or refused `/me` is a `modbot.discord.command` fact, like every other command: who
  looked at their own record is an access record too.
- "What Modbot keeps" writes nothing; it is the same list for everyone.

### 6.1 Event invites (added 2026-10-01)

A third button, **Get event invites** or **Stop event invites** (whichever changes what the member
has), and an **Event invites** On/Off line on the card. It is the only way a member asks to be
invited to the group's events: an event that invites a saved list sends nothing to anybody on it who
did not ask (calendar auto-invite design §2.1). The choice is kept in `event_invite_choice` with the
VRChat account linked at the time and when it was made; each change is a
`modbot.calendar.invites.on` / `.off` fact about the member, with no name in it; a second press of
the same choice records nothing. The button follows the same switch and the same per-person limit
as the other two.

## 7. Risks and guards

| Risk | Guard |
|---|---|
| A public surface for every member | Discord only, private replies only, off by default, not registered while off |
| Showing other members' data | only rows about the caller are read; no co-presence, reporters, notes, flags or evidence |
| Helping ban evasion | standing is "Good" or "Banned"; no reason, rule or case file |
| Staff text reaching a member | nothing a moderator wrote is read |
| Flooding the staff with requests | one open request per account, 20 in any 24 hours across the server, 5 uses a minute per account |
| Promising more deletion than the purge does | the reply says case files are kept |
| Demo mode serves everyone as an administrator | the bot never runs in a demo; the switch refuses to turn on there; the command checks too |
| Buttons from other bots | only presses whose id starts `modbot:` are answered |

## 8. Not in this version

- Appeals (M5 §8 still stands for them).
- A reason written for the member, for a banned member's standing.
- Downloading one's own data.
- A web page for members.
