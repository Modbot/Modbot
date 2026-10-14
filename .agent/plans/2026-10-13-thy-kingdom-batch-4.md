# Thy Kingdom batch 4: build plan (2026-10-13)

Ideas, findings and the user's answers: `.agent/research/2026-10-13-thy-kingdom-ideas-batch-4.md`.
Checked against origin/staging a0db4d02. Each step is one builder (delegate skill, Sonnet 5.5 High).
Idea 5 (scheduled group posts) needs nothing built: Marketing already does it. Idea 4 also covers batch 5 #5.

## New from VRChat / Discord / outside services

| Call | New? | Notes |
|---|---|---|
| VRChat calendar event "interested" count (step D) | No | Field on the calendar event `CalendarVRChatReader` already reads. |
| Discord: read invites + their use counts, vanity invite, invite created/deleted events, create invite (step A) | **Yes (Discord)** | Needs Manage Server (create: Create Invite). Discord.Net handles Discord's own limits. |
| Discord: scheduled event "interested" count (step D) | No | On the scheduled event Modbot already reads. |
| Discord: send a DM (steps B, F) | No | DM sender exists. B's one-time nudge is sent slowly (one DM every 30 s at most). |
| Discord: edit role / channel permissions (step G restore) | **Yes (Discord)** | Needs Manage Roles / Manage Channels. Only on staff's press of Restore. |
| Ko-fi webhook, inbound (step H1) | **Yes (outside)** | Ko-fi posts to Modbot; checked with the server's Ko-fi verification token. No outgoing calls. |
| Patreon webhooks inbound + Patreon API (step H2) | **Yes (outside)** | Webhook signature check. API rate limit: look it up (§4.3.4); if not found, 1 request per 10 s, marked NOT MEASURED. |

No new VRChat endpoint.

## Steps

| Step | Idea | What | Migration | Size |
|---|---|---|---|---|
| A | 1 | Invite tracking: record which invite each new Discord member used (compare use counts on join). Staff name each invite (e.g. "Linktree", "Partner: X"); a page shows joins per source over time. Modbot can create the per-source invites from a list of names. The vanity invite counts as its own source. | Yes | Medium |
| B | 2 | Account links: a view of what share of VRChat group members have a linked Discord account. One slow one-time DM to unlinked Discord members with the `/verifyaccount` link (staff start it; sent once per person, ever). A "Link your VRChat" button in the welcome message. Enough to replace the separate VRChat Linking bot. | Yes | Small–medium |
| C | 3 | Voting (covers batch 2 step K): `/vote` or a Modbot page creates a vote with any number of options, Discord buttons, no time limit (staff close it), optionally limited to a role, one vote per person (can change it), results kept in Modbot. | Yes | Medium |
| D | 4 (+ batch 5 #5) | Event series: per-series page showing Discord interested, VRChat interested and people who actually came, per date, over time. Monthly summary post in a chosen staff channel. No drop alert. Interested counts are stored once per date (taken when the event starts). | Yes | Medium |
| E | 6 | New-staff tracker: a checklist on each new staff member's profile with two kinds of items, set per server: tasks the trial mod fills in themselves (written answers) and items their buddy signs off. Day-30 reminder in a staff channel (checklist, events worked, buddy note) for the Council vote. 30-day inactive check-in reminder for all staff. | Yes | Medium–large |
| F | 7 | Report updates: when closing a report, staff can send the reporter a DM: a preset message (set per server), their own text, or skip. Never posted in a channel. | Yes (presets) | Small–medium |
| G | 8 | Server snapshots: roles, channels and permissions saved on a schedule the server picks (e.g. monthly) or by hand. What-changed view between two snapshots. Restore one role's or one channel's permissions from a snapshot. Download a snapshot as a file. | Yes | Medium |
| H1 | 9 | Ko-fi: inbound webhook with the verification token. Each tier maps to a Discord role and a VRChat group role; thank-you message; role removed when a monthly payment hasn't come 3 days after the month is up (Ko-fi sends no "stopped" event). Match the supporter by Discord account (Ko-fi's Discord link) or by a code they give staff. | Yes | Medium |
| H2 | 9 | Patreon on top of H1: webhooks (pledge created / updated / deleted) with signature check, same tier → role mapping, same thank-you; role removed when the pledge ends. Together with H1 replaces the Ko-fi and Patreon bots. | Yes | Medium |

## Shared files (rebase carefully)

| File | Steps |
|---|---|
| `src/Modbot.Core/Data/Migrations/ModbotContextModelSnapshot.cs` | all: regenerate the migration after rebasing, never hand-merge the snapshot |
| `src/Modbot.Web/src/lib/nav.ts`, `api.ts` | all |
| `src/Modbot.Discord/Gateway/IDiscordGateway.cs`, `DiscordNetGateway*.cs` | A B C D F G H1 |
| Member-join handling / welcome message | A B (B builds after A) |
| Slash command registration + Commands card | C |
| Settings contracts / Settings page | all |
| Report close flow | F only |
| Supporter tier mapping | H1 writes it, H2 reuses it (H2 builds after H1) |
| `docs/content/docs/` | every step |

## Order (three builders at a time)

1. Wave 1: A, C, F
2. Wave 2: B (on A), D, G
3. Wave 3: E, H1
4. Wave 4: H2 (on H1)

Each builder: compile checks, rebase onto staging, diff review, localhost + Chrome check, push to staging.
Tests written, not run; one testing pass after wave 4 takes CI green on master.
Builder A also commits this plan.

## Notes

- Thy Kingdom's live bot lacks Manage Server and Manage Roles, so invite tracking (A), role steps (H) and
  restore (G) fail there until they are given; each card shows the missing permission.
- Ko-fi and Patreon keys are secrets: stored like other server secrets, never shown back in full.
