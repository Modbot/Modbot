# Thy Kingdom ideas, batch 4 (2026-10-13): research only

Sent by the Thy Kingdom Driver session on 2026-10-13. The user picked these. The same rules as
batches 2 and 3 apply: check what Modbot already has, research each idea, and build nothing until
the user OKs a plan. Keep each feature generic, with Thy Kingdom as the first user.

1. **Invite tracking:** show which link brought each new member. The links are the vanity invite
   (discord.gg/thykingdom), the Linktree (thykingdom.vip/links), the VRChat group link and partner
   links.
2. **Account-link nudges:** show what share of VRChat group members have linked a Discord account,
   and gently nudge the rest. The group has about 4,822 members and the Discord about 754. Check
   whether this could replace the separate "VRChat Linking" bot.
3. **Generic voting:** members vote on options. The user's wording is "generic voting", not
   movie-only. Thy Kingdom's first use is picking the next Watch Party film. Watch Party is their
   most popular event: 9.1 interested on average, with peaks of 85.
4. **Event series health:** track interest in each event series over time, and alert when one keeps
   dropping. Saturday hangouts average 2 to 4 interested. Pairs with batch 2 #4 (regulars and
   return rate).
5. **Scheduled group posts:** queue VRChat group posts and announcements for a set time.
6. **New-staff tracker:** a first-week checklist with sign-off, plus a reminder for the Council vote
   when the 30-day trial ends. Context: `G:\Thy Kingdom\handbook-draft.md` §6 and §19.
7. **Updates for people who report:** tell the reporter when their report has been handled. The
   handbook says to tell the people involved the outcome.
8. **Server backup and diff:** snapshot roles, channels and permissions, and show what changed. The
   user wants it monthly or on a schedule they choose, not fixed weekly.
9. **Supporter perks sync:** Ko-fi tiers give matching roles in Discord and VRChat (the group has a
   "Supporter" role), plus a thank-you. Thy Kingdom has 9 Ko-fi tiers. Check whether this could
   replace the separate Ko-fi and Patreon bots.

## Findings (2026-10-13, read from origin/master b703abf0; grep-level)

| # | Already in Modbot | Missing | New endpoint? | Size |
|---|---|---|---|---|
| 1 | Discord join facts; VRChat invite-create facts; Modbot's own app invites | Invite codes/use counts never read; Linktree/VRChat link can't be traced without per-source links | Discord invites + vanity reads, INVITE_CREATE/DELETE (needs Manage Server) | Medium |
| 2 | DiscordAccountLink + /verify; linked 18+ role; "Linked" count in server analytics | Share-linked view for the VRChat group; nudges | No (DM sender exists) | Small |
| 3 | Nothing (giveaway reaction counting reusable) | Votes table, tally, UI | Discord polls (if native) | Medium |
| 4 | Calendar events/occurrences; alert watchers | Interest counts never stored (Discord interested, VRChat) | Discord scheduled-event users read; VRChat side unknown | Medium |
| 5 | Marketing schedules VRChat group posts (PostVRChatSender) — already covered | Nothing found missing (not read in depth) | No | None/small |
| 6 | Staff roles, ModbotUser.CreatedAt, notification channels | Checklist, sign-off, staff trial, Council vote reminder | No | Medium–large |
| 7 | Reports open/closed with closer and required close note | Reporter is never told (privacy rule: reporter id never sent to Discord) | No (DM sender exists) | Small–medium |
| 8 | Current server index + audit-log facts for role/channel changes | Snapshots over time, diff view, schedule | No | Medium |
| 9 | Nothing for Ko-fi/Patreon; webhooks outbound only; list roles reusable | Inbound webhook with signature, tier→role mapping, thank-you | Inbound Ko-fi webhook (new) | Medium–large |

## User's answers
- 1: B (2026-10-13): record which invite each new Discord member used; staff name each invite (Linktree, Partner: X); a page shows joins per source; Modbot can also create the per-source invites from a list of names. Needs Manage Server.
- 2: C (2026-10-13): linked-share view; one slow one-time DM to unlinked Discord members with the /verify link; 'Link your VRChat' button in the welcome message; Modbot replaces the VRChat Linking bot.
- 3: B (2026-10-13): voting with Modbot buttons (any number of options, no time limit, can be limited to a role, results kept in Modbot). Covers the held batch-2 polls step K.
- 4: A + C, not B (2026-10-13): per-series page (Discord interested, VRChat interested, actually came, over time) plus a monthly summary post to a staff channel; no drop alert. Built together with batch 5 #5.
- 5: B (2026-10-13): nothing to build now; Marketing already schedules VRChat group, Discord and Bluesky posts. Driver gets a how-to; look for gaps once Thy Kingdom has used it (later batch).
- 6: C + trial tasks (2026-10-13): checklist on each new staff member's profile with two kinds of items: tasks the trial mod does and fills out themselves (written answers), and items the buddy signs off; staff-channel reminder at day 30 (checklist, events worked, buddy note) for the Council vote; 30-day inactive check-in reminders for all staff. Checklist items set per server.
- 7: C with presets (2026-10-13): when closing a report, staff can send the reporter a DM; they pick a preset message (set per server) or write their own, or skip. Never posted in a channel.
- 8: C (2026-10-13): scheduled snapshots of roles, channels, permissions (schedule chosen by the server, e.g. monthly); what-changed view; restore one role's or channel's permissions from a snapshot (needs Manage Roles/Channels); download a snapshot as a file.
- 9: B (2026-10-13): Ko-fi and Patreon: each tier maps to a Discord role and a VRChat group role, thank-you message, role removed when a monthly payment stops; replaces both bots. New: inbound Ko-fi webhook (verification token) and Patreon webhooks/API (new outside service; check its limits).
